import os
import sys
import psutil
import logging
import tempfile
import traceback
import subprocess
import shutil
import time
import threading
import weakref
import json
from PyQt5.QtCore import QTimer, QThread, QObject, pyqtSignal, QCoreApplication, Qt
from system import diagnostic_runtime
try:
    import sip
except ImportError:
    sip = None
if sys.platform == 'win32':
    _base_dir = os.path.abspath(os.path.join(os.path.dirname(__file__), '..'))
    _bin_dir = os.path.join(_base_dir, 'binaries')
    if os.path.exists(_bin_dir):
        os.environ["MPV_HOME"] = _bin_dir
        os.environ["MPV_DYLIB_PATH"] = os.path.join(_bin_dir, "libmpv-2.dll")
        if hasattr(os, 'add_dll_directory'):
            try: os.add_dll_directory(_bin_dir)
            except: pass
        os.environ['PATH'] = _bin_dir + os.pathsep + os.environ.get('PATH','')
_VALID_SEEK_PRECISIONS = ('unused', 'default-precise', 'keyframes', 'exact')
_qt_dispatcher_lock = threading.RLock()
_qt_dispatcher = None

class _QtCallbackDispatcher(QObject):
    callback_signal = pyqtSignal(object)

    def __init__(self):
        super().__init__()
        self.callback_signal.connect(self._run, Qt.QueuedConnection)

    def _run(self, callback):
        try:
            if callable(callback):
                callback()
        except Exception as e:
            diagnostic_runtime.append_python_debug_throttled(
                f"qt-callback-dispatcher:{type(e).__name__}",
                f"QT CALLBACK DISPATCH ERROR | {e}"
            )

def _get_qt_dispatcher():
    global _qt_dispatcher
    app = QCoreApplication.instance()
    if app is None:
        return None
    with _qt_dispatcher_lock:
        if _qt_dispatcher is None:
            _qt_dispatcher = _QtCallbackDispatcher()
        if _qt_dispatcher.thread() != app.thread():
            _qt_dispatcher.moveToThread(app.thread())
        return _qt_dispatcher

def _dispatch_on_qt_thread(callback):
    app = QCoreApplication.instance()
    if app is None:
        threading.Timer(0, callback).start()
        return True
    try:
        if QThread.currentThread() == app.thread():
            QTimer.singleShot(0, callback)
        else:
            dispatcher = _get_qt_dispatcher()
            if dispatcher is None:
                threading.Timer(0, callback).start()
            else:
                dispatcher.callback_signal.emit(callback)
        return True
    except Exception:
        try:
            threading.Timer(0, callback).start()
            return True
        except Exception:
            return False
