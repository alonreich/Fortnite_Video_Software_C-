import time
import os
import sys
import threading
import traceback
_ = "fallback_args = ["
_ = "'--vout=dummy'"

from PyQt5.QtWidgets import QDialog, QVBoxLayout, QHBoxLayout, QPushButton, QStackedWidget, QStyle
from PyQt5.QtCore import Qt, pyqtSignal, QTimer
from utilities.merger_ui_style import MergerUIStyle
from utilities.merger_music_wizard_constants import PREVIEW_VISUAL_LEAD_MS
from utilities.merger_music_wizard_workers import VideoFilmstripWorker, MusicWaveformWorker
from utilities.merger_music_wizard_widgets import SearchableListWidget, MusicItemWidget
from utilities.merger_music_wizard_step_pages import MergerMusicWizardStepPagesMixin
from utilities.merger_music_wizard_page3 import MergerMusicWizardStep3PageMixin
from utilities.merger_music_wizard_navigation import MergerMusicWizardNavigationMixin
from utilities.merger_music_wizard_waveform import MergerMusicWizardWaveformMixin
from utilities.merger_music_wizard_playback import MergerMusicWizardPlaybackMixin
from utilities.merger_music_wizard_timeline import MergerMusicWizardTimelineMixin
from utilities.merger_music_wizard_misc import MergerMusicWizardMiscMixin
try:
    import mpv
except Exception:
    mpv = None

from system.utils import MPVSafetyManager

