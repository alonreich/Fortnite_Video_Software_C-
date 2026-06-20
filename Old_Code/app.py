import sys
import os
import struct
import platform
import faulthandler
sys.dont_write_bytecode = True
os.environ['PYTHONDONTWRITEBYTECODE'] = '1'
BASE_DIR  = os.path.dirname(os.path.abspath(__file__))
BIN_DIR   = os.path.join(BASE_DIR, 'binaries')
if BASE_DIR not in sys.path: sys.path.insert(0, BASE_DIR)
from system.utils import ConsoleManager, ProcessManager, MPVSafetyManager, DependencyDoctor
from system import diagnostic_runtime
from PyQt5.QtWidgets import QApplication, QMessageBox, QProgressDialog, QStyle, QLineEdit, QTextEdit, QPlainTextEdit, QAbstractSpinBox
from PyQt5.QtCore import QCoreApplication, QObject, QThread, pyqtSignal, QTimer, Qt, QLocale, QEvent
from PyQt5.QtGui import QIcon
from ui.styles import UIStyles
logger = ConsoleManager.initialize(BASE_DIR, "main_app.log", "Main_App")
import traceback
import threading
import subprocess, ctypes
NATIVE_FAULT_LOG_HANDLE = None
try:
    os.makedirs(os.path.join(BASE_DIR, "logs"), exist_ok=True)
    NATIVE_FAULT_LOG_HANDLE = open(os.path.join(BASE_DIR, "logs", "native_fault.log"), "a", encoding="utf-8")
    faulthandler.enable(file=NATIVE_FAULT_LOG_HANDLE, all_threads=True)
    logger.info("NATIVE FAULT LOG ACTIVE -> %s", os.path.join(BASE_DIR, "logs", "native_fault.log"))
except Exception as fault_log_err:
    logger.warning("NATIVE FAULT LOG unavailable: %s", fault_log_err)