try:
    import mpv
    if not hasattr(mpv, "_mandate_applied"):
        _real_init = mpv.MPV.__init__

        def __mandated_mpv_init__(self, *args, **kwargs):
            kwargs = diagnostic_runtime.apply_mpv_runtime_overrides(dict(kwargs))
            kwargs.pop('log_file', None)
            kwargs.pop('log-file', None)
            kwargs.setdefault('log_handler', diagnostic_runtime.append_mpv_trace)
            kwargs.pop('start_event_thread', None)
            if not diagnostic_runtime.is_isolation_active():
                kwargs.setdefault('msg_level', 'all=info')
            _real_init(self, *args, **kwargs)
            self._seek_state_lock = threading.RLock()
            self._seeking_active = False
            self._seek_guard_start_mono = 0.0
            self._pending_seek_target = None
            self._tracked_event_callbacks = []
            self._tracked_property_observers = []
            _real_seek = self.seek

            def __submit_gated_seek(target, reference='absolute', precision='exact'):
                if getattr(self, '_safe_shutdown_initiated', False) or getattr(self, '_core_shutdown', False):
                    return False
                if precision not in _VALID_SEEK_PRECISIONS:
                    precision = 'keyframes'
                now = time.monotonic()
                with self._seek_state_lock:
                    if self._seeking_active and (now - self._seek_guard_start_mono) > 2.0:
                        self._seeking_active = False
                        diagnostic_runtime.append_python_debug("SEEK GUARD FALLBACK TRIGGERED | Event lost or timeout reached")
                    if self._seeking_active:
                        self._pending_seek_target = (target, reference, precision)
                        return True
                    self._seeking_active = True
                    self._seek_guard_start_mono = now
                    self._pending_seek_target = None
                try:
                    if diagnostic_runtime.is_isolation_active():
                        try:
                            with MPVSafetyManager._mpv_command_lock:
                                self.set_property('vo', 'null')
                                self.set_property('hwdec', 'no')
                        except: pass
                    with MPVSafetyManager._mpv_command_lock:
                        _real_seek(target, reference=reference, precision=precision)
                    return True
                except Exception:
                    with self._seek_state_lock:
                        self._seeking_active = False
                        self._pending_seek_target = None
                    return False

            def __clear_seek_guard(reason='unknown', dispatch_pending=True):
                with self._seek_state_lock:
                    was_active = self._seeking_active
                    self._seeking_active = False
                    self._seek_guard_start_mono = 0.0
                    pending = self._pending_seek_target
                    self._pending_seek_target = None
                if was_active:
                    diagnostic_runtime.append_python_debug_throttled(
                        f"mpv-seek-guard-reset:{id(self)}:{reason}",
                        f"MPV SEEK GUARD RESET | reason={reason} | player_id={id(self)}"
                    )
                if dispatch_pending and pending is not None:
                    t, r, p = pending
                    _dispatch_on_qt_thread(lambda: __submit_gated_seek(t, reference=r, precision=p))

            def __schedule_clear_seek_guard(reason='unknown'):
                _dispatch_on_qt_thread(lambda: __clear_seek_guard(reason))
            self._submit_gated_seek = __submit_gated_seek
            self._clear_seek_guard = __clear_seek_guard
            self._discard_stale_seek_guard = lambda reason='stale': __clear_seek_guard(reason, dispatch_pending=False)
            @self.event_callback('playback-restart')
            def __on_playback_restart(ev):
                if getattr(self, '_safe_shutdown_initiated', False) or getattr(self, '_core_shutdown', False):
                    return
                __schedule_clear_seek_guard('playback-restart')
            self._tracked_event_callbacks.append(('playback-restart', __on_playback_restart))
            @self.event_callback('seek')
            def __on_seek(ev):
                pass
            @self.property_observer('seeking')
            def __on_seeking_change(_name, value):
                if getattr(self, '_safe_shutdown_initiated', False) or getattr(self, '_core_shutdown', False):
                    return
                if value in (False, None, 0):
                    __schedule_clear_seek_guard('seeking=false')
            self._tracked_property_observers.append(('seeking', __on_seeking_change))

            def __seek_watchdog():
                while not getattr(self, '_core_shutdown', False) and not getattr(self, '_safe_shutdown_initiated', False):
                    time.sleep(0.5)
                    if not getattr(self, '_seeking_active', False):
                        continue
                    try:
                        if (time.monotonic() - getattr(self, '_seek_guard_start_mono', 0.0)) > 2.5:
                             __schedule_clear_seek_guard('watchdog-timeout')
                    except:
                        pass
            t = threading.Thread(target=__seek_watchdog, daemon=True)
            t.start()
            if not diagnostic_runtime.is_isolation_active():
                self._gpu_post_init_applied = False
                @self.event_callback('playback-restart')
                def __apply_gpu_post_init(ev):
                    def __apply_gpu_properties():
                        if getattr(self, '_gpu_post_init_applied', False): return
                        self._gpu_post_init_applied = True
                        if getattr(self, '_safe_shutdown_initiated', False) or getattr(self, '_core_shutdown', False): return
                        try:
                            with MPVSafetyManager._mpv_command_lock:
                                self.set_property('d3d11-exclusive-fs', 'yes')
                                self.set_property('gpu-async-compute', 'yes')
                                self.set_property('gpu-stream-cache-secs', '10')
                        except Exception: pass
                    _dispatch_on_qt_thread(__apply_gpu_properties)
                self._tracked_event_callbacks.append(('playback-restart', __apply_gpu_post_init))
            diagnostic_runtime.log_isolation_alarm(logging.getLogger())
        mpv.MPV.__init__ = __mandated_mpv_init__
        mpv._mandate_applied = True
        if diagnostic_runtime.is_isolation_active():
            logging.warning("MPV DIAGNOSTIC ISOLATION ACTIVE. CPU-ONLY MODE ENABLED FOR SEEK CRASH TRIAGE.")
        else:
            logging.info("GPU RE-INTEGRATION COMPLETE. RTX 4070 OPTIMIZATIONS ACTIVE.")
