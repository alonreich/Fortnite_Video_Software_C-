import faulthandler, logging, os, signal, subprocess, sys, time, threading, traceback, weakref
from PyQt5.QtCore import (Qt, QTimer, pyqtSignal, QEvent, QRect, QPoint, QSize, QCoreApplication, QThread)
from PyQt5.QtGui import (QIcon, QPixmap, QColor, QFont, QPainter)
from PyQt5.QtWidgets import (QMainWindow, QWidget, QVBoxLayout, QHBoxLayout, QMessageBox, QStyle, QApplication, QLabel, QFrame, QSizePolicy, QProgressBar, QStackedLayout, QPushButton, QCheckBox, QSpinBox, QDoubleSpinBox, QGridLayout, QLineEdit, QSlider, QTextEdit, QPlainTextEdit, QAbstractSpinBox)
from system.utils import MPVSafetyManager
from ui.parts.player_mixin import PlayerMixin
from ui.parts.ui_builder_mixin import UiBuilderMixin
from ui.parts.volume_mixin import VolumeMixin
from ui.parts.main_window_core_a import MainWindowCoreAMixin
from ui.parts.main_window_core_b import MainWindowCoreBMixin
from ui.parts.main_window_core_c import MainWindowCoreCMixin
from ui.parts.main_window_events import MainWindowEventsMixin
from ui.parts.main_window_file_a import MainWindowFileAMixin
from ui.parts.main_window_file_b import MainWindowFileBMixin
from ui.parts.main_window_tools import MainWindowToolsMixin
from ui.parts.main_window_ui_helpers_a import MainWindowUiHelpersAMixin
from ui.parts.main_window_ui_helpers_b import MainWindowUiHelpersBMixin
from ui.parts.music_mixin import MusicMixin
from ui.parts.trim_mixin import TrimMixin
from ui.parts.ffmpeg_mixin import FfmpegMixin
from ui.parts.keyboard_mixin import KeyboardMixin
from ui.parts.phase_overlay_mixin import PhaseOverlayMixin
from ui.styles import UIStyles
from system.config import ConfigManager
from system.state_transfer import StateTransfer
from ui.widgets.tooltip_manager import ToolTipManager
from ui.widgets.timeline_overlay import TimelineOverlay

def _safe_single_shot(delay_ms, callback):
    single_shot = getattr(QTimer, "singleShot", None)
    if callable(single_shot):
        single_shot(delay_ms, callback)
    elif delay_ms <= 0 and callable(callback):
        callback()

def _recovery_int(value, default=0):
    try:
        return int(value)
    except Exception:
        return int(default)

def _recovery_float(value, default=0.0):
    try:
        return float(value)
    except Exception:
        return float(default)

def _normalize_recovery_music_track(track):
    if isinstance(track, dict):
        path = track.get("path") or track.get("file") or track.get("music_path")
        offset = track.get("offset_sec", track.get("offset", track.get("file_offset_sec", None)))
        if offset is None and track.get("start_ms") is not None:
            offset = _recovery_float(track.get("start_ms"), 0.0) / 1000.0
        duration = track.get("duration_sec", track.get("duration", track.get("dur", 0.0)))
    elif isinstance(track, (list, tuple)) and len(track) >= 1:
        path = track[0]
        offset = track[1] if len(track) >= 2 else 0.0
        duration = track[2] if len(track) >= 3 else 0.0
    else:
        return None
    if not path:
        return None
    return (str(path), max(0.0, _recovery_float(offset, 0.0)), max(0.0, _recovery_float(duration, 0.0)))

def _deserialize_recovery_music_tracks(raw_tracks):
    tracks = []
    for track in list(raw_tracks or []):
        normalized = _normalize_recovery_music_track(track)
        if normalized:
            tracks.append(normalized)
    return tracks

def _serialize_recovery_music_tracks(raw_tracks):
    result = []
    for path, offset, duration in _deserialize_recovery_music_tracks(raw_tracks):
        result.append({"path": path, "offset_sec": offset, "duration_sec": duration})
    return result