PID_APP_NAME = "fortnite_video_software_main"
ORIGINAL_PATH = os.environ.get("PATH", "")
DEBUG_ENABLED = "--debug" in sys.argv or os.environ.get("FVS_DEBUG") == "1"
ENCODER_TEST_TIMEOUT = int(os.environ.get("FVS_ENCODER_TIMEOUT", "15"))
FORCE_GPU = os.environ.get("FVS_FORCE_GPU")
LOCALE_CODE = QLocale.system().name().split("_")[0].lower()
HARDWARE_SCAN_DETAILS = {"errors": {}, "timed_out": []}
logger.info("BOOT: Fortnite Video Software initializing...")
TRANSLATIONS = {
    "en": {"app_name": "Fortnite Video Software", "dependency_error_title": "Dependency Error", "dependency_error_text": "FFmpeg or FFprobe is missing or not working.\n\nPlease ensure both 'ffmpeg.exe' and 'ffprobe.exe' are in the 'binaries' folder next to this app.", "dependency_error_open_folder": "Open Binaries Folder", "dependency_error_retry": "Retry", "dependency_error_exit": "Exit", "dependency_error_details": "FFmpeg Path: {ffmpeg}\nFFprobe Path: {ffprobe}\nBundled BIN_DIR added to PATH: {bin_dir}\n\nError:\n{error}", "single_instance_title": "Already Running", "single_instance_text": "The app is already running. Please close the other window before opening another.", "pid_warning_title": "Startup Warning", "pid_warning_text": "The app could not create a temporary PID file. It will still run, but single-instance detection might be limited.", "hardware_scan_title": "Hardware Scan", "hardware_scan_text": "Detecting hardware acceleration...", "hardware_scan_done": "Hardware mode detected: {mode}", "hardware_scan_cpu": "Hardware acceleration not available. Using CPU mode.", "hardware_scan_cpu_details": "GPU encoders not detected. You can update your graphics driver and try again.", "ffmpeg_path_message": "Using FFmpeg: {ffmpeg}", "diagnostics_title": "Diagnostics", "copy_to_clipboard": "Copy to Clipboard", "report_to_alon": "Report To Alon Reich"},
    "he": {"app_name": "תוכנת וידאו פורטנייט", "dependency_error_title": "שגיאת תלות", "dependency_error_text": "FFmpeg או FFprobe חסרים או לא עובדים.\n\nודא שהקבצים 'ffmpeg.exe' ו-'ffprobe.exe' נמצאים בתיקיית 'binaries' ליד האפליקציה.", "dependency_error_open_folder": "פתח תיקיית Binaries", "dependency_error_retry": "נסה שוב", "dependency_error_exit": "יציאה", "dependency_error_details": "נתיב FFmpeg: {ffmpeg}\nנתיב FFprobe: {ffprobe}\nתיקיית BIN_DIR נוספה ל-PATH: {bin_dir}\n\nשגיאה:\n{error}", "single_instance_title": "כבר פועל", "single_instance_text": "האפליקציה כבר פתוחה. סגור את החלון האחר לפני פתיחה נוספת.", "pid_warning_title": "אזהרת פתיחה", "pid_warning_text": "לא ניתן ליצור קובץ PID זמני. האפליקציה תמשיך לעבוד, אך זיהוי מופע יחיד עלול להיות מוגבל.", "hardware_scan_title": "סריקת חומרה", "hardware_scan_text": "בודק האצת חומרה...", "hardware_scan_done": "מצב חומרה זוהה: {mode}", "hardware_scan_cpu": "האצת חומרה אינה זמינה. מעבר למצב CPU.", "hardware_scan_cpu_details": "לא נמצאו מקודדי GPU. אפשר לעדכן דרייבר ולנסות שוב.", "ffmpeg_path_message": "משתמש ב-FFmpeg: {ffmpeg}", "diagnostics_title": "אבחון", "copy_to_clipboard": "העתק ללוח", "report_to_alon": "דווח לאלון רייך"}
}
from system.utils import UIManager
def tr(key: str) -> str: return TRANSLATIONS.get(LOCALE_CODE, TRANSLATIONS["en"]).get(key, TRANSLATIONS["en"].get(key, key))
def normalize_hardware_strategy(value: str | None) -> str | None:
    text = str(value or "").strip().upper()
    if "NVIDIA" in text: return "NVIDIA"
    if "AMD" in text: return "AMD"
    if "INTEL" in text: return "INTEL"
    if "CPU" in text: return "CPU"
    return None
PID_FILE_HANDLE = None
def cleanup_pid_lock():
    global PID_FILE_HANDLE
    if PID_FILE_HANDLE:
        try: PID_FILE_HANDLE.close()
        except Exception: pass
        PID_FILE_HANDLE = None
def _pe_machine_name(pe_path: str):
    try:
        with open(pe_path, "rb") as f: head = f.read(4096)
        pe_off = int.from_bytes(head[0x3C:0x40], "little")
        if pe_off + 6 >= len(head):
            with open(pe_path, "rb") as f: f.seek(pe_off + 4); machine = int.from_bytes(f.read(2), "little")
        else: machine = int.from_bytes(head[pe_off + 4:pe_off + 6], "little")
        return {0x014C: "x86", 0x8664: "x64", 0xAA64: "arm64"}.get(machine, f"unknown(0x{machine:04x})")
    except Exception: return "unknown"
def _python_machine_name():
    m = (platform.machine() or "").lower()
    if "arm" in m: return "arm64"
    return "x64" if struct.calcsize("P") * 8 == 64 else "x86"