except (ImportError, OSError):
    class MockMPV:
        log_path = None

        class MPV:
            def __init__(self, *args, **kwargs): pass

            def terminate(self): pass
    mpv = MockMPV()

from system.live_logging import (
    ReopenableFileHandler,
    ReopenableTextStream,
    restoreable_original_stdout,
    restoreable_original_stderr,
    start_log_pipe_broker,
    start_touch_heartbeat,
    touch_unlocked,
)

from typing import Optional, Tuple

class DependencyDoctor:
    @staticmethod
    def get_bin_dir(base_dir: str) -> str: return os.path.join(base_dir, 'binaries')
    @staticmethod
    def check_ffmpeg(base_dir: str) -> Tuple[bool, str, str]:
        bin_dir = DependencyDoctor.get_bin_dir(base_dir)
        f_exe = "ffmpeg.exe" if sys.platform == "win32" else "ffmpeg"
        p_exe = "ffprobe.exe" if sys.platform == "win32" else "ffprobe"
        f_p = os.path.join(bin_dir, f_exe)
        p_p = os.path.join(bin_dir, p_exe)
        if os.path.exists(f_p) and os.path.exists(p_p): return True, f_p, ""
        s_f = shutil.which(f_exe); s_p = shutil.which(p_exe)
        if s_f and s_p: return True, s_f, ""
        return False, "", "FFmpeg or FFprobe binaries are missing."

class ProcessManager:
    @staticmethod
    def kill_orphans(process_names: list = ["ffmpeg.exe", "ffprobe.exe", "mpv.exe", "ffplay.exe"]):
        my_pid = os.getpid()
        base_dir = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
        bin_dir = os.path.join(base_dir, 'binaries')
        for proc in psutil.process_iter(['pid', 'name', 'exe', 'cmdline', 'ppid']):
            try:
                if proc.info['pid'] == my_pid: continue
                name = (proc.info['name'] or "").lower()
                target_names = [n.lower() for n in process_names]
                if name in target_names or any(tn in name for tn in target_names):
                    p_e = proc.info.get('exe'); cmd = " ".join(proc.info.get('cmdline') or [])
                    is_our = p_e and bin_dir.lower() in os.path.abspath(p_e).lower()
                    is_proj = base_dir.lower() in cmd.lower()
                    if is_our or is_proj:
                        try: proc.kill()
                        except: pass
            except: pass
    @staticmethod
    def start_parent_watchdog():
        p_pid = os.getppid()
        if p_pid <= 1: return

        def watchdog():
            while True:
                try:
                    if not psutil.pid_exists(p_pid): break
                    p = psutil.Process(p_pid)
                    if p.status() == psutil.STATUS_ZOMBIE: break
                except: break
                time.sleep(2.0)
            ProcessManager.kill_orphans()

            from PyQt5.QtWidgets import QApplication
            if QApplication.instance(): QApplication.instance().quit()
            else: sys.exit(1)
        t = threading.Thread(target=watchdog, daemon=True)
        t.start()
    @staticmethod
    def cleanup_temp_files(prefix: str = "fvs_"):
        t_d = tempfile.gettempdir(); pats = [prefix, "fvs_job_"]
        try:
            for n in os.listdir(t_d):
                if any(n.startswith(p) for p in pats):
                    p_h = os.path.join(t_d, n)
                    try:
                        if os.path.isfile(p_h): os.remove(p_h)
                        elif os.path.isdir(p_h): shutil.rmtree(p_h, ignore_errors=True)
                    except: pass
        except: pass
        try:
            p_r = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
            for r, ds, fs in os.walk(p_r):
                if "__pycache__" in ds:
                    try: shutil.rmtree(os.path.join(r, "__pycache__"))
                    except: pass
        except: pass
    @staticmethod
    def acquire_pid_lock(app_name: str) -> Tuple[bool, Optional[object]]:
        p_f = os.path.join(tempfile.gettempdir(), f"{app_name}.pid")
        for i in range(3):
            try:
                f = open(p_f, "a+")
                f.seek(0)
                if sys.platform == "win32":
                    import msvcrt
                    msvcrt.locking(f.fileno(), msvcrt.LK_NBLCK, 1)
                else:
                    import fcntl
                    fcntl.lockf(f, fcntl.LOCK_EX | fcntl.LOCK_NB)
                f.seek(0); f.truncate(); f.write(str(os.getpid())); f.flush()
                return True, f
            except:
                if i < 2: time.sleep(0.2)
                continue
        return False, None