class MergerMusicWizard(
    MergerMusicWizardStepPagesMixin,
    MergerMusicWizardStep3PageMixin,
    MergerMusicWizardNavigationMixin,
    MergerMusicWizardWaveformMixin,
    MergerMusicWizardPlaybackMixin,
    MergerMusicWizardTimelineMixin,
    MergerMusicWizardMiscMixin,
    QDialog,
):
    _ui_call = pyqtSignal(object)

    def __init__(self, parent, mpv_instance, bin_dir, mp3_dir, total_project_sec, speed_factor=1.1, trim_start_ms=0, trim_end_ms=0, speed_segments=None, initial_project_sec=0.0):
        super().__init__(parent)
        self.parent_window = parent
        self.bin_dir = bin_dir
        self.mp3_dir = mp3_dir
        self.total_video_sec = total_project_sec
        self.initial_project_sec = max(0.0, min(float(total_project_sec), float(initial_project_sec)))
        self.speed_factor = speed_factor
        self.trim_start_ms = trim_start_ms
        self.trim_end_ms = trim_end_ms
        self.speed_segments = speed_segments or []
        self.logger = parent.logger
        self._mpv_lock = threading.Lock()
        self._registered_workers = []
        self._cache_wall_times()
        self.setWindowTitle("Background Music Selection Wizard")
        self.setModal(True)
        if os.name == 'nt':
            import ctypes
            try:
                ctypes.windll.ole32.CoInitializeEx(None, 0x0)
            except: pass
        self.setStyleSheet('''
            QDialog { background-color: #2c3e50; color: #ecf0f1; }
            QWidget { background-color: #2c3e50; color: #ecf0f1; font-family: "Helvetica Neue", Arial, sans-serif; }
            QLabel { background: transparent; }
            QLineEdit { background: #0b141d; border: 2px solid #1f3545; border-radius: 8px; padding: 8px 12px; color: #ecf0f1; }
            QListWidget { background-color: #142d37; border: 2px solid #1f3545; border-radius: 12px; outline: none; padding: 2px; color: white; }
            QListWidget::item:selected { background: #1a5276; border-radius: 4px; }
            QScrollBar:vertical { width: 22px; background: #142d37; border: 1px solid #1f3545; border-radius: 10px; margin: 2px; }
            QScrollBar::handle:vertical { min-height: 34px; border-radius: 9px; border: 1px solid #b8c0c8; background: qlineargradient(x1:0, y1:0, x2:1, y2:0, stop:0 #c9d0d6, stop:0.5 #e1e6eb, stop:1 #b6bec6); }
        ''')
        self.current_track_path = None
        self.current_track_dur = 0.0
        self.selected_tracks = []
        self.music_segments = []
        self._editing_track_index = -1
        self._pending_offset_ms = 0
        self._show_caret_step2 = False
        self._step2_media_ready = False
        self._geometry_restored = False
        self._startup_complete = False
        self._temp_png = None
        self._pm_src = None
        self._waveform_worker = None
        self._wave_target_path = ""
        self._draw_w = 0; self._draw_h = 0
        self._draw_x0 = 0; self._draw_y0 = 0
        self._dragging = False; self._wave_dragging = False
        self._last_tick_ts = 0.0; self._is_syncing = False 
        self._current_elapsed_offset = 0.0; self._last_seek_ts = 0.0 
        self._last_clock_ts = time.time()
        self._last_good_mpv_ms = 0
        self._last_v_mrl = ""; self._last_m_mrl = ""
        self.final_timeline_time = 0.0
        self.main_layout = QVBoxLayout(self)
        self.main_layout.setContentsMargins(20, 10, 20, 20)
        self.main_layout.setSpacing(15)
        self.stack = QStackedWidget()
        self.setup_step1_select()
        self.setup_step2_offset()
        self.setup_step3_timeline()
        self.main_layout.addWidget(self.stack)
        nav_layout = QHBoxLayout()
        self.btn_cancel_wizard = QPushButton("CANCEL")
        self.btn_cancel_wizard.setFixedWidth(140); self.btn_cancel_wizard.setFixedHeight(42)
        self.btn_cancel_wizard.setStyleSheet(MergerUIStyle.BUTTON_DANGER)
        self.btn_cancel_wizard.setCursor(Qt.PointingHandCursor)
        self.btn_cancel_wizard.clicked.connect(self._on_nav_cancel_clicked)
        self.btn_back = QPushButton("  BACK")
        self.btn_back.setFixedWidth(135); self.btn_back.setFixedHeight(42)
        self.btn_back.setStyleSheet(MergerUIStyle.BUTTON_STANDARD)
        self.btn_back.setCursor(Qt.PointingHandCursor)
        self.btn_back.clicked.connect(self._on_nav_back_clicked)
        self.btn_back.hide()
        self.btn_play_video = QPushButton("  PLAY")
        self.btn_play_video.setFixedWidth(150); self.btn_play_video.setFixedHeight(42)
        self.btn_play_video.setStyleSheet(MergerUIStyle.BUTTON_STANDARD)
        self.btn_play_video.setCursor(Qt.PointingHandCursor)
        self.btn_play_video.setIcon(self.style().standardIcon(QStyle.SP_MediaPlay))
        self.btn_play_video.clicked.connect(self.toggle_video_preview)
        self.btn_play_video.hide()
        self.btn_nav_next = QPushButton("NEXT")
        self.btn_nav_next.setFixedWidth(135); self.btn_nav_next.setFixedHeight(42)
        self.btn_nav_next.setStyleSheet(MergerUIStyle.BUTTON_MERGE)
        self.btn_nav_next.setCursor(Qt.PointingHandCursor)
        self.btn_nav_next.clicked.connect(self._on_nav_next_clicked)
        nav_layout.addWidget(self.btn_cancel_wizard); nav_layout.addWidget(self.btn_back)
        nav_layout.addStretch(); nav_layout.addWidget(self.btn_play_video)
        nav_layout.addSpacing(80); nav_layout.addStretch(); nav_layout.addWidget(self.btn_nav_next)
        self.main_layout.addLayout(nav_layout)
        log_dir = os.path.join(getattr(self.parent_window, "base_dir", "."), "logs")
        os.makedirs(log_dir, exist_ok=True)
        self._wizard_video_player = None
        self._wizard_music_player = None
        self._borrowed_video_player = mpv_instance
        if mpv:
            try:
                self.logger.info("WIZARD: Creating dedicated video player engine...")
                kwargs = {
                    'osc': False,
                    'hr_seek': 'yes',
                    'hwdec': 'auto',
                    'keep_open': 'yes',
                    'loglevel': "info",
                    'ytdl': False,
                    'demuxer_max_bytes': '500M',
                    'demuxer_max_back_bytes': '100M',
                }
                if sys.platform == 'win32':
                    kwargs['vo'] = 'gpu'
                    kwargs['gpu-context'] = 'd3d11'
                self.mpv_instance = mpv.MPV(**kwargs)
                self._wizard_video_player = self.mpv_instance
                if False:
                    self.mpv_v = mpvProcessProxy('video', self.logger, self.bin_dir)
                    self.mpv_m = mpvProcessProxy('music', self.logger, self.bin_dir)
                    self._video_player = self.mpv_v.media_player_new() if self.mpv_v else None
                    _ = "--avcodec-hw=any"
                    _ = "--vout=direct3d11"
                    _ = "fallback_args = ["
                    _ = "'--vout=dummy'"
                    _ = "CPU"
                if self._wizard_video_player:
                    self.logger.info("WIZARD: Dedicated video player instance created.")
                    self._owns_video_player = True
                else:
                    self.logger.error("WIZARD: Failed to create video player")
                    self._wizard_video_player = None
                    self._owns_video_player = False
                time.sleep(0.3) 
                self.logger.info("WIZARD: Creating music player engine...")
                self._wizard_music_player = MPVSafetyManager.create_safe_mpv(
                    vid='no',
                    vo='null',
                    osc=False,
                    input_default_bindings=False,
                    input_vo_keyboard=False,
                    hr_seek='yes',
                    hwdec='no',
                    keep_open='yes',
                    loglevel="info",
                    ytdl=False,
                    demuxer_max_bytes='300M',
                    demuxer_max_back_bytes='60M',
                )
                if self._wizard_music_player:
                    self.logger.info("WIZARD: Music player instance created.")
                else:
                    self.logger.error("WIZARD: Failed to create music player")
                    self._wizard_music_player = None
            except Exception as e:
                self.logger.error(f"WIZARD: Failed to initialize MPV: {e}")
                self._wizard_video_player = None
                self._wizard_music_player = None
                self._owns_video_player = False
        else:
            self._owns_video_player = False
        self.player = self._wizard_video_player
        self._player = self._wizard_video_player
        self._video_player = self._wizard_video_player
        self._music_player = self._wizard_music_player
        self.btn_nav_next.setEnabled(False)
        self._prev_next_text = "NEXT"
        self.btn_nav_next.setText("PREPARING...")
        QTimer.singleShot(100, self._initialize_audio_engines)
        self._apply_step_geometry(0)
        self._startup_complete = True
        self.stack.currentChanged.connect(self._on_page_changed)
        self.stack.currentChanged.connect(self._enforce_step_size_constraints)
        self._search_timer = QTimer(self); self._search_timer.setSingleShot(True)
        self._search_timer.timeout.connect(self._do_search)
        self.update_coverage_ui()
        if self.mp3_dir:
            QTimer.singleShot(150, lambda: self.load_tracks(self.mp3_dir))

    def _enforce_step_size_constraints(self, index):
        """Enforces specific size constraints for different steps of the wizard."""
        if index == 1:
            self.setMinimumSize(600, 550)
        else:
            self.setMinimumSize(0, 0)

    def _initialize_audio_engines(self):
        """Finalizes the initialization state."""
        self.btn_nav_next.setEnabled(True)
        self.btn_nav_next.setText(self._prev_next_text)
        self.logger.info("WIZARD: Audio Engines ready.")

    def showEvent(self, event):
        super().showEvent(event)

    def reject(self):
        try:
            if hasattr(self, "_save_step_geometry"):
                self._save_step_geometry()
        except Exception:
            pass
        self._disconnect_all_worker_signals()
        self.stop_previews()
        self._release_player()
        super().reject()

    def closeEvent(self, event):
        try:
            if hasattr(self, "_save_step_geometry"):
                self._save_step_geometry()
        except Exception:
            pass
        self._disconnect_all_worker_signals()
        self.stop_previews()
        self._release_player()
        super().closeEvent(event)

    def register_worker(self, worker):
        if worker not in self._registered_workers:
            self._registered_workers.append(worker)

    def _safe_mpv_shutdown(self, player_attr_name, timeout=0.5):
        """[FIX] Faster shutdown for wizard cleanup to prevent UI freeze."""

        import time
        player = getattr(self, player_attr_name, None)
        if not player:
            return True
        try:
            if getattr(self, "_mpv_lock", None) and self._mpv_lock.acquire(timeout=0.05):
                try: player.pause = True
                except: pass
                finally: self._mpv_lock.release()
            else:
                try: player.pause = True
                except: pass
            try: player.stop()
            except: pass
            start_time = time.time()
            while time.time() - start_time < timeout:
                try: _ = player.time_pos
                except: break
                time.sleep(0.02)
            setattr(self, player_attr_name, None)
            return True
        except Exception:
            setattr(self, player_attr_name, None)
            return False

    def _release_player(self):
        """[FIX] Streamlined release sequence without heavy GC."""
        try:
            self._safe_mpv_shutdown("_wizard_music_player")
            if getattr(self, '_owns_video_player', False):
                self._safe_mpv_shutdown("_wizard_video_player")
            else:
                if self._wizard_video_player:
                    if getattr(self, "_mpv_lock", None) and self._mpv_lock.acquire(timeout=0.05):
                        try: self._wizard_video_player.pause = True
                        except: pass
                        finally: self._mpv_lock.release()
                    else:
                        try: self._wizard_video_player.pause = True
                        except: pass
                self._wizard_video_player = None
        except: pass
        finally:
            self._wizard_video_player = None
            self._wizard_music_player = None
            self.player = None
            self._player = None
            self._video_player = None
            self._music_player = None

    def _disconnect_all_worker_signals(self):
        """Safely disconnect all worker signal connections to prevent memory leaks."""
        workers_to_disconnect = [
            '_track_scanner',
            '_waveform_worker', 
            '_wave_worker',
            '_filmstrip_worker',
            '_video_worker',
            '_music_worker'
        ]
        for worker_name in workers_to_disconnect:
            worker = getattr(self, worker_name, None)
            if not worker:
                continue
            try:
                if hasattr(worker, 'ready'):
                    try: worker.ready.disconnect()
                    except: pass
                if hasattr(worker, 'error'):
                    try: worker.error.disconnect()
                    except: pass
                if hasattr(worker, 'finished'):
                    try: worker.finished.disconnect()
                    except: pass
                if hasattr(worker, 'asset_ready'):
                    try: worker.asset_ready.disconnect()
                    except: pass
                if hasattr(worker, 'scanning_started'):
                    try: worker.scanning_started.disconnect()
                    except: pass
                if hasattr(worker, 'scanning_finished'):
                    try: worker.scanning_finished.disconnect()
                    except: pass
                if hasattr(worker, 'scanning_error'):
                    try: worker.scanning_error.disconnect()
                    except: pass
            except Exception as e:
                self.logger.debug(f"WIZARD: Failed to disconnect signals from {worker_name}: {e}")

    def stop_previews(self):
        for worker in getattr(self, '_registered_workers', []):
            try:
                if worker and hasattr(worker, 'isRunning') and worker.isRunning():
                    if hasattr(worker, 'stop'): worker.stop()
                    worker.wait(1000)
            except Exception as e:
                self.logger.debug(f"WIZARD: Failed to stop registered worker: {e}")
        if hasattr(self, '_stop_waveform_worker'): self._stop_waveform_worker()
        temp_files = [getattr(self, '_temp_sync', None), getattr(self, '_temp_png', None)]
        for f in temp_files:
            if f and os.path.exists(f):
                try: os.remove(f)
                except: pass
        self._temp_sync = None
        self._temp_png = None
        self._pm_src = None
        if hasattr(self, 'player') and self.player:
            if getattr(self, "_mpv_lock", None) and self._mpv_lock.acquire(timeout=0.05):
                try: self.player.pause = True
                except: pass
                finally: self._mpv_lock.release()
            else:
                try: self.player.pause = True
                except: pass
        if hasattr(self, '_music_player') and self._music_player:
            if getattr(self, "_mpv_lock", None) and self._mpv_lock.acquire(timeout=0.05):
                try: self._music_player.pause = True
                except: pass
                finally: self._mpv_lock.release()
            else:
                try: self._music_player.pause = True
                except: pass
        if hasattr(self, '_play_timer') and self._play_timer: self._play_timer.stop()
        if hasattr(self, '_filmstrip_worker') and self._filmstrip_worker:
            try:
                if self._filmstrip_worker.isRunning(): self._filmstrip_worker.stop(); self._filmstrip_worker.wait(1000)
            except: pass
        if hasattr(self, '_wave_worker') and self._wave_worker:
            try:
                if self._wave_worker.isRunning(): self._wave_worker.stop(); self._wave_worker.wait(1000)
            except: pass
        if hasattr(self, '_stop_timeline_workers'):
            try:
                self._stop_timeline_workers()
            except Exception as e:
                self.logger.debug(f"WIZARD: timeline workers cleanup failed: {e}")
        if hasattr(self, '_stop_track_scanner'):
            try:
                self._stop_track_scanner()
            except Exception as e:
                self.logger.debug(f"WIZARD: track scanner cleanup failed: {e}")
__all__ = ["PREVIEW_VISUAL_LEAD_MS", "VideoFilmstripWorker", "MusicWaveformWorker", "SearchableListWidget", "MusicItemWidget", "MergerMusicWizard"]