def probe_mpv_dependencies():
    required = ["libmpv-2.dll"]
    missing = [f for f in required if not os.path.exists(os.path.join(BIN_DIR, f))]
    if missing:
        logger.error(f"FILES: Missing required MPV binaries: {missing}")
        return False, "MPV playback requires libmpv-2.dll in the binaries folder next to this app."
    libmpv_path = os.path.join(BIN_DIR, "libmpv-2.dll")
    py_arch = _python_machine_name()
    dll_arch = _pe_machine_name(libmpv_path)
    if dll_arch in ("x86", "x64", "arm64") and dll_arch != py_arch:
        logger.error(f"FILES: Architecture mismatch: Python is {py_arch}, libmpv is {dll_arch}")
        return False, f"MPV DLL architecture ({dll_arch}) does not match this Python build ({py_arch}). Replace libmpv-2.dll with a matching build."
    try:
        import mpv
        logger.info("FILES: MPV dependencies verified successfully.")
        return True, ""
    except Exception as e:
        logger.error(f"FILES: MPV import failed: {e}")
        arch = platform.machine()
        err_msg = str(e)
        if "WinError 193" in err_msg or "could not load" in err_msg:
            err_msg = (
                f"The MPV DLL in binaries is not compatible with this Python.\n\n"
                f"System: {arch} / Python: {py_arch} / libmpv: {dll_arch}\n{e}"
            )
        return False, f"Failed to load mpv library:\n\n{err_msg}"

def notify_mpv_probe_failure(reason: str):
    msg_box = QMessageBox()
    msg_box.setIcon(QMessageBox.Critical)
    msg_box.setWindowTitle("MPV Not Available")
    msg_box.setText("Video preview playback will be disabled until MPV is fixed.")
    msg_box.setInformativeText(reason)
    msg_box.exec_()

def check_mpv_dependencies():
    ok, reason = probe_mpv_dependencies()
    if not ok:
        notify_mpv_probe_failure(reason)
    return ok
def get_ffmpeg_hwaccels(ffmpeg_path: str) -> list:
    try:
        startupinfo = None
        if sys.platform == "win32": startupinfo = subprocess.STARTUPINFO(); startupinfo.dwFlags |= subprocess.STARTF_USESHOWWINDOW; startupinfo.wShowWindow = subprocess.SW_HIDE
        result = subprocess.run([ffmpeg_path, '-hwaccels'], capture_output=True, text=True, check=True, startupinfo=startupinfo, creationflags=(subprocess.CREATE_NO_WINDOW if sys.platform == "win32" else 0), timeout=5)
        hwaccels = []
        for line in result.stdout.splitlines():
            line = line.strip()
            if line and not line.startswith("Hardware acceleration methods:"): hwaccels.append(line)
        return hwaccels
    except Exception: return []
from processing.media_utils import check_encoder_capability as _check_encoder_capability
def check_encoder_capability(ffmpeg_path: str, encoder_name: str) -> bool:
    logger.info(f"GPU: Testing encoder '{encoder_name}'...")
    res = _check_encoder_capability(ffmpeg_path, encoder_name, hardware_scan_details=HARDWARE_SCAN_DETAILS)
    if res: logger.info(f"GPU: Encoder '{encoder_name}' is WORKING.")
    else: logger.warning(f"GPU: Encoder '{encoder_name}' failed test.")
    return res