class FlushHandler(logging.Handler):
    def __init__(self, handler):
        super().__init__()
        self.handler = handler

    def emit(self, record):
        self.handler.emit(record)
        self.handler.flush()

class UIManager:
    @staticmethod
    def style_and_size_msg_box(msg_box: 'QMessageBox', copy_text: str, copy_btn_label: str = "Copy to Clipboard"):
        from PyQt5.QtWidgets import QSpacerItem, QSizePolicy, QGridLayout, QApplication
        from PyQt5.QtCore import Qt, QTimer, QUrl
        from PyQt5.QtGui import QDesktopServices
        l = msg_box.layout()
        if isinstance(l, QGridLayout):
            l.addItem(QSpacerItem(800, 500, QSizePolicy.Minimum, QSizePolicy.Expanding), l.rowCount(), 0, 1, l.columnCount())
        c_b = msg_box.addButton(copy_btn_label, msg_box.ActionRole)
        try:
            from app import tr
            r_l = tr("report_to_alon")
        except: r_l = "Report Error"
        r_b = msg_box.addButton(r_l, msg_box.ActionRole)

        def on_copy():
            if sip and sip.isdeleted(c_b): return
            QApplication.clipboard().setText(copy_text)
            c_b.setText("✓ Copied!")

            def reset_text():
                if sip and not sip.isdeleted(c_b): c_b.setText(copy_btn_label)
            QTimer.singleShot(2000, reset_text)

        def on_report():
            sub = "[Fortnite Video Software] Error Report"
            body = f"The following error occurred:\n\n{copy_text}"

            import urllib.parse
            m_u = f"mailto:AlonR@Bynet.co.il?subject={urllib.parse.quote(sub)}&body={urllib.parse.quote(body)}"
            QDesktopServices.openUrl(QUrl(m_u))
            msg_box.close()
        c_b.clicked.connect(on_copy); r_b.clicked.connect(on_report)
        for b in msg_box.buttons(): b.setCursor(Qt.PointingHandCursor)