def _normalize_recovery_speed_segments(raw_segments):
    segments = []
    for seg in list(raw_segments or []):
        if not isinstance(seg, dict):
            continue
        try:
            start = int(seg.get("start", seg.get("start_ms", 0)))
            end = int(seg.get("end", seg.get("end_ms", 0)))
            speed = float(seg.get("speed", seg.get("multiplier", 1.0)))
        except Exception:
            continue
        if end > start:
            segments.append({"start": start, "end": end, "start_ms": start, "end_ms": end, "speed": speed})
    segments.sort(key=lambda item: (item["start"], item["end"]))
    return segments

class _QtLiveLogHandler(logging.Handler):
    def __init__(self, target):
        super().__init__()
        self.target = weakref.ref(target)

    def emit(self, record):
        try:
            t = self.target()
            if t and hasattr(t, "log_overlay_sink"):
                msg = self.format(record)
                t.log_overlay_sink(msg)
        except Exception as err:
            try:
                logging.getLogger("Main_App").debug("log_overlay_sink failed: %s", err)
            except Exception:
                pass

class FortniteVideoSoftware(QMainWindow, PlayerMixin, UiBuilderMixin, VolumeMixin,
                            MainWindowCoreAMixin, MainWindowCoreBMixin, MainWindowCoreCMixin,
                            MainWindowEventsMixin, MainWindowFileAMixin, MainWindowFileBMixin,
                            MainWindowToolsMixin, MainWindowUiHelpersAMixin, MainWindowUiHelpersBMixin,
                            MusicMixin, TrimMixin, FfmpegMixin, KeyboardMixin, PhaseOverlayMixin):
    progress_update_signal = pyqtSignal(float)
    status_update_signal = pyqtSignal(str)
    process_finished_signal = pyqtSignal(bool, str)
    video_ended_signal = pyqtSignal()
    thumbnail_extracted_signal = pyqtSignal(object)
    duration_changed_signal = pyqtSignal(int)

    def __init__(self, file_path=None, hardware_strategy="Scanning...", bin_dir="", config_manager=None, tooltip_manager=None, mpv_ready=True, mpv_error_hint=""):
        super().__init__()
        self.base_dir = os.path.abspath(os.path.join(os.path.dirname(__file__), '..'))
        self.bin_dir = bin_dir if bin_dir else os.path.join(self.base_dir, 'binaries')
        self.config_manager = config_manager if config_manager else ConfigManager(os.path.join(self.base_dir, 'config', 'main_app', 'main_app.conf'))
        self.tooltip_manager = tooltip_manager if tooltip_manager else ToolTipManager(self)
        app_instance = None
        try:
            app_getter = getattr(QCoreApplication, "instance", None)
            app_instance = app_getter() if callable(app_getter) else None
        except Exception:
            app_instance = None
        if app_instance and hasattr(app_instance, "installEventFilter"):
            app_instance.installEventFilter(self.tooltip_manager)
        self.timeline_overlay = TimelineOverlay(self)
        self.positionSlider = self.timeline_overlay.positionSlider
        PlayerMixin.__init__(self)
        self.logger = logging.getLogger("Main_App"); self._mpv_lock = threading.RLock(); self._is_seeking_active = False; self._last_player_output_bind_ts = 0; self._binding_player_output = False
        self._mpv_ready = bool(mpv_ready)
        self._mpv_error_hint = str(mpv_error_hint or "")
        self.duration_changed_signal.connect(self._safe_handle_duration_changed)
        self.thumbnail_extracted_signal.connect(self._on_thumb_extracted)
        restore_transfer = os.environ.pop("FVS_STATE_TRANSFER_RESTORE", "") == "1"
        self._state_transfer_session = {}
        try:
            if restore_transfer:
                self._state_transfer_session = StateTransfer.load_state() or {}
                if not file_path and self._state_transfer_session.get("input_file"):
                    file_path = self._state_transfer_session.get("input_file")
            else:
                StateTransfer.clear_state()
        except: pass
        self.setWindowTitle("Fortnite Video Software - Pro Edition"); self.setMinimumSize(1200, 800)
        self.central_widget = QWidget(); self.setCentralWidget(self.central_widget)
        self._init_core_logic(file_path, hardware_strategy)
        self._setup_recovery_manager()
        self.set_style(); self.init_ui(); self._setup_mpv(); self._set_video_controls_enabled(False)
        self.setAcceptDrops(True); self.status_bar = self.statusBar(); self.status_bar.hide(); self.restore_geometry()
        def redirect_show_message(message, timeout=5000):
            if hasattr(self, "_set_right_status_message"):
                self._set_right_status_message(message, timeout)
        self.status_bar.showMessage = redirect_show_message
        if not self._mpv_ready and self.statusBar():
            hint = (self._mpv_error_hint or "MPV playback is unavailable.").strip().replace("\n", " ")
            self.statusBar().showMessage(f"Preview disabled: {hint}", 0)
        if self._state_transfer_session:
            _safe_single_shot(1200, self._restore_state_transfer_session)
        self.positionSlider.sliderMoved.connect(self.set_player_position)
        self.positionSlider.trim_times_changed.connect(self._on_slider_trim_changed)
        self.positionSlider.music_trim_changed.connect(self._on_slider_music_trim_changed)
        self.positionSlider.rangeChanged.connect(lambda *_: self._maybe_enable_process())
        self.positionSlider.valueChanged.connect(lambda: self._sync_main_timeline_badges())
        self.status_update_signal.connect(self.log_overlay_sink)
        self.show()
        _safe_single_shot(0, self._post_show_bootstrap)
        _safe_single_shot(100, self._update_overlay_positions)
        _safe_single_shot(500, self._update_overlay_positions)
        _safe_single_shot(1500, self._update_overlay_positions)

    def _post_show_bootstrap(self):
        try:
            p = getattr(self, "player", None)
            if p and hasattr(p, "_wid_bound_once"):
                p._wid_bound_once = False
        except Exception:
            pass
        try:
            vs = getattr(self, "video_surface", None)
            if vs:
                vs.show()
        except Exception:
            pass
        self._bind_main_player_output()
        self._restore_recovery_state()
        try:
            if os.environ.get("FVS_RESTORE_SESSION") == "1":
                os.environ.pop("FVS_RESTORE_SESSION", None)
        except Exception:
            pass

    def log_overlay_sink(self, msg: str):
        if hasattr(self, "_append_live_log"):
            self._append_live_log(msg)

    def _init_core_logic(self, file_path, hardware_strategy):
        self._restoring_recovery_state = False
        self.input_file_path = None; self.original_duration_ms = 0; self.original_resolution = ""; self.trim_start_ms = 0; self.trim_end_ms = 0
        self.is_playing = False; self.wants_to_play = False; self.playback_rate = 1.1; self.hardware_strategy = hardware_strategy
        self.scan_complete = (hardware_strategy != "Scanning..."); self.speed_segments = []
        self.last_dir = self.config_manager.config.get('last_directory', os.path.expanduser("~"))
        self.timer = QTimer(self); self.timer.timeout.connect(self.update_player_state)
        if file_path and os.path.exists(file_path): _safe_single_shot(500, lambda: self.handle_file_selection(file_path))

    def _setup_recovery_manager(self):
        from system.recovery_manager import RecoveryManager
        self.recovery_manager = RecoveryManager("main_app", self.logger)
        self.recovery_timer = QTimer(self)
        self.recovery_timer.timeout.connect(self._save_recovery_state)
        self.recovery_timer.start(10000)

        self.recovery_debounce_timer = QTimer(self)
        self.recovery_debounce_timer.setSingleShot(True)
        self.recovery_debounce_timer.timeout.connect(self._save_recovery_state)

        QTimer.singleShot(1000, self._connect_recovery_signals)

    def _trigger_recovery_save(self):
        if not hasattr(self, "recovery_debounce_timer"): return
        self.recovery_debounce_timer.start(500)

    def _immediate_recovery_save(self):
        self._save_recovery_state(sync=True)

    def _connect_recovery_signals(self):
        try:
            # Immediate save for these (user likely to crash after manual action)
            immediate_widgets = [
                self.start_trim_button, self.end_trim_button, self.thumb_pick_btn,
                self.music_button, self.granular_button, getattr(self, "granular_clear_button", None)
            ]
            for b in immediate_widgets:
                if b is not None:
                    try: b.clicked.connect(self._immediate_recovery_save)
                    except: pass

            # Debounced save for these (continuous adjustments)
            debounced_widgets = [
                self.mobile_checkbox, self.teammates_checkbox, self.boss_hp_checkbox,
                self.granular_checkbox, getattr(self, "no_fade_checkbox", None),
                self.quality_slider, self.speed_spinbox, self.volume_slider,
                self.portrait_text_input, self.start_minute_input, self.start_second_input,
                self.start_ms_input, self.end_minute_input, self.end_second_input, self.end_ms_input
            ]
            for w in debounced_widgets:
                if w is None: continue
                try:
                    if hasattr(w, "toggled"): w.toggled.connect(self._trigger_recovery_save)
                    elif hasattr(w, "valueChanged"): w.valueChanged.connect(self._trigger_recovery_save)
                    elif hasattr(w, "textChanged"): w.textChanged.connect(self._trigger_recovery_save)
                except: pass

            if hasattr(self, "positionSlider"):
                self.positionSlider.valueChanged.connect(self._trigger_recovery_save)
                if hasattr(self.positionSlider, "trim_times_changed"):
                    self.positionSlider.trim_times_changed.connect(self._immediate_recovery_save)
                if hasattr(self.positionSlider, "music_trim_changed"):
                    self.positionSlider.music_trim_changed.connect(self._immediate_recovery_save)
        except Exception as e:
            self.logger.debug(f"RECOVERY: Signal binding error: {e}")

    def _save_recovery_state(self, sync=False):
        if not hasattr(self, "recovery_manager"): return
        if getattr(self, "_restoring_recovery_state", False): return

        input_path = getattr(self, "source_file_path", None) or self.input_file_path
        if not input_path:
            return

        music_tracks = _serialize_recovery_music_tracks(getattr(self, "_wizard_tracks", []))
        has_music = bool(music_tracks)
        music_start_ms = _recovery_int(getattr(self, "music_timeline_start_ms", 0), 0)
        music_end_ms = _recovery_int(getattr(self, "music_timeline_end_ms", 0), 0)
        if has_music and music_end_ms <= music_start_ms:
            music_start_ms = _recovery_int(getattr(self, "trim_start_ms", 0), 0)
            try:
                track_duration_ms = int(round(float(music_tracks[0].get("duration_sec", 0.0) or 0.0) * 1000.0))
            except: track_duration_ms = 0
            trim_end_ms = _recovery_int(getattr(self, "trim_end_ms", 0), 0)
            music_end_ms = trim_end_ms if trim_end_ms > music_start_ms else music_start_ms + max(0, track_duration_ms)
        speed_value = _recovery_float(self.speed_spinbox.value() if hasattr(self, "speed_spinbox") else getattr(self, "playback_rate", 1.1), 1.1)
        thumbnail_ms = self.positionSlider.get_thumbnail_pos_ms() if hasattr(self, "positionSlider") and hasattr(self.positionSlider, "get_thumbnail_pos_ms") else 0
        selected_intro_sec = getattr(self, "selected_intro_abs_time", None)
        if selected_intro_sec is None and thumbnail_ms:
            selected_intro_sec = _recovery_float(thumbnail_ms, 0.0) / 1000.0
        state = {
            "assets": {
                "input_file_path": input_path,
                "wizard_tracks": music_tracks,
                "current_music_path": getattr(self, "_current_music_path", None),
                "original_resolution": getattr(self, "original_resolution", None),
                "original_duration_ms": getattr(self, "original_duration_ms", 0)
            },
            "volatile_settings": {
                "trim_start_ms": self.trim_start_ms,
                "trim_end_ms": self.trim_end_ms,
                "playback_rate": speed_value,
                "speed_segments": _normalize_recovery_speed_segments(self.speed_segments),
                "video_mix_volume": self.volume_slider.value() if hasattr(self, "volume_slider") else 100,
                "music_volume_pct": getattr(self, "_music_volume_pct", 80),
                "video_volume_pct": getattr(self, "_video_volume_pct", 80),
                "quality_slider_index": self.quality_slider.value() if hasattr(self, "quality_slider") else 7,
                "current_music_offset": getattr(self, "_current_music_offset", 0.0),
                "music_timeline_start_ms": music_start_ms,
                "music_timeline_end_ms": music_end_ms,
                "thumbnail_pos_ms": thumbnail_ms,
                "selected_intro_abs_time_sec": selected_intro_sec,
                "hardware_strategy": self.hardware_strategy,
                "target_mb_override": getattr(self, "target_mb_override", None)
            },
            "ui_dynamics": {
                "mobile_checked": self.mobile_checkbox.isChecked() if hasattr(self, "mobile_checkbox") else False,
                "teammates_checked": self.teammates_checkbox.isChecked() if hasattr(self, "teammates_checkbox") else False,
                "boss_hp_checked": self.boss_hp_checkbox.isChecked() if hasattr(self, "boss_hp_checked") else False,
                "granular_checked": self.granular_checkbox.isChecked() if hasattr(self, "granular_checkbox") else False,
                "no_fade_checked": getattr(self, "no_fade_checkbox", None).isChecked() if getattr(self, "no_fade_checkbox", None) else False,
                "portrait_text": self.portrait_text_input.text() if hasattr(self, "portrait_text_input") else "",
                "music_button_active": has_music,
                "active_tab_index": 0,
                "window_geometry_base64": bytes(self.saveGeometry().toBase64()).decode("utf-8"),
                "last_directory": self.last_dir,
                "slider_value_ms": self.positionSlider.value() if hasattr(self, "positionSlider") else 0
            }
        }
        self.logger.info(f"RECOVERY: Saving state (Sync={sync}). File: {input_path}")
        if sync:
            self.recovery_manager.save_state(state)
        else:
            self.recovery_manager.save_state_async(state)

    def _restore_recovery_state(self):
        if os.environ.get("FVS_RESTORE_SESSION") != "1": return
        state = self.recovery_manager.load_state()
        if not state: return
        self._restoring_recovery_state = True
        self.logger.info("RECOVERY: Restoring previous session state...")
        v = state.get("volatile_settings", {})
        u = state.get("ui_dynamics", {})
        a = state.get("assets", {})
        f = a.get("input_file_path")
        if f and os.path.exists(f):
            self.handle_file_selection(f)
            self.original_resolution = a.get("original_resolution", self.original_resolution)
            self.original_duration_ms = a.get("original_duration_ms", self.original_duration_ms)
            self.trim_start_ms = _recovery_int(v.get("trim_start_ms", 0), 0)
            self.trim_end_ms = _recovery_int(v.get("trim_end_ms", self.trim_end_ms), self.trim_end_ms)
            self.playback_rate = _recovery_float(v.get("playback_rate", 1.1), 1.1)
            self.speed_segments = _normalize_recovery_speed_segments(v.get("speed_segments", []))
            self.hardware_strategy = v.get("hardware_strategy", self.hardware_strategy)
            self.target_mb_override = v.get("target_mb_override", None)

            if hasattr(self, "speed_spinbox"): self.speed_spinbox.setValue(self.playback_rate)
            if hasattr(self, "volume_slider"): self.volume_slider.setValue(v.get("video_mix_volume", 100))
            if hasattr(self, "quality_slider"): self.quality_slider.setValue(v.get("quality_slider_index", 7))
            self._wizard_tracks = _deserialize_recovery_music_tracks(a.get("wizard_tracks", []))
            self._music_volume_pct = _recovery_int(v.get("music_volume_pct", 80), 80)
            self._video_volume_pct = _recovery_int(v.get("video_volume_pct", 80), 80)
            self._current_music_offset = _recovery_float(v.get("current_music_offset", 0.0), 0.0)
            self.music_timeline_start_ms = _recovery_int(v.get("music_timeline_start_ms", 0), 0)
            self.music_timeline_end_ms = _recovery_int(v.get("music_timeline_end_ms", 0), 0)
            if not self._wizard_tracks and a.get("current_music_path"):
                fallback_path = str(a.get("current_music_path"))
                if os.path.exists(fallback_path):
                    duration_sec = max(0.0, (self.music_timeline_end_ms - self.music_timeline_start_ms) / 1000.0)
                    self._wizard_tracks = [(fallback_path, self._current_music_offset, duration_sec)]
            self._wizard_tracks = [track for track in self._wizard_tracks if os.path.exists(track[0])]
            if self._wizard_tracks and self.music_timeline_end_ms <= self.music_timeline_start_ms:
                self.music_timeline_start_ms = self.trim_start_ms
                duration_ms = int(round(self._wizard_tracks[0][2] * 1000.0))
                self.music_timeline_end_ms = min(self.trim_end_ms, self.music_timeline_start_ms + duration_ms) if duration_ms > 0 else self.trim_end_ms
            restored_intro_sec = v.get("selected_intro_abs_time_sec", v.get("intro_abs_time_sec", None))
            if restored_intro_sec is not None:
                self.selected_intro_abs_time = _recovery_float(restored_intro_sec, 0.0)
            if hasattr(self, "mobile_checkbox"): self.mobile_checkbox.setChecked(u.get("mobile_checked", False))
            if hasattr(self, "teammates_checkbox"): self.teammates_checkbox.setChecked(u.get("teammates_checked", False))
            if hasattr(self, "boss_hp_checkbox"): self.boss_hp_checkbox.setChecked(u.get("boss_hp_checked", False))
            if hasattr(self, "granular_checkbox"): self.granular_checkbox.setChecked(u.get("granular_checked", False))
            if hasattr(self, "no_fade_checkbox"): self.no_fade_checkbox.setChecked(u.get("no_fade_checked", False))
            if hasattr(self, "portrait_text_input"): self.portrait_text_input.setText(u.get("portrait_text", ""))
            if hasattr(self, "_set_music_button_state"): self._set_music_button_state(bool(self._wizard_tracks))
            if self._wizard_tracks:
                btn = getattr(self, "music_button", None)
                if btn:
                    btn.setText('♪  REMOVE MUSIC  ♪')

                    from ui.styles import UIStyles
                    btn.setStyleSheet(UIStyles.BUTTON_DANGER + ' QPushButton { font-size: 10px; padding: 0px; }')
            if self._wizard_tracks and self._ensure_music_player_ready():
                f_t = self._wizard_tracks[0]; self._current_music_path = f_t[0]; self._current_music_offset = f_t[1]
                self._safe_mpv_command("loadfile", self._current_music_path, "replace", target_player=self._music_preview_player)
                self._safe_mpv_set("volume", self._music_volume_pct, target_player=self._music_preview_player)
            self.logger.info(
                "RECOVERY: Restored trim=%s-%sms speed=%.3fx music=%s tracks=%d quality=%s thumbnail=%s granular=%d hw=%s",
                self.trim_start_ms, self.trim_end_ms, self.playback_rate, bool(self._wizard_tracks),
                len(self._wizard_tracks), v.get("quality_slider_index", 7), v.get("thumbnail_pos_ms", 0),
                len(self.speed_segments), self.hardware_strategy
            )
            _safe_single_shot(1000, lambda: self._apply_restored_slider_state(v, u))
        else:
            self._restoring_recovery_state = False

    def _restore_state_transfer_session(self, attempt=0):
        state = getattr(self, "_state_transfer_session", {}) or {}
        if not state:
            return
        try:
            state_file = state.get("input_file")
            if state_file and not os.path.exists(state_file) and state.get("source_file"):
                state_file = state.get("source_file")
            if state_file and os.path.exists(state_file) and os.path.abspath(str(getattr(self, "input_file_path", "") or "")) != os.path.abspath(str(state_file)):
                self.handle_file_selection(state_file)
                if state.get("source_file"):
                    self.source_file_path = state.get("source_file")
                    self._loaded_display_path = state.get("source_file")
                    if hasattr(self, "drop_label"):
                        self.drop_label.setText(os.path.basename(str(state.get("source_file"))))
                if attempt < 12:
                    _safe_single_shot(500, lambda: self._restore_state_transfer_session(attempt + 1))
                return
            duration_ms = int(getattr(self, "original_duration_ms", 0) or 0)
            if duration_ms <= 0 and hasattr(self, "positionSlider"):
                duration_ms = int(self.positionSlider.maximum() or 0)
            if duration_ms <= 0 and attempt < 12:
                _safe_single_shot(500, lambda: self._restore_state_transfer_session(attempt + 1))
                return
            trim_start = int(state.get("trim_start", 0) or 0)
            trim_end = int(state.get("trim_end", duration_ms) or duration_ms)
            if duration_ms > 0:
                trim_start = max(0, min(trim_start, duration_ms))
                trim_end = max(trim_start, min(trim_end, duration_ms))
            self.trim_start_ms = trim_start
            self.trim_end_ms = trim_end
            restored_segments = []
            for seg in list(state.get("speed_segments", []) or []):
                if not isinstance(seg, dict):
                    continue
                try:
                    seg_start = int(seg.get("start", seg.get("start_ms", 0)))
                    seg_end = int(seg.get("end", seg.get("end_ms", 0)))
                    seg_speed = float(seg.get("speed", getattr(self, "playback_rate", 1.0)))
                except Exception:
                    continue
                if seg_end > seg_start:
                    restored_segments.append({"start": seg_start, "end": seg_end, "start_ms": seg_start, "end_ms": seg_end, "speed": seg_speed})
            self.speed_segments = restored_segments
            if hasattr(self, "granular_checkbox"):
                self.granular_checkbox.setChecked(bool(state.get("granular_checked", bool(self.speed_segments))))
            if state.get("hardware_mode"):
                self.hardware_strategy = state.get("hardware_mode")
            if state.get("resolution"):
                self.original_resolution = state.get("resolution")
                if hasattr(self, "resolution_label"):
                    self.resolution_label.setText(str(self.original_resolution))
            if hasattr(self, "positionSlider"):
                self.positionSlider.set_trim_times(self.trim_start_ms, self.trim_end_ms)
                visible_segments = self.speed_segments if bool(getattr(self, "granular_checkbox", None) and self.granular_checkbox.isChecked()) else []
                self.positionSlider.set_speed_segments(visible_segments)
                self.positionSlider.update()
            if hasattr(self, "_update_trim_widgets_from_trim_times"):
                self._update_trim_widgets_from_trim_times()
            if hasattr(self, "_update_granular_button_state"):
                self._update_granular_button_state()
            if hasattr(self, "_update_quality_label"):
                self._update_quality_label()
            StateTransfer.clear_state()
            self._state_transfer_session = {}
            self.logger.info("STATE_TRANSFER: Restored session returned from Crop Tool.")
        except Exception as e:
            self.logger.warning(f"STATE_TRANSFER: Failed to restore crop session state: {e}")

    def _apply_restored_slider_state(self, v, u):
        if hasattr(self, "positionSlider"):
            self.positionSlider.set_trim_times(self.trim_start_ms, self.trim_end_ms)
            if self._wizard_tracks:
                self.positionSlider.set_music_visible(True)
                self.positionSlider.set_music_times(self.music_timeline_start_ms, self.music_timeline_end_ms)
            else:
                self.positionSlider.reset_music_times()
            self.positionSlider.setValue(u.get("slider_value_ms", 0))
            if hasattr(self.positionSlider, "set_thumbnail_pos_ms"):
                self.positionSlider.set_thumbnail_pos_ms(v.get("thumbnail_pos_ms", 0))
            visible_segments = self.speed_segments if bool(getattr(self, "granular_checkbox", None) and self.granular_checkbox.isChecked()) else []
            self.positionSlider.set_speed_segments(visible_segments)
            self.positionSlider.update()
        if hasattr(self, "_set_music_button_state"): self._set_music_button_state(bool(getattr(self, "_wizard_tracks", [])))
        self._update_trim_widgets_from_trim_times()
        self._update_quality_label()
        if hasattr(self, "_update_granular_button_state"): self._update_granular_button_state()
        if hasattr(self, "_maybe_enable_process"): self._maybe_enable_process()
        self._restoring_recovery_state = False
        self._save_recovery_state()

    def _on_mpv_idle_changed(self, is_idle):
        if is_idle and self.wants_to_play: _safe_single_shot(0, self._safe_handle_mpv_end)

    def _update_overlay_positions(self):
        if hasattr(self, "_update_upload_hint_responsive"): self._update_upload_hint_responsive()
        try:
            if hasattr(self, "_resize_overlay"): self._resize_overlay()
            if hasattr(self, "timeline_overlay"):
                res = getattr(self, "original_resolution", "1920x1080")
                self.timeline_overlay.update_geometry(self.video_surface, res)
            if hasattr(self, "portrait_mask_overlay"): self._update_portrait_mask_overlay_state()
        except: pass

    def dragEnterEvent(self, event):
        if event.mimeData().hasUrls():
            event.accept()
            if hasattr(self, "upload_hint_container") and self.upload_hint_container and getattr(self, "_upload_hint_active", False):
                self.upload_hint_container.setStyleSheet(
                    'QFrame#uploadHintContainer {'
                    '  background-color: rgba(15, 23, 42, 0.92);'
                    '  border: 2px solid #7DD3FC;'
                    '  border-radius: 20px;'
                    '}'
                )
        else:
            event.ignore()

    def dragLeaveEvent(self, event):
        if hasattr(self, "upload_hint_container") and self.upload_hint_container and getattr(self, "_upload_hint_active", False):
            self.upload_hint_container.setStyleSheet(
                'QFrame#uploadHintContainer {'
                '  background-color: rgba(15, 23, 42, 0.82);'
                '  border: 2px dashed rgba(125, 211, 252, 0.4);'
                '  border-radius: 20px;'
                '}'
            )
        event.accept()

    def dropEvent(self, event):
        if hasattr(self, "upload_hint_container") and self.upload_hint_container and getattr(self, "_upload_hint_active", False):
            self.upload_hint_container.setStyleSheet(
                'QFrame#uploadHintContainer {'
                '  background-color: rgba(15, 23, 42, 0.82);'
                '  border: 2px dashed rgba(125, 211, 252, 0.4);'
                '  border-radius: 20px;'
                '}'
            )
        files = [u.toLocalFile() for u in event.mimeData().urls()]
        if files: self.handle_file_selection(files[0])

    def resizeEvent(self, event):
        self._update_overlay_positions()
        if hasattr(self, "_update_upload_hint_responsive"): self._update_upload_hint_responsive()
        MainWindowEventsMixin.resizeEvent(self, event)

    def mousePressEvent(self, event): MainWindowEventsMixin.mousePressEvent(self, event)

    def closeEvent(self, event): MainWindowEventsMixin.closeEvent(self, event)
VideoCompressorApp = FortniteVideoSoftware