class HardwareWorker(QObject):
    finished = pyqtSignal(str)
    status_update = pyqtSignal(str)
    def __init__(self, ffmpeg_path):
        super().__init__(); self.ffmpeg_path = ffmpeg_path; self.stop_requested = False; self.watchdog_timer = None; self._is_aborted = False
    def abort(self): self._is_aborted = True; self.stop_requested = True
    def stop(self): self.stop_requested = True
    def run(self):
        import threading
        def watchdog(): self.stop_requested = True
        self.watchdog_timer = threading.Timer(15.0, watchdog); self.watchdog_timer.daemon = True; self.watchdog_timer.start(); ffmpeg_path = self.ffmpeg_path
        try:
            self.status_update.emit("🔍 Initializing Hardware Triage...")
            available = get_ffmpeg_hwaccels(ffmpeg_path); logger.info(f"GPU: Available FFmpeg hwaccels: {available}")
            detected_mode = self._determine_hardware_strategy_with_stop(available, ffmpeg_path)
            if not self._is_aborted: self.finished.emit(detected_mode)
        except Exception as e:
            logger.error(f"GPU: Hardware scan error: {e}")
            if not self._is_aborted: self.finished.emit(FORCE_GPU if FORCE_GPU in {"NVIDIA", "AMD", "INTEL"} else "CPU")
        finally:
            if self.watchdog_timer: self.watchdog_timer.cancel()
    def _determine_hardware_strategy_with_stop(self, available_accels, ffmpeg_path):
        os.environ.pop("VIDEO_HW_ENCODER", None); os.environ.pop("VIDEO_FORCE_CPU", None)
        if FORCE_GPU:
            logger.info(f"GPU: Forced GPU mode: {FORCE_GPU}")
            self.status_update.emit(f"🚀 Forcing {FORCE_GPU} Hardware Mode...")
            if FORCE_GPU == "NVIDIA": os.environ["VIDEO_HW_ENCODER"] = "h264_nvenc"; return "NVIDIA"
            if FORCE_GPU == "AMD": os.environ["VIDEO_HW_ENCODER"] = "h264_amf"; return "AMD"
            if FORCE_GPU == "INTEL": os.environ["VIDEO_HW_ENCODER"] = "h264_qsv"; return "INTEL"
        for mode, encoder in (("NVIDIA", "h264_nvenc"), ("AMD", "h264_amf"), ("INTEL", "h264_qsv")):
            if self.stop_requested or self._is_aborted:
                break
            self.status_update.emit(f"🧪 Testing {mode} Acceleration...")
            if check_encoder_capability(self.ffmpeg_path, encoder):
                self.status_update.emit(f"✅ {mode} Acceleration Verified!")
                os.environ["VIDEO_HW_ENCODER"] = encoder
                return mode
        self.status_update.emit("⚠️ Hardware acceleration unavailable. Falling back to CPU.")
        os.environ["VIDEO_FORCE_CPU"] = "1"
        return "CPU"
def build_diagnostics(ffmpeg_path: str, ffprobe_path: str, error_text: str) -> str: return tr("dependency_error_details").format(ffmpeg=ffmpeg_path, ffprobe=ffprobe_path, bin_dir=BIN_DIR, error=error_text or "Unknown error")
def show_dependency_error_dialog(ffmpeg_path: str, ffprobe_path: str, error_text: str):
    msg_box = QMessageBox(); msg_box.setIcon(QMessageBox.Critical); msg_box.setWindowTitle(tr("dependency_error_title")); msg_box.setText(tr("dependency_error_text")); details = build_diagnostics(ffmpeg_path, ffprobe_path, error_text); msg_box.setDetailedText(details); UIManager.style_and_size_msg_box(msg_box, details, tr("copy_to_clipboard")); open_button = msg_box.addButton(tr("dependency_error_open_folder"), QMessageBox.ActionRole); retry_button = msg_box.addButton(tr("dependency_error_retry"), QMessageBox.AcceptRole); exit_button = msg_box.addButton(tr("dependency_error_exit"), QMessageBox.RejectRole); msg_box.exec_(); clicked = msg_box.clickedButton()
    if clicked == open_button:
        try: subprocess.Popen(["explorer", BIN_DIR])
        except Exception: pass
        return "open"
    if clicked == retry_button: return "retry"
    if clicked == exit_button: return "exit"
    return "exit"
def show_startup_warning(app: QApplication, title: str, text: str):
    if hasattr(app, "activeWindow") and app.activeWindow():
        window = app.activeWindow()
        if hasattr(window, "statusBar"):
            try: window.statusBar().showMessage(text, 8000); return
            except Exception: pass
    msg = QMessageBox(); msg.setIcon(QMessageBox.Warning); msg.setWindowTitle(title); msg.setText(text); msg.exec_()
RECOVERY_INSTANCE = None