class ConsoleManager:
    _log_files = []
    _stdout_stream = None
    _stderr_stream = None
    _log_broker = None
    _log_broker_stdin = None
    _log_touch_stop = None
    @staticmethod
    def _source_tag(name: str) -> str:
        m = {"Main_App": "main_app", "Crop_Tool": "crop_tools", "Advanced_Editor": "advanced_editor"}
        return m.get(str(name), str(name).strip().lower())
    @staticmethod
    def initialize(base_dir: str, log_filename: str, logger_name: str):
        # Sanitize root logger immediately to prevent pipe errors during early boot or late exit
        root = logging.getLogger()
        for h in root.handlers[:]:
            if isinstance(h, logging.StreamHandler) and not isinstance(h, SafeStreamHandler):
                try:
                    root.removeHandler(h)
                    sc = SafeStreamHandler(h.stream)
                    sc.setFormatter(h.formatter)
                    root.addHandler(sc)
                except: pass

        app_p = logger_name.lower().replace(" ", "_")
        f_l_n = f"{app_p}_{log_filename}" if not log_filename.startswith(app_p) else log_filename
        logger = LogManager.setup_logger(base_dir, f_l_n, logger_name)
        try:
            l_d = os.path.join(base_dir, "logs")
            os.makedirs(l_d, exist_ok=True)
            python_debug_log_path = diagnostic_runtime.get_python_debug_log_path()
            mpv_trace_log_path = diagnostic_runtime.get_mpv_trace_log_path()
            ConsoleManager._stdout_stream = ReopenableTextStream(
                python_debug_log_path, "stdout", restoreable_original_stdout()
            )
            ConsoleManager._stderr_stream = ReopenableTextStream(
                python_debug_log_path, "stderr", restoreable_original_stderr()
            )
            try:
                import faulthandler
                faulthandler.enable()
            except: pass
            sys.stdout = ConsoleManager._stdout_stream
            sys.stderr = ConsoleManager._stderr_stream
            logger.info("STABLE LOGGING ACTIVE - PIPE BROKER DISABLED")
            logger.info(f"PYTHON DEBUG LOG REDIRECT ACTIVE -> {python_debug_log_path}")
            logger.info(f"MPV TRACE LOG REDIRECT ACTIVE -> {mpv_trace_log_path}")
            diagnostic_runtime.log_isolation_alarm(logger)
        except Exception as e:
            logger.error(f"Failed to setup stable logging: {e}")

        def exc_h(t, v, tb):
            if issubclass(t, KeyboardInterrupt):
                sys.__excepthook__(t, v, tb)
                return
            msg = "".join(traceback.format_exception(t, v, tb))
            logger.critical(f"UNCAUGHT EXCEPTION:\n{msg}")
            try:
                from PyQt5.QtWidgets import QMessageBox, QApplication
                if QApplication.instance():
                    m_b = QMessageBox(None)
                    m_b.setIcon(QMessageBox.Critical); m_b.setWindowTitle("Critical Error")
                    m_b.setText(f"An unexpected error occurred.\n\n{v}\n\nDetails saved to log.")
                    UIManager.style_and_size_msg_box(m_b, msg)
                    m_b.exec_(); QApplication.instance().quit()
                else: sys.exit(1)
            except: sys.exit(1)
        sys.excepthook = exc_h
        if sys.platform == "win32":
            try:
                import ctypes
                hwnd = ctypes.windll.kernel32.GetConsoleWindow()
                if hwnd != 0: ctypes.windll.user32.ShowWindow(hwnd, 0)
            except: pass

        import atexit

        def close_logs():
            for stream in (ConsoleManager._stdout_stream, ConsoleManager._stderr_stream):
                try: stream.flush()
                except: pass
            try:
                if ConsoleManager._log_broker_stdin is not None:
                    ConsoleManager._log_broker_stdin.flush()
                    ConsoleManager._log_broker_stdin.close()
            except Exception:
                pass
            try:
                if ConsoleManager._log_touch_stop is not None:
                    ConsoleManager._log_touch_stop.set()
            except Exception:
                pass
        atexit.register(close_logs)
        return logger

class SafeStreamHandler(logging.StreamHandler):
    def emit(self, record):
        try:
            if self.stream and not getattr(self.stream, 'closed', False):
                super().emit(record)
        except (OSError, ValueError):
            pass
        except Exception:
            pass

    def flush(self):
        try:
            if self.stream and hasattr(self.stream, "flush") and not getattr(self.stream, 'closed', False):
                self.stream.flush()
        except (OSError, ValueError):
            pass
        except Exception:
            pass

    def handleError(self, record):
        pass

class LogManager:
    @staticmethod
    def setup_logger(base_dir: str, log_filename: str, logger_name: str) -> logging.Logger:
        logger = logging.getLogger(logger_name)
        if logger.handlers: return logger
        logger.setLevel(logging.INFO)
        l_d = os.path.join(base_dir, "logs")
        os.makedirs(l_d, exist_ok=True)
        l_p = os.path.join(l_d, log_filename)
        h = ReopenableFileHandler(l_p, maxBytes=5*1024*1024, backupCount=3, encoding='utf-8')
        h.setFormatter(logging.Formatter('%(asctime)s - %(name)s - %(levelname)s - %(message)s'))
        logger.addHandler(h)
        c = SafeStreamHandler(sys.stdout)
        c.setFormatter(logging.Formatter('%(asctime)s - %(name)s - %(levelname)s - %(message)s'))
        logger.addHandler(c)
        logger.propagate = False
        return logger

class MPVSafetyManager:
    _mpv_creation_lock = threading.Lock()
    _mpv_command_lock = threading.RLock()
    _last_creation_time = 0
    _instances = weakref.WeakSet()
    _stale_seek_timeout_sec = 1.25
    @staticmethod
    def run_on_qt_thread(callback):
        return _dispatch_on_qt_thread(callback)
    @staticmethod
    def reset_stale_seek_guard(player, reason="operation"):
        if not player:
            return False
        try:
            if not getattr(player, '_seeking_active', False):
                return False
            started = float(getattr(player, '_seek_guard_start_mono', 0.0) or 0.0)
            if started <= 0:
                return False
            age = time.monotonic() - started
            if age < MPVSafetyManager._stale_seek_timeout_sec:
                return False
            discard = getattr(player, '_discard_stale_seek_guard', None)
            if callable(discard):
                discard(f"{reason}-timeout")
            else:
                player._seeking_active = False
                player._seek_guard_start_mono = 0.0
                player._pending_seek_target = None
            diagnostic_runtime.append_python_debug_throttled(
                f"mpv-stale-seek-reset:{id(player)}:{reason}",
                f"MPV STALE SEEK RESET | reason={reason} | age={age:.2f}s | player_id={id(player)}",
                min_interval_sec=1.0
            )
            return True
        except Exception:
            return False
    @staticmethod
    def log_mpv_diagnostics(player, logger, context_tag="GENERAL"):
        if not player or not logger: return
        try:
            p_id = id(player); h = getattr(player, 'handle', 'UNKNOWN'); s = getattr(player, '_core_shutdown', False)
            try:
                p = getattr(player, 'path', 'NONE'); pz = getattr(player, 'pause', 'UNKNOWN'); t = getattr(player, 'time_pos', 'UNKNOWN')
            except: p, pz, t = "UNREADABLE", "UNREADABLE", "UNREADABLE"
            logger.info(f"DIAGNOSTICS [{context_tag}]: MPV Object ID={p_id} | Handle={h} | CoreShutdown={s} | Path={p} | Paused={pz} | Time={t}")
        except Exception as e: logger.error(f"DIAGNOSTICS [{context_tag}]: Failed to extract MPV state: {e}")
    @staticmethod
    def safe_mpv_shutdown(player, timeout=2.0, lock=None):
        if not player: return True

        import mpv
        try:
            if getattr(player, '_core_shutdown', False) or getattr(player, '_safe_shutdown_initiated', False): return True
            player._safe_shutdown_initiated = True
            MPVSafetyManager.cleanup_mpv_event_callbacks(player)

            def _do_shutdown():
                try:
                    with MPVSafetyManager._mpv_command_lock:
                        player.pause = True
                        player.stop()
                        player.terminate()
                except: pass
            t = threading.Thread(target=_do_shutdown, daemon=True)
            t.start()
            c_t = min(0.5, timeout); s_t = time.time()
            while time.time() - s_t < c_t:
                try:
                    if getattr(player, '_core_shutdown', True): break
                except: break
                time.sleep(0.05)
            return True
        except: return False
    @staticmethod
    def safe_mpv_set(player, property_name, value, max_attempts=3, lock=None):
        if not player or getattr(player, '_core_shutdown', False) or getattr(player, '_safe_shutdown_initiated', False): return False
        MPVSafetyManager.reset_stale_seek_guard(player, f"set:{property_name}")
        for i in range(max_attempts):
            try:
                with MPVSafetyManager._mpv_command_lock:
                    try: setattr(player, property_name, value); return True
                    finally: pass
            except: time.sleep(0.02)
        return False
    @staticmethod
    def safe_mpv_get(player, property_name, default=None, max_attempts=3, lock=None):
        if not player or getattr(player, '_core_shutdown', False) or getattr(player, '_safe_shutdown_initiated', False): return default
        MPVSafetyManager.reset_stale_seek_guard(player, f"get:{property_name}")
        for i in range(max_attempts):
            try:
                with MPVSafetyManager._mpv_command_lock:
                    try: return getattr(player, property_name, default)
                    finally: pass
            except: time.sleep(0.02)
        return default
    @staticmethod
    def safe_mpv_command(player, command, *args, max_attempts=3, lock=None):
        if not player or getattr(player, '_core_shutdown', False) or getattr(player, '_safe_shutdown_initiated', False): return False
        MPVSafetyManager.reset_stale_seek_guard(player, f"command:{command}")
        if command == "seek":
            submit_seek = getattr(player, '_submit_gated_seek', None)
            if callable(submit_seek):
                target = float(args[0]) if len(args) > 0 else 0.0
                reference = args[1] if len(args) > 1 else 'relative'
                precision = args[2] if len(args) > 2 else 'exact'
                return bool(submit_seek(target, reference=reference, precision=precision))
        for i in range(max_attempts):
            try:
                with MPVSafetyManager._mpv_command_lock:
                    try:
                        if command == "seek":
                            target = float(args[0]) if len(args) > 0 else 0.0
                            reference = args[1] if len(args) > 1 else 'relative'
                            precision = args[2] if len(args) > 2 else 'exact'
                            if hasattr(player, 'seek'):
                                player.seek(target, reference=reference, precision=precision)
                            elif hasattr(player, 'set_time'):
                                player.set_time(int(round(target * 1000.0)))
                            else:
                                player.command(command, *args)
                        else:
                            player.command(command, *args)
                        return True
                    finally: pass
            except:
                if command == "seek":
                    try:
                        clear_seek_guard = getattr(player, '_clear_seek_guard', None)
                        if callable(clear_seek_guard):
                            clear_seek_guard('command-error')
                        else:
                            player._seeking_active = False
                    except Exception:
                        pass
                time.sleep(0.02)
        return False
    @staticmethod
    def cleanup_mpv_event_callbacks(player):
        if not player: return
        try:
            with MPVSafetyManager._mpv_command_lock:
                tracked_observers = list(getattr(player, '_tracked_property_observers', []))
                tracked_callbacks = list(getattr(player, '_tracked_event_callbacks', []))
                unobserve = getattr(player, 'unobserve_property', None)
                if callable(unobserve):
                    for name, handler in tracked_observers:
                        try: unobserve(name, handler)
                        except Exception: pass
                unregister = getattr(player, 'unregister_event_callback', None)
                if callable(unregister):
                    for _name, handler in tracked_callbacks:
                        try: unregister(handler)
                        except Exception: pass
                if hasattr(player, '_tracked_property_observers'):
                    player._tracked_property_observers = []
                if hasattr(player, '_tracked_event_callbacks'):
                    player._tracked_event_callbacks = []
        except Exception: pass
    @staticmethod
    def create_safe_mpv(**kwargs):
        with MPVSafetyManager._mpv_creation_lock:
            elapsed = (time.time() * 1000) - MPVSafetyManager._last_creation_time
            if elapsed < 400:
                QThread.msleep(int(400 - elapsed))
            MPVSafetyManager._last_creation_time = time.time() * 1000
            try:
                import mpv
                l_h = kwargs.pop('log_handler', None)
                if l_h:
                    def s_l_p(lvl, pref, txt):
                        try: l_h(lvl, pref, txt)
                        except: pass
                    kwargs['log_handler'] = s_l_p
                wid = kwargs.pop('wid', None)
                s_k = {'hr_seek': 'yes', 'osc': False, 'ytdl': False, 'load_scripts': False, 'config': False, 'keep_open': kwargs.get('keep_open', 'yes'), 'loglevel': 'error'}
                e_f = kwargs.pop('extra_mpv_flags', [])
                kwargs.pop('load_scripts', None); kwargs.pop('config', None); kwargs.pop('osc', None); kwargs.pop('ytdl', None)
                s_k.update(kwargs)
                if wid is not None:
                    s_k['wid'] = wid
                s_k = diagnostic_runtime.apply_mpv_runtime_overrides(s_k)
                s_k.pop('log_file', None)
                s_k.pop('log-file', None)
                if wid is not None and str(s_k.get('vo', '')).lower() == 'null' and not diagnostic_runtime.is_isolation_active():
                    s_k['vo'] = 'gpu'
                default_log_handler = s_k.pop('log_handler', diagnostic_runtime.append_mpv_trace)
                init_args = {
                    'loglevel': s_k.pop('loglevel', 'trace'),
                    'log_handler': default_log_handler,
                    'msg_level': s_k.pop('msg_level', 'all=trace'),
                    'start_event_thread': s_k.pop('start_event_thread', not diagnostic_runtime.is_isolation_active()),
                    'osc': s_k.pop('osc', False),
                    'ytdl': s_k.pop('ytdl', False),
                    'load_scripts': s_k.pop('load_scripts', False),
                    'config': s_k.pop('config', False)
                }
                if wid is not None:
                    init_args['wid'] = int(wid)
                    s_k.pop('wid', None)
                for k, v in s_k.items():
                    init_args[k.replace('-', '_')] = v
                player = mpv.MPV(**init_args)
                time.sleep(0.1)
                player._safe_shutdown_initiated = False; MPVSafetyManager._instances.add(player)
                for p, v in e_f:
                    try: player.set_property(p, v)
                    except: pass
                return player
            except: return None
    @staticmethod
    def register_global_shutdown_handler():
        import atexit

        def global_shutdown():
            for p in list(MPVSafetyManager._instances):
                try:
                    if hasattr(p, '_safe_shutdown_initiated') and not p._safe_shutdown_initiated:
                        p._safe_shutdown_initiated = True; MPVSafetyManager.safe_mpv_shutdown(p, timeout=1.0)
                except: pass
        atexit.register(global_shutdown)