def exception_hook(exctype, value, tb):
    error_text = "".join(traceback.format_exception(exctype, value, tb)); logger.critical(f"FATAL: Uncaught exception: {error_text}")

    global RECOVERY_INSTANCE
    if RECOVERY_INSTANCE:
        RECOVERY_INSTANCE._skip_cleanup = True

    try:
        app = QCoreApplication.instance()
        if app is not None:
            msg_box = QMessageBox(); msg_box.setIcon(QMessageBox.Critical); msg_box.setWindowTitle(tr("diagnostics_title")); msg_box.setText(str(value)); msg_box.setDetailedText(error_text); UIManager.style_and_size_msg_box(msg_box, f"Value: {value}\n\nTraceback:\n{error_text}", tr("copy_to_clipboard")); msg_box.exec_()
    except Exception as dialog_err:
        try:
            logger.critical(f"FATAL: Could not show crash dialog: {dialog_err}")
        except Exception:
            pass
    sys.__excepthook__(exctype, value, tb)
if __name__ == "__main__":
    logger.info("BOOT: Starting Fortnite Video Software core...")
    def validate_crops_coordinations():
        import json; from processing.hud_config import DEFAULT_HUD_CONFIG, sanitize_hud_config, validate_hud_config; conf_dir = os.path.join(BASE_DIR, 'processing'); conf_path = os.path.join(conf_dir, 'crops_coordinations.conf'); default_conf_data = DEFAULT_HUD_CONFIG
        def write_defaults():
            try:
                os.makedirs(conf_dir, exist_ok=True)
                with open(conf_path, 'w', encoding='utf-8') as f: json.dump(default_conf_data, f, indent=4)
                logger.info(f"FILES: Created default config at {conf_path}")
            except Exception as e: logger.error(f"FILES: Failed to write default config: {e}")
        if not os.path.exists(conf_path): logger.warning(f"FILES: Config missing at {conf_path}, using internal defaults."); write_defaults(); return
        try:
            with open(conf_path, 'r', encoding='utf-8') as f: data = json.load(f)
            issues = validate_hud_config(data)
            if issues:
                logger.warning(f"FILES: Config validation failed at {conf_path}, repairing: {issues}"); data = sanitize_hud_config(data); os.makedirs(conf_dir, exist_ok=True)
                with open(conf_path, 'w', encoding='utf-8') as f: json.dump(data, f, indent=4)
            else: logger.info(f"FILES: Config verified at {conf_path}")
        except Exception as e: logger.error(f"FILES: Error reading {conf_path}: {e}"); write_defaults()
    validate_crops_coordinations()
    ProcessManager.kill_orphans(); ProcessManager.cleanup_temp_files()
    if os.environ.get("FVS_STATE_TRANSFER_RESTORE") != "1":
        try:
            from system.temp_video_workspace import cleanup_workspace
            cleanup_workspace(logger)
        except Exception as e:
            logger.warning(f"TEMP_WORKSPACE: boot cleanup skipped: {e}")
    logger.info("FILES: Verifying FFmpeg core..."); is_valid_deps, ffmpeg_path, dep_error = DependencyDoctor.check_ffmpeg(BASE_DIR); ffprobe_path = os.path.join(os.path.dirname(ffmpeg_path), "ffprobe.exe" if sys.platform == "win32" else "ffprobe")
    if is_valid_deps: logger.info(f"FILES: FFmpeg core verified: {ffmpeg_path}")
    else: logger.error(f"FILES: FFmpeg core verification failed: {dep_error}")
    app = QCoreApplication.instance()
    if app is None: app = QApplication(sys.argv)

    class GlobalKeyboardFilter(QObject):
        def eventFilter(self, obj, event):
            if event.type() == QEvent.KeyPress:
                # Comprehensive list of input widgets where global shortcuts should be disabled
                from PyQt5.QtWidgets import QPushButton
                input_widgets = (QLineEdit, QTextEdit, QPlainTextEdit, QAbstractSpinBox, QPushButton)
                fw = QApplication.focusWidget()

                if isinstance(fw, input_widgets):
                    return False

                key = event.key()
                # Mapping of keys to global actions
                if key in (Qt.Key_Space, Qt.Key_BracketLeft, Qt.Key_BracketRight,
                          Qt.Key_Left, Qt.Key_Right, Qt.Key_Up, Qt.Key_Down,
                          Qt.Key_Plus, Qt.Key_Equal, Qt.Key_Minus, Qt.Key_F12,
                          Qt.Key_Return, Qt.Key_Enter, Qt.Key_Z, Qt.Key_Y):

                    mw = QApplication.activeWindow()
                    if mw and hasattr(mw, "handle_global_key_press"):
                        # Only intercept if the window explicitly handles and accepts the event
                        if mw.handle_global_key_press(event):
                            return True
            return False
    kb_filter = GlobalKeyboardFilter(app)
    app.installEventFilter(kb_filter)

    app.setStyleSheet(UIStyles.GLOBAL_STYLE); app.setApplicationName(tr("app_name")); QCoreApplication.setOrganizationName("FortniteVideoSoftware"); sys.excepthook = exception_hook; import time; pid_retries = 3; success = False; pid_handle = None
    for attempt in range(pid_retries):
        success, pid_handle = ProcessManager.acquire_pid_lock(PID_APP_NAME)
        if success: break
        time.sleep(0.5)
    if not success: logger.warning("BOOT: Single instance lock active. Exiting."); sys.exit(0)
    PID_FILE_HANDLE = pid_handle
    logger.info("FILES: Checking MPV dependencies...")
    mpv_ready, mpv_hint = probe_mpv_dependencies()
    if not mpv_ready:
        notify_mpv_probe_failure(mpv_hint)
    from system.recovery_manager import RecoveryManager
    recovery = RecoveryManager("main_app", logger)
    RECOVERY_INSTANCE = recovery

    logger.info(f"BOOT: Checking recovery fault state... (State file: {recovery.state_file.exists()}, Lock file: {recovery.lock_file.exists()})")
    for h in logger.handlers: h.flush()
    if recovery.check_fault():
        logger.info("BOOT: Recovery fault detected. Showing dialog.")
        for h in logger.handlers: h.flush()
        msg_box = QMessageBox()
        msg_box.setIcon(QMessageBox.Question)
        msg_box.setWindowTitle(tr("app_name"))
        msg_box.setText("The application crashed last time. Would you like to restore your previous session?")
        msg_box.setStandardButtons(QMessageBox.Yes | QMessageBox.No)
        for button_role in (QMessageBox.Yes, QMessageBox.No):
            button = msg_box.button(button_role)
            if button is not None:
                button.setMinimumWidth(120)
                button.setCursor(Qt.PointingHandCursor)
        if msg_box.exec_() == QMessageBox.Yes:
            logger.info("BOOT: User chose to RESTORE session.")
            os.environ["FVS_RESTORE_SESSION"] = "1"
            recovery.activate_safe_mode()
        else:
            logger.info("BOOT: User chose NOT to restore session.")
            recovery.clear_state()
        for h in logger.handlers: h.flush()
    else:
        logger.info("BOOT: No recovery fault detected.")
        for h in logger.handlers: h.flush()
    recovery.acquire_lock()
    app.aboutToQuit.connect(recovery.cleanup_lock)

    from ui.main_window import VideoCompressorApp
    if not is_valid_deps:
         while True:
            action = show_dependency_error_dialog(ffmpeg_path, ffprobe_path, dep_error)
            if action == "open": continue
            if action == "retry":
                 is_valid_retry, ffmpeg_path, dep_error = DependencyDoctor.check_ffmpeg(BASE_DIR)
                 if is_valid_retry: break
                 continue
            sys.exit(1)
    app.aboutToQuit.connect(lambda: os.environ.__setitem__("PATH", ORIGINAL_PATH))
    if sys.platform.startswith("win"):
        try: ctypes.windll.shell32.SetCurrentProcessExplicitAppUserModelID("FortniteVideoTool.VideoCompressor")
        except: pass
        try:
            hwnd = ctypes.windll.kernel32.GetConsoleWindow()
            if hwnd: ctypes.windll.user32.ShowWindow(hwnd, 0)
        except: pass
    icon_path = ""
    try:
        preferred = os.path.join(BASE_DIR, "icons", "Video_Icon_File.ico"); fallback = os.path.join(BASE_DIR, "icons", "app_icon.ico"); icon_path = preferred if os.path.exists(preferred) else fallback
        if os.path.exists(icon_path): app.setWindowIcon(QIcon(icon_path))
        else: app.setWindowIcon(app.style().standardIcon(QStyle.SP_ComputerIcon))
    except: pass
    from system.config import ConfigManager; from ui.widgets.tooltip_manager import ToolTipManager; config_path = os.path.join(BASE_DIR, 'config', 'main_app', 'main_app.conf'); cm = ConfigManager(config_path); tm = ToolTipManager(); cached_hw = normalize_hardware_strategy(cm.config.get("last_hardware_strategy"))
    if cached_hw == "CPU": cached_hw = normalize_hardware_strategy(cm.config.get("cached_hardware"))
    isolation_active = diagnostic_runtime.is_isolation_active()
    if isolation_active: cached_hw = "CPU"
    file_arg = sys.argv[1] if len(sys.argv) > 1 else None; initial_strategy = "CPU" if isolation_active else (cached_hw if cached_hw else "Scanning...")
    if cached_hw == "NVIDIA": os.environ["VIDEO_HW_ENCODER"] = "h264_nvenc"
    elif cached_hw == "AMD": os.environ["VIDEO_HW_ENCODER"] = "h264_amf"
    elif cached_hw == "INTEL": os.environ["VIDEO_HW_ENCODER"] = "h264_qsv"
    ex = VideoCompressorApp(file_arg, initial_strategy, bin_dir=BIN_DIR, config_manager=cm, tooltip_manager=tm, mpv_ready=mpv_ready, mpv_error_hint=mpv_hint if not mpv_ready else "")
    try:
        if icon_path and os.path.exists(icon_path): ex.setWindowIcon(QIcon(icon_path))
        elif hasattr(app, "style"): ex.setWindowIcon(app.style().standardIcon(QStyle.SP_ComputerIcon))
    except: pass
    ex.show(); QTimer.singleShot(100, lambda: ex.set_style())
    if not file_arg: QTimer.singleShot(250, lambda: ex._set_upload_hint_active(True))
    try:
        if hasattr(ex, "statusBar") and ex.statusBar(): ex.statusBar().showMessage(tr("ffmpeg_path_message").format(ffmpeg=ffmpeg_path), 8000)
    except: pass
    def start_hw_scan():
        if isolation_active:
            logger.warning("GPU: Diagnostic isolation active; forcing CPU-only playback profile and skipping GPU scan/cache.")
            ex.on_hardware_scan_finished("CPU"); ex.scan_complete = True; return
        if cached_hw:
            logger.info(f"GPU: Loading cached strategy: {cached_hw}")
            ex.on_hardware_scan_finished(cached_hw); ex.scan_complete = True; return
        hw_thread = QThread(); hw_worker = HardwareWorker(ffmpeg_path); hw_worker.moveToThread(hw_thread); hw_thread.started.connect(hw_worker.run); hw_worker.status_update.connect(lambda msg: ex.status_update_signal.emit(msg)); hw_worker.finished.connect(ex.on_hardware_scan_finished); hw_worker.finished.connect(hw_thread.quit); hw_worker.finished.connect(hw_worker.deleteLater); hw_thread.finished.connect(hw_thread.deleteLater); ex._hw_thread = hw_thread; ex._hw_worker = hw_worker; hw_thread.start(); logger.info("GPU: Background hardware scan started.")
    QTimer.singleShot(500, start_hw_scan); from system.utils import MPVSafetyManager; MPVSafetyManager.register_global_shutdown_handler(); logger.info("BOOT: Fortnite Video Software initialized successfully."); ret = app.exec_(); logger.info(f"BOOT: App exiting with code: {ret}")
    try:
        if hasattr(ex, "_hw_worker") and ex._hw_worker: ex._hw_worker.abort()
        if hasattr(ex, "_hw_thread") and ex._hw_thread.isRunning(): ex._hw_thread.quit(); ex._hw_thread.wait(2000)
    except: pass
    sys.exit(ret)