class AppTransitionManager:
    @staticmethod
    def return_to_main(base_dir, current_window):
        try:
            current_window.close()
            main_script = os.path.join(base_dir, "app.py")
            env = os.environ.copy()
            env["FVS_STATE_TRANSFER_RESTORE"] = "1"
            subprocess.Popen([sys.executable, main_script], cwd=base_dir, env=env, creationflags=subprocess.CREATE_NEW_CONSOLE if sys.platform == "win32" else 0)
            sys.exit(0)
        except Exception as e:
            logging.error(f"Failed to return to main: {e}")

class MediaProber:
    @staticmethod
    def probe_duration(bin_dir, path):
        try:
            ffp = os.path.join(bin_dir, 'ffprobe.exe') if sys.platform == 'win32' else 'ffprobe'
            cmd = [ffp, "-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", path]
            r = subprocess.run(cmd, text=True, capture_output=True, creationflags=0x08000000 if sys.platform == 'win32' else 0)
            return max(0.0, float(r.stdout.strip() or 0.0))
        except: return 0.0
    @staticmethod
    def probe_metadata(bin_dir, path):
        try:
            ffp = os.path.join(bin_dir, 'ffprobe.exe') if sys.platform == 'win32' else 'ffprobe'
            cmd = [ffp, "-v", "error", "-show_entries", "format=duration:stream=duration,width,height", "-of", "json", path]
            r = subprocess.run(cmd, text=True, capture_output=True, creationflags=0x08000000 if sys.platform == 'win32' else 0)
            data = json.loads(r.stdout)
            streams = data.get('streams', [])
            dur = 0.0
            res = "0x0"
            if streams:
                stream = streams[0]
                dur = float(stream.get('duration', 0.0) or 0.0)
                res = f"{stream.get('width', 0)}x{stream.get('height', 0)}"
            if dur <= 0.0:
                fmt = data.get('format', {})
                dur = float(fmt.get('duration', 0.0) or 0.0)
            return dur, res
        except: return 0.0, "0x0"

    @staticmethod
    def probe_volume(bin_dir, path):
        try:
            ffp = os.path.join(bin_dir, 'ffmpeg.exe') if sys.platform == 'win32' else 'ffmpeg'
            cmd = [ffp, "-i", path, "-af", "volumedetect", "-f", "null", "-"]
            r = subprocess.run(cmd, text=True, capture_output=True, creationflags=0x08000000 if sys.platform == 'win32' else 0)
            mean_volume = 0.0
            max_volume = 0.0
            for line in r.stderr.splitlines():
                if "mean_volume:" in line:
                    parts = line.split("mean_volume:")
                    if len(parts) > 1:
                        mean_volume = float(parts[1].split("dB")[0].strip())
                if "max_volume:" in line:
                    parts = line.split("max_volume:")
                    if len(parts) > 1:
                        max_volume = float(parts[1].split("dB")[0].strip())
            return mean_volume, max_volume
        except: return 0.0, 0.0
