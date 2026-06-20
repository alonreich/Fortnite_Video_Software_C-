from PyQt5.QtWidgets import QMainWindow, QApplication, QPushButton, QMessageBox, QShortcut, QDialog
from PyQt5.QtCore import pyqtSignal, Qt, QTimer, QEvent, QMutex, QMutexLocker
from PyQt5.QtGui import QIcon, QKeySequence, QDesktopServices
import sys
import os
sys.dont_write_bytecode = True
os.environ['PYTHONDONTWRITEBYTECODE'] = '1'

import tempfile
import subprocess
import time
import shutil
from pathlib import Path
from system.state_transfer import StateTransfer
from utilities.merger_ui import MergerUI
from utilities.merger_handlers_main import MergerHandlers
from utilities.merger_utils import _get_logger, _human, escape_ffmpeg_path, get_disk_free_space, _ffprobe, build_audio_ducking_filters
from utilities.merger_window_logic import MergerWindowLogic
from utilities.workers import ProbeWorker
from utilities.merger_engine import MergerEngine
from utilities.merger_draggable_list import MergerDraggableList
from utilities.merger_phase_overlay_mixin import MergerPhaseOverlayMixin
from utilities.merger_phase_overlay_logic import MergerPhaseOverlayLogic
from utilities.merger_phase_overlay_draw import MergerPhaseOverlayDraw
from utilities.merger_music_dialog import MusicDialogHandler
from ui.widgets.spinning_wheel_slider import SpinningWheelSlider

class VideoMergerWindow(QMainWindow, MergerPhaseOverlayMixin, MergerPhaseOverlayLogic, MergerPhaseOverlayDraw):
    MAX_FILES = 100
    status_updated = pyqtSignal(str)
    return_to_main = pyqtSignal()

    def __init__(self, ffmpeg_path: str | None = None, parent: QMainWindow | None = None, mpv_instance=None, bin_dir: str = '', config_manager=None, base_dir: str = ''):
        super().__init__(parent)
        self._loaded = False
        self.base_dir = base_dir
        self.ffmpeg = ffmpeg_path or "ffmpeg"
        self.mpv_instance = mpv_instance
        self.bin_dir = bin_dir
        self.original_duration = 0.0
        self.process = None
        self._pulse_phase = 0
        self._cfg = config_manager.config if config_manager else {}
        self.config_manager = config_manager
        self.logger = _get_logger()
        self.logic_handler = MergerWindowLogic(self)
        self.ui_handler = MergerUI(self)
        self.event_handler = MergerHandlers(self)
        self.music_dialog_handler = MusicDialogHandler(self)
        self.engine = None
        self._state_mutex = QMutex()
        self._is_processing = False
        self._is_cancelling = False
        self._status_lock_until = 0.0
        self._temp_dir = None
        self._probe_worker = None
        self._last_gpu_val = 0
        self._iops_prev = None
        self._iops_dyn_max = 1.0
        self._cleanup_stale_temps()
        self.init_ui()
        self.logic_handler.load_config()
        self._setup_recovery_manager()
        self._restore_state_transfer_session()
        self.connect_signals()
        self.setAcceptDrops(True)
        QTimer.singleShot(100, self._scan_mp3_folder)
        QTimer.singleShot(500, self._restore_recovery_state)
        self._original_merge_btn_style = """
            QPushButton {
                background-color: #1b6d26;
                color: white;
                font-weight: bold;
                font-size: 12px;
                border-radius: 10px;
            }
            QPushButton:hover { background-color: #22822d; }
            QPushButton:disabled { background-color: #7f8c8d; color: #bdc3c7; }
        """
        self.event_handler.update_button_states()
        self.logger.info("OPEN: Video Merger window created")

    def _restore_state_transfer_session(self):
        try:
            state = StateTransfer.load_state()
            if not state:
                return
            self.logger.info("STATE_TRANSFER: Loading session from main app...")
            input_file = state.get("input_file")
            if input_file and os.path.exists(input_file):
                self.event_handler.add_videos_from_list([input_file])
            StateTransfer.clear_state()
        except Exception as e:
            self.logger.error(f"STATE_TRANSFER: Failed to load state: {e}")

    def _setup_recovery_manager(self):
        from system.recovery_manager import RecoveryManager
        self.recovery_manager = RecoveryManager("video_merger", self.logger)
        self.recovery_timer = QTimer(self)
        self.recovery_timer.timeout.connect(self._save_recovery_state)
        self.recovery_timer.start(5000)

    def _save_recovery_state(self):
        if not hasattr(self, "recovery_manager"): return
        video_files = []
        for i in range(self.listw.count()):
            it = self.listw.item(i)
            video_files.append({
                "path": it.data(Qt.UserRole),
                "probe_data": it.data(Qt.UserRole + 1),
                "hash": it.data(Qt.UserRole + 2),
                "clip_id": it.data(Qt.UserRole + 3)
            })
        state = {
            "assets": {
                "video_files": video_files,
                "wizard_tracks": self.unified_music_widget.get_wizard_tracks() if hasattr(self.unified_music_widget, "get_wizard_tracks") else []
            },
            "volatile_settings": {
                "video_volume": self.unified_music_widget.get_video_volume() if hasattr(self.unified_music_widget, "get_video_volume") else 100,
                "music_volume": self.unified_music_widget.get_volume() if hasattr(self.unified_music_widget, "get_volume") else 80,
                "quality_level": self.quality_slider.value() if hasattr(self, "quality_slider") else 7
            },
            "ui_dynamics": {
                "window_geometry_base64": bytes(self.saveGeometry().toBase64()).decode("utf-8"),
                "last_dir": self._last_dir,
                "last_out_dir": self._last_out_dir
            }
        }
        self.recovery_manager.save_state_async(state)

    def _restore_recovery_state(self):
        if os.environ.get("FVS_RESTORE_SESSION") != "1": return
        state = self.recovery_manager.load_state()
        if not state: return
        self.logger.info("RECOVERY: Restoring previous session state...")
        a = state.get("assets", {})
        v = state.get("volatile_settings", {})
        u = state.get("ui_dynamics", {})
        video_files = a.get("video_files", [])
        if video_files:
            self.event_handler.clear_all()
            for item in video_files:
                p = item.get("path")
                if p and os.path.exists(p):
                    self.event_handler._add_single_item_internal(
                        p,
                        probe_data=item.get("probe_data"),
                        f_hash=item.get("hash"),
                        clip_id=item.get("clip_id")
                    )
        if hasattr(self, "unified_music_widget"):
            w_tracks = a.get("wizard_tracks", [])
            if w_tracks:
                self.unified_music_widget.apply_state({
                    "wizard_tracks": w_tracks,
                    "video_volume": v.get("video_volume", 100),
                    "music_volume": v.get("music_volume", 80)
                })
        if hasattr(self, "quality_slider"):
            self.quality_slider.setValue(v.get("quality_level", 7))
        self.event_handler.update_button_states()

    def showEvent(self, event):
        if not getattr(self, "_loaded", False):
            self._loaded = True
        super().showEvent(event)

    def create_draggable_list_widget(self):
        listw = MergerDraggableList(self)
        if hasattr(listw, "drag_started"):
            listw.drag_started.connect(self.event_handler.on_drag_started)
        if hasattr(listw, "drag_completed"):
            listw.drag_completed.connect(self.event_handler.on_drag_completed)
        if hasattr(listw, "files_dropped"):
            listw.files_dropped.connect(self._handle_dropped_files)
        return listw

    def add_videos(self):
        self.event_handler.add_videos()

    def remove_selected(self):
        self.logger.info("USER: Clicked REMOVE SELECTED")
        self.event_handler.remove_selected()

    def move_item(self, direction):
        self.event_handler.move_item(direction)

    def return_to_main_app(self):
        self.logger.info("USER: Clicked RETURN TO MENU")
        if self.is_processing:
            msg = QMessageBox(self)
            msg.setIcon(QMessageBox.Information)
            msg.setWindowTitle("Merge in progress")
            msg.setText("Please cancel the merge first, then return to menu.")
            for btn in msg.findChildren(QPushButton): btn.setCursor(Qt.PointingHandCursor)
            msg.exec_()
            return
        if self.listw.count() > 0 and not self.is_processing:
            msg = QMessageBox(self)
            msg.setIcon(QMessageBox.Question)
            msg.setWindowTitle("Return to menu")
            msg.setText("You still have videos in the list. Return to menu anyway?")
            msg.setStandardButtons(QMessageBox.Yes | QMessageBox.No)
            msg.setDefaultButton(QMessageBox.No)
            for btn in msg.findChildren(QPushButton): btn.setCursor(Qt.PointingHandCursor)
            reply = msg.exec_()
            if reply != QMessageBox.Yes:
                return
        self.return_to_main.emit()

    def perform_move(self, from_row, to_row, rebuild_widget=False):
        self.logic_handler.perform_move(from_row, to_row, rebuild_widget)

    def make_item_widget(self, path):
        return self.event_handler.make_item_widget(path)

    def set_ui_busy(self, busy: bool):
        self.btn_add.setEnabled(not busy)
        self.btn_add_folder.setEnabled(not busy)
        self.btn_remove.setEnabled(not busy)
        self.btn_clear.setEnabled(not busy)
        self.btn_merge.setEnabled(not busy)
        self.btn_back.setEnabled(not busy)
        self.listw.setEnabled(not busy)
        undo_enabled = False
        redo_enabled = False
        if (not busy) and hasattr(self, 'event_handler') and hasattr(self.event_handler, 'undo_stack'):
            try:
                undo_enabled = self.event_handler.undo_stack.canUndo()
                redo_enabled = self.event_handler.undo_stack.canRedo()
            except RuntimeError:
                undo_enabled = False
                redo_enabled = False
        if hasattr(self, 'btn_undo'):
            self.btn_undo.setEnabled(undo_enabled)
        if hasattr(self, 'btn_redo'):
            self.btn_redo.setEnabled(redo_enabled)

    def setup_progress_visualization(self):
        if not hasattr(self, "_progress_samples"):
            self._progress_samples = []

    def _cleanup_stale_temps(self):
        try:
            tmp = Path(tempfile.gettempdir())
            now = time.time()
            for p in tmp.glob("fvs_merger_*"):
                if p.is_dir() and (p / ".fvs_merger_tmp").exists():
                    try:
                        mtime = p.stat().st_mtime
                        if now - mtime > 1800:
                            shutil.rmtree(p, ignore_errors=True)
                            self.logger.info(f"Cleaned stale temp: {p}")
                    except Exception as ex:
                        self.logger.debug(f"Temp cleanup skip for {p}: {ex}")
        except Exception as e:
            self.logger.warning(f"Temp cleanup failed: {e}")

    def _handle_dropped_files(self, files):
        if self.is_processing:
            return
        allowed = {'.mp4', '.mov', '.mkv', '.m4v', '.ts', '.avi', '.webm'}
        supported = [f for f in files if Path(f).suffix.lower() in allowed]
        skipped = len(files) - len(supported)
        if supported:
            self.event_handler.add_videos_from_list(supported)
            if skipped > 0:
                self.set_status_message(
                    f"Added {len(supported)} file(s). Skipped {skipped} unsupported file(s).",
                    "color: #ffa500;",
                    2500,
                    force=True,
                )
            return
        if files and skipped > 0:
            self.set_status_message(
                "No supported video files were dropped. Supported: mp4/mov/mkv/m4v/ts/avi/webm",
                "color: #ff6b6b;",
                3500,
                force=True,
            )
    @property
    def is_processing(self) -> bool:
        with QMutexLocker(self._state_mutex):
            return self._is_processing

    def set_processing_state(self, value: bool) -> bool:
        with QMutexLocker(self._state_mutex):
            if value:
                if self._is_processing or self._is_cancelling:
                    return False
                self._is_processing = True
                return True
            self._is_processing = False
            if not self._is_processing:
                self._is_cancelling = False
            return True

    def request_cancellation(self) -> bool:
        with QMutexLocker(self._state_mutex):
            if not self._is_processing or self._is_cancelling:
                return False
            self._is_cancelling = True
            return True

    def dragEnterEvent(self, event):
        if event.mimeData().hasUrls() and not self.is_processing:
            event.acceptProposedAction()
            return
        event.ignore()

    def dropEvent(self, event):
        if self.is_processing:
            event.ignore()
            return
        urls = event.mimeData().urls() if event.mimeData().hasUrls() else []
        files = [u.toLocalFile() for u in urls if u.isLocalFile()]
        self._handle_dropped_files(files)
        if files:
            event.acceptProposedAction()
        else:
            event.ignore()

    def resizeEvent(self, event: QEvent):
        super().resizeEvent(event)
        if hasattr(self, "_resize_overlay"):
            self._resize_overlay()
        if hasattr(self, "logic_handler"):
            try:
                self.logic_handler.request_save_config(400)
            except Exception:
                pass

    def moveEvent(self, event: QEvent):
        super().moveEvent(event)
        if hasattr(self, "logic_handler"):
            try:
                self.logic_handler.request_save_config(400)
            except Exception:
                pass

    def init_ui(self):
        self.setWindowTitle("Video Merger")
        self.resize(1000, 700)

        from PyQt5.QtWidgets import QProgressBar
        self.progress_bar = QProgressBar()
        self.ui_handler.setup_ui()
        self.ui_handler.set_style()
        self._original_merge_btn_style = self.btn_merge.styleSheet()
        self.set_icon()
        self._ensure_overlay_widgets()
        self._pulse_timer = QTimer(self)
        self._pulse_timer.timeout.connect(self._pulse_button_color)
        self._pulse_phase = 0 # Initialize phase
        self.setup_progress_visualization()
        self.setup_keyboard_shortcuts()
        
    def setup_keyboard_shortcuts(self):
        self.merge_shortcut = QShortcut(QKeySequence("Ctrl+Return"), self)
        self.merge_shortcut.activated.connect(self._shortcut_merge)
        self.add_shortcut = QShortcut(QKeySequence("Ctrl+O"), self)
        self.add_shortcut.activated.connect(self._shortcut_add)
        self.move_up_shortcut = QShortcut(QKeySequence("Ctrl+Up"), self)
        self.move_up_shortcut.activated.connect(lambda: self._shortcut_move(-1))
        self.move_down_shortcut = QShortcut(QKeySequence("Ctrl+Down"), self)
        self.move_down_shortcut.activated.connect(lambda: self._shortcut_move(1))

    def _is_ui_busy_for_actions(self) -> bool:
        return bool(self.is_processing or getattr(self.event_handler, "_loading_lock", False))

    def _shortcut_merge(self):
        if self._is_ui_busy_for_actions():
            return
        self.on_merge_clicked()

    def _shortcut_add(self):
        if self._is_ui_busy_for_actions():
            return
        self.add_videos()

    def _shortcut_move(self, direction: int):
        if self._is_ui_busy_for_actions():
            return
        self.move_item(direction)

    def set_icon(self):
        try:
            _proj_root_path = Path(self.base_dir) if self.base_dir else Path(__file__).resolve().parents[1]
            icon_path = _proj_root_path / "icons" / "Video_Icon_File.ico"
            if not icon_path.exists():
                icon_path = _proj_root_path / "icons" / "app_icon.ico"
            if icon_path.exists():
                self.setWindowIcon(QIcon(str(icon_path)))
        except Exception:
            pass

    def connect_signals(self):
        self.event_handler.setup_list_connections()
        self.listw.itemSelectionChanged.connect(self.event_handler.update_button_states)
        self.listw.itemSelectionChanged.connect(self.event_handler.refresh_selection_highlights)
        self.status_updated.connect(self.handle_status_update)
        self.listw.model().rowsInserted.connect(self.event_handler.update_button_states)
        self.listw.model().rowsRemoved.connect(self.event_handler.update_button_states)
        self.listw.model().rowsRemoved.connect(self.on_list_cleared)
        self.listw.model().rowsMoved.connect(self.event_handler.update_button_states)
        self.listw.model().rowsMoved.connect(self.event_handler.on_rows_moved)
        self.btn_add.clicked.connect(self.add_videos)
        self.btn_add_folder.clicked.connect(self.event_handler.add_folder)
        self.btn_remove.clicked.connect(self.remove_selected)
        self.btn_clear.clicked.connect(self.confirm_clear_list)
        self.listw.itemSelectionChanged.connect(self.event_handler.on_selection_changed)

    def confirm_clear_list(self):
        if self.listw.count() > 0:
            msg = QMessageBox(self)
            msg.setIcon(QMessageBox.Question)
            msg.setWindowTitle('Confirm Clear')
            msg.setText("Are you sure you want to remove all videos from the list?")
            msg.setStandardButtons(QMessageBox.Yes | QMessageBox.No)
            msg.setDefaultButton(QMessageBox.No)
            for btn in msg.findChildren(QPushButton): btn.setCursor(Qt.PointingHandCursor)
            reply = msg.exec_()
            if reply == QMessageBox.Yes:
                self.logger.info("USER: Confirmed CLEAR ALL")
                self.event_handler.clear_all()
            else:
                self.logger.info("USER: Cancelled CLEAR ALL")

    def closeEvent(self, event):
        self._stop_all_workers()
        self.logic_handler.save_config()
        super().closeEvent(event)

    def _stop_all_workers(self):
        if hasattr(self, '_stats_worker') and self._stats_worker:
            try:
                self._stats_worker.stop()
                self._stats_worker = None
            except Exception as ex:
                self.logger.debug(f"Stats worker stop skip: {ex}")
        if self.engine and self.engine.isRunning():
            try:
                self.engine.cancel()
                self.engine.wait(2000)
            except Exception as ex:
                self.logger.debug(f"Engine stop skip: {ex}")
        if self._probe_worker and self._probe_worker.isRunning():
            try:
                if hasattr(self._probe_worker, 'abort'):
                    self._probe_worker.abort()
                else:
                    self._probe_worker.cancel()
                self._probe_worker.wait(1500)
            except Exception as ex:
                self.logger.debug(f"Probe worker stop skip: {ex}")
        loader = getattr(getattr(self, "event_handler", None), "_loader", None)
        if loader and loader.isRunning():
            try:
                if hasattr(loader, 'abort'): loader.abort()
                else: loader.cancel()
                loader.wait(2000)
            except Exception as ex:
                self.logger.debug(f"Loader stop skip: {ex}")

    def handle_status_update(self, msg: str):
        if hasattr(self, "btn_processing") and self.btn_processing.isVisible():
            dots = "." * (getattr(self, "_pulse_phase", 0) % 4)
            self.btn_processing.setText(f"MERGING{dots}")
        self.set_status_message(f"Processing... {msg}", "color: #43b581; font-weight: normal;", 1500)

    def set_status_message(self, msg: str, style: str | None = None, lock_ms: int = 0, force: bool = False):
        if not force and self.is_status_locked():
            return
        if style:
            self.status_label.setStyleSheet(style)
        self.status_label.setText(msg)
        if lock_ms > 0:
            self._status_lock_until = time.time() + (lock_ms / 1000.0)
        else:
            self._status_lock_until = 0.0

    def is_status_locked(self) -> bool:
        return time.time() < self._status_lock_until

    def on_list_cleared(self):
        if self.listw.count() == 0:
            self._reset_music_player()

    def on_merge_clicked(self):
        self.start_merge_processing()

    def _human_time(self, seconds: float) -> str:
        try:
            total = max(0, int(round(float(seconds))))
        except Exception:
            total = 0
        h = total // 3600
        m = (total % 3600) // 60
        s = total % 60
        return f"{h:02}:{m:02}:{s:02}"

    def estimate_total_duration_seconds(self) -> float:
        total = 0.0
        for i in range(self.listw.count()):
            try:
                it = self.listw.item(i)
                probe_data = it.data(Qt.UserRole + 1) or {}
                dur = float((probe_data.get("format") or {}).get("duration") or 0.0)
                total += max(0.0, dur)
            except Exception:
                continue
        try:
            if hasattr(self, "unified_music_widget"):
                self.unified_music_widget.set_video_total_seconds(total)
        except Exception:
            pass
        return total

    def estimate_total_duration_text(self) -> str:
        total = self.estimate_total_duration_seconds()
        return self._human_time(total) if total > 0 else ""

    def _probe_media_duration(self, path: str) -> float:
        try:
            ffprobe = _ffprobe(self.ffmpeg)
            cmd = [
                ffprobe,
                "-v", "error",
                "-show_entries", "format=duration",
                "-of", "default=noprint_wrappers=1:nokey=1",
                path,
            ]
            flags = subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0
            r = subprocess.run(cmd, capture_output=True, text=True, creationflags=flags, timeout=6)
            if r.returncode == 0 and r.stdout:
                return max(0.0, float(r.stdout.strip()))
        except Exception:
            pass
        return 0.0

    def _estimate_required_output_bytes(self, video_files: list[str]) -> int:
        total_in = 0
        for p in video_files:
            try:
                total_in += os.path.getsize(p)
            except Exception:
                continue
        return max(int(total_in * 1.05), 300 * 1024 * 1024)

    def _collect_preflight_warnings(self) -> list[str]:
        warnings = []
        for i in range(self.listw.count()):
            it = self.listw.item(i)
            p = it.data(Qt.UserRole)
            probe_data = it.data(Qt.UserRole + 1) or {}
            streams = probe_data.get("streams") or []
            has_video = any((s.get("codec_type") == "video") or (s.get("width") and s.get("height")) for s in streams)
            if not has_video:
                warnings.append(f"Row {i+1}: {os.path.basename(str(p))} (video stream not detected)")
        return warnings

    def _get_next_output_path(self):
        r"""
        Get the output path in the user's Downloads folder.
        Name: Merged-Videos-X.mp4
        """
        try:
            output_dir = Path(os.path.expanduser("~")) / "Downloads"
            output_dir.mkdir(parents=True, exist_ok=True)
            i = 1
            while True:
                name = f"Merged-Videos-{i}.mp4"
                p = output_dir / name
                if not p.exists():
                    return str(p)
                i += 1
        except Exception as e:
            self.logger.error(f"Path generation failed: {e}")
            fallback_dir = Path(self.base_dir).resolve() if self.base_dir else Path.cwd()
            return str((fallback_dir / f"Merged-Videos-{int(time.time())}.mp4").resolve())

    def start_merge_processing(self):
        if not self.set_processing_state(True):
            return
        if getattr(self.event_handler, "_loading_lock", False):
            self.set_processing_state(False)
            QMessageBox.information(self, "Please wait", "Still loading files. Please wait until loading finishes.")
            return
        n = self.listw.count()
        if n < 1:
            QMessageBox.information(self, "Need a video", "Please add at least 1 video to merge.")
            self.set_processing_state(False)
            return
        video_files = []
        for i in range(n):
            it = self.listw.item(i)
            video_files.append(it.data(Qt.UserRole))
        preflight_warnings = self._collect_preflight_warnings()
        if preflight_warnings:
            preview = "\n".join(preflight_warnings[:5])
            if len(preflight_warnings) > 5:
                preview += f"\n...and {len(preflight_warnings)-5} more"
            reply = QMessageBox.question(
                self,
                "Potential file compatibility issues",
                f"Some items may fail during merge:\n\n{preview}\n\nContinue anyway?",
                QMessageBox.Yes | QMessageBox.No,
                QMessageBox.No,
            )
            if reply != QMessageBox.Yes:
                self.set_processing_state(False)
                return
        wizard_tracks = self.unified_music_widget.get_wizard_tracks() if hasattr(self.unified_music_widget, "get_wizard_tracks") else []
        music_tracks = [t[0] for t in wizard_tracks] if wizard_tracks else (self.unified_music_widget.get_selected_tracks() if hasattr(self.unified_music_widget, "get_selected_tracks") else [])
        music_enabled = bool(music_tracks)
        music_path = wizard_tracks[0][0] if wizard_tracks else (music_tracks[0] if music_tracks else self.unified_music_widget.get_selected_track())
        music_offset = float(wizard_tracks[0][1]) if wizard_tracks else float(self.unified_music_widget.get_offset())
        if music_enabled and not music_tracks:
            QMessageBox.information(self, "Select music", "Music is enabled, but no track is selected.")
            self.set_processing_state(False)
            return
        if music_tracks:
            total_video = self.estimate_total_duration_seconds()
            try:
                self.unified_music_widget.update_coverage_guidance(total_video, self._probe_media_duration)
            except Exception:
                pass
            if wizard_tracks:
                music_unique_total = sum(max(0.0, float(t[2])) for t in wizard_tracks)
            else:
                music_unique_total = 0.0
                for t in music_tracks:
                    music_unique_total += self._probe_media_duration(t)
            if total_video > 0 and music_unique_total < total_video:
                missing = self._human_time(total_video - music_unique_total)
                reply = QMessageBox.question(
                    self,
                    "Music Coverage Warning",
                    f"Your selected music ({self._human_time(music_unique_total)}) is shorter than all videos ({self._human_time(total_video)}).\n\n"
                    f"You need about {missing} more music for full coverage.\n\n"
                    "If you continue, the rest of the video will be quiet.\n"
                    "Do you want to proceed?",
                    QMessageBox.Yes | QMessageBox.No,
                    QMessageBox.No,
                )
                if reply != QMessageBox.Yes:
                    self.set_processing_state(False)
                    return
            mus_dur = self._probe_media_duration(music_path)
            if mus_dur > 0 and music_offset >= max(0.0, mus_dur - 0.1):
                QMessageBox.warning(
                    self,
                    "Music start is too late",
                    "The selected music start offset is at/after the end of the track.\n"
                    "Please reduce the Start value.",
                )
                self.set_processing_state(False)
                return
        self._output_path = self._get_next_output_path()
        free_bytes = get_disk_free_space(os.path.dirname(os.path.abspath(self._output_path)))
        req_bytes = self._estimate_required_output_bytes(video_files)
        if free_bytes < req_bytes:
            if free_bytes < (req_bytes * 0.5):
                QMessageBox.critical(
                    self,
                    "Critically Low Disk Space",
                    f"You only have {_human(free_bytes)} available, but need at least {_human(req_bytes)}.\n"
                    "Please free up space to continue."
                )
                self.set_processing_state(False)
                return
            reply = QMessageBox.question(
                self,
                "Low Disk Space",
                f"Estimated required space: {_human(req_bytes)}\n"
                f"Available space: {_human(free_bytes)}\n\n"
                "Merge might fail if the output is larger than expected. Continue?",
                QMessageBox.Yes | QMessageBox.No,
                QMessageBox.No,
            )
            if reply != QMessageBox.Yes:
                self.set_processing_state(False)
                return
        est_text = self.estimate_total_duration_text()
        if est_text:
            self.set_status_message(f"Preparing merge. Estimated output length: {est_text}", "color: #43b581;", 2000, force=True)
        self._show_processing_overlay()
        self._pulse_timer.start(250)
        
        self.btn_merge.hide()
        self.btn_cancel.show()
        self.btn_processing.show()
        self.btn_processing.setEnabled(False)
        
        self.event_handler.update_button_states()
        self.logic_handler.request_save_config()
        self.set_status_message("Analyzing files...", "color: #43b581;", 0, force=True)
        self._probe_worker = ProbeWorker(video_files, self.ffmpeg)
        self._probe_worker.finished.connect(self._on_probe_finished)
        self._probe_worker.error.connect(self._on_probe_error)
        self._probe_worker.start()

    def _on_probe_finished(self, results, total_duration):
        try:
            self._validate_and_finalize(results, total_duration)
        except Exception as e:
            self.logger.exception(f"CRASH: Failed during _validate_and_finalize: {e}")
            self._merge_finished_cleanup(False, f"Crash in validation: {e}")

    def _on_probe_error(self, error_msg):
        msg = str(error_msg or "Probe failed")
        if "cancelled" in msg.lower():
            self._merge_finished_cleanup(False, "Cancelled by user.")
            return
        self._merge_finished_cleanup(False, msg)

    def _validate_and_finalize(self, results, total_duration):
        if not self.is_processing: return
        result_by_path = {r.get("path"): r for r in (results or []) if isinstance(r, dict)}
        video_files = []
        first_res = None
        normalize_video = False
        has_audio_input = False
        all_have_audio = True
        audio_plan = []
        total_v_bits = 0.0
        total_a_bits = 0.0
        total_v_dur = 0.0
        total_a_dur = 0.0
        peak_a_rate = 44100
        for i in range(self.listw.count()):
            it = self.listw.item(i)
            path = it.data(Qt.UserRole)
            video_files.append(path)
            info = result_by_path.get(path)
            if not info:
                self._merge_finished_cleanup(False, f"Probe data missing for file: {path}")
                return
            dur = float(info.get("duration") or 0.0)
            v_bitrate = info.get("video_bitrate", 0)
            a_bitrate = info.get("audio_bitrate", 0)
            if v_bitrate > 0 and dur > 0:
                total_v_bits += v_bitrate * dur
                total_v_dur += dur
            if a_bitrate > 0 and dur > 0:
                total_a_bits += a_bitrate * dur
                total_a_dur += dur
            peak_a_rate = max(peak_a_rate, info.get("audio_rate", 0))
            res = info.get("resolution")
            if not res or len(res) != 2 or not all(isinstance(v, int) and v > 0 for v in res):
                self._merge_finished_cleanup(False, f"Could not determine video resolution for: {path}")
                return
            if first_res is None:
                first_res = tuple(res)
            elif tuple(res) != first_res:
                normalize_video = True
            clip_has_audio = bool(info.get("has_audio"))
            has_audio_input = has_audio_input or clip_has_audio
            all_have_audio = all_have_audio and clip_has_audio
            audio_plan.append({
                "path": path,
                "duration": dur,
                "has_audio": clip_has_audio,
            })
        audio_mixed = has_audio_input and (not all_have_audio)
        if audio_mixed:
            normalize_video = True
        target_v_bitrate = int(total_v_bits / total_v_dur) if total_v_dur > 0 else 0
        target_a_bitrate = int(total_a_bits / total_a_dur) if total_a_dur > 0 else 192000
        if peak_a_rate == 0: peak_a_rate = 48000
        self._finalize_merge_setup(
            video_files,
            total_duration,
            has_audio_input,
            first_res,
            normalize_video,
            target_v_bitrate,
            target_a_bitrate,
            peak_a_rate,
            audio_plan=audio_plan,
            audio_mixed=audio_mixed,
        )

    def _finalize_merge_setup(
        self,
        video_files,
        total_duration=0.0,
        has_audio_input=False,
        target_resolution=None,
        normalize_video=False,
        target_v_bitrate=0,
        target_a_bitrate=0,
        target_a_rate=48000,
        audio_plan=None,
        audio_mixed=False,
    ):
        if not self.is_processing: return
        self._temp_dir = tempfile.TemporaryDirectory(prefix="fvs_merger_")
        try:
            Path(self._temp_dir.name, ".fvs_merger_tmp").write_text("fvs", encoding="utf-8")
        except Exception as e:
            self._merge_finished_cleanup(False, f"Failed to init temp dir: {e}")
            return
        wizard_tracks = []
        if hasattr(self.unified_music_widget, "get_wizard_tracks"):
            wizard_tracks = self.unified_music_widget.get_wizard_tracks()
        music_vol = self.unified_music_widget.get_volume()
        video_vol = self.unified_music_widget.get_video_volume()
        cmd = ["-y"]
        filters = []
        if normalize_video:
            tw, th = int(target_resolution[0]), int(target_resolution[1])
            for i, path in enumerate(video_files):
                cmd.extend(["-i", path])
                filters.append(
                    f"[{i}:v]scale={tw}:{th}:force_original_aspect_ratio=decrease:flags=lanczos,"
                    f"pad={tw}:{th}:(ow-iw)/2:(oh-ih)/2,setsar=1[v{i}]"
                )
            v_inputs = "".join(f"[v{i}]" for i in range(len(video_files)))
            filters.append(f"{v_inputs}concat=n={len(video_files)}:v=1:a=0[v_out]")
            map_video = "[v_out]"
            if has_audio_input:
                if audio_mixed and audio_plan:
                    for i, entry in enumerate(audio_plan):
                        dur = max(0.05, float(entry.get("duration") or 0.0))
                        if entry.get("has_audio"):
                            filters.append(
                                f"[{i}:a]aformat=sample_fmts=fltp:channel_layouts=stereo:sample_rates={target_a_rate},"
                                f"volume={video_vol/100.0}[a{i}]"
                            )
                        else:
                            filters.append(f"anullsrc=channel_layout=stereo:sample_rate={target_a_rate}:d={dur}[a{i}]")
                    a_inputs = "".join(f"[a{i}]" for i in range(len(video_files)))
                    filters.append(f"{a_inputs}concat=n={len(video_files)}:v=0:a=1[a_serial]")
                else:
                    a_inputs = "".join(f"[{i}:a]" for i in range(len(video_files)))
                    filters.append(f"{a_inputs}concat=n={len(video_files)}:v=0:a=1,volume={video_vol/100.0}[a_serial]")
                map_audio = "[a_serial]"
            else:
                map_audio = None
        else:
            concat_txt = Path(self._temp_dir.name, "concat_list.txt")
            with concat_txt.open("w", encoding="utf-8") as f:
                f.write("ffconcat version 1.0\n")
                for path in video_files:
                    f.write(f"file '{escape_ffmpeg_path(path)}'\n")
            cmd.extend(["-f", "concat", "-safe", "0", "-i", str(concat_txt)])
            map_video = "0:v"
            if has_audio_input:
                filters.append(f"[0:a]volume={video_vol/100.0}[a_vid_vol]")
                map_audio = "[a_vid_vol]"
            else:
                map_audio = None
        if wizard_tracks:
            music_start_idx = len(video_files) if normalize_video else 1
            fadeout_lead = 7.0
            crossfade_sec = 3.0
            expanded_tracks = []
            covered = 0.0
            for path, offset, dur in wizard_tracks:
                expanded_tracks.append((path, offset, dur))
                if len(expanded_tracks) == 1: covered += dur
                else: covered += max(0.0, dur - crossfade_sec)
            for t, _, _ in expanded_tracks:
                cmd.extend(["-i", t])
            music_inputs = []
            for idx, (track_path, start_sec, dur) in enumerate(expanded_tracks):
                in_idx = music_start_idx + idx
                out_label = f"m_{idx}"
                fade_start = max(0.0, dur - fadeout_lead)
                fadeout_str = f",afade=t=out:st={fade_start}:d={fadeout_lead}" if dur > fadeout_lead else ""
                if idx == 0:
                    filters.append(
                        f"[{in_idx}:a]atrim=start={start_sec},asetpts=PTS-STARTPTS,volume={music_vol/100.0},afade=t=in:d=3{fadeout_str}[{out_label}]"
                    )
                else:
                    filters.append(
                        f"[{in_idx}:a]atrim=start={start_sec},asetpts=PTS-STARTPTS,volume={music_vol/100.0}{fadeout_str}[{out_label}]"
                    )
                music_inputs.append(out_label)
            music_out = music_inputs[0]
            for i in range(1, len(music_inputs)):
                next_label = f"m_xf_{i}"
                filters.append(f"[{music_out}][{music_inputs[i]}]acrossfade=d={crossfade_sec}:c1=tri:c2=tri[{next_label}]")
                music_out = next_label
            filters.append(f"[{music_out}]atrim=duration={max(0.1, float(total_duration))}[mus]")
            ducking_filters = build_audio_ducking_filters(
                video_audio_stream=map_audio or f"anullsrc=channel_layout=stereo:sample_rate={target_a_rate}:d={total_duration}",
                music_stream="[mus]",
                music_volume=1.0, 
                sample_rate=target_a_rate,
                video_has_audio=has_audio_input,
                duration=total_duration
            )
            filters.extend(ducking_filters)
            map_audio = "[a_out]"
        if filters:
            filter_script_path = Path(self._temp_dir.name, "filter_complex.txt")
            with open(filter_script_path, "w", encoding="utf-8") as f:
                f.write(";".join(filters))
            cmd.extend(["-filter_complex_script", str(filter_script_path)])
        cmd.extend(["-map", map_video])
        if map_audio: cmd.extend(["-map", map_audio])
        quality = 4
        if hasattr(self, "quality_slider"):
            quality = self.quality_slider.value()
        self.engine = MergerEngine(
            self.ffmpeg, cmd, self._output_path, total_duration, 
            use_gpu=True, target_v_bitrate=target_v_bitrate, 
            target_a_bitrate=target_a_bitrate, target_a_rate=target_a_rate,
            quality_level=quality
        )
        self.engine.progress.connect(self._update_progress)
        self.engine.log_line.connect(self._append_log)
        self.engine.finished.connect(self._merge_finished_cleanup)
        self.engine.start()

    def _update_progress(self, percent, time_str):
        self.set_status_message(f"Merging: {percent}% ({time_str})", "color: #43b581;", force=True)
        if hasattr(self, '_graph'): 
            self._sample_perf_counters_safe()
        if hasattr(self, "_overlay_progress_bar"):
            try:
                p = max(0, min(100, int(percent)))
                self._overlay_progress_bar.setValue(p)
                self._overlay_progress_bar.setFormat(f"{p}%  ({time_str})")
            except Exception:
                pass
        self.setWindowTitle(f"Video Merger - {percent}%")

    def _append_log(self, line):
        self._append_live_log(str(line))

    def cancel_processing(self):
        if self.request_cancellation():
            self.logger.info("USER: Clicked CANCEL MERGE")
            self.set_status_message("Cancelling...", "color: #ffa500;", force=True)
            if self.engine and self.engine.isRunning():
                self.engine.cancel()
            if self._probe_worker and self._probe_worker.isRunning():
                self._probe_worker.cancel()
                self._probe_worker.wait(1200)
            QTimer.singleShot(2200, self._ensure_cancel_cleanup)

    def _ensure_cancel_cleanup(self):
        if not self.is_processing:
            return
        probe_alive = bool(self._probe_worker and self._probe_worker.isRunning())
        engine_alive = bool(self.engine and self.engine.isRunning())
        if not probe_alive and not engine_alive:
            self._merge_finished_cleanup(False, "Cancelled by user.")

    def _merge_finished_cleanup(self, success, result_msg):
        with QMutexLocker(self._state_mutex):
            self._is_processing = False
            self._is_cancelling = False
        self._pulse_timer.stop()
        self._hide_processing_overlay()
        self.setWindowTitle("Video Merger")
        
        # Robust Temp Cleanup to avoid WinError 32 (File in use)
        if self._temp_dir:
            try:
                # Explicitly signal intent to cleanup and wait slightly for handles to clear
                td = self._temp_dir
                self._temp_dir = None # Clear reference first
                td.cleanup()
            except Exception as ex:
                self.logger.debug(f"Temp dir cleanup deferred/failed: {ex}")
        
        self.btn_cancel.hide()
        self.btn_processing.hide()
        self.btn_merge.show()
        
        self.event_handler.update_button_states()
        if success:
            result = self.event_handler.show_success_dialog(result_msg)
            self.set_status_message("Merge Complete!", "color: #43b581; font-weight: bold;", 5000, force=True)
            if result == 999:
                self.close()
            elif result == QDialog.Rejected:
                self.event_handler.clear_all()
                self.add_videos()
        else:
            if "Cancelled" not in result_msg:
                 friendly = "Merge failed. Please check input files and available disk space."
                 msg = QMessageBox(self)
                 msg.setIcon(QMessageBox.Critical)
                 msg.setWindowTitle("Merge Failed")
                 msg.setText(f"{friendly}\n\nDetails:\n{result_msg}")
                 for btn in msg.findChildren(QPushButton): btn.setCursor(Qt.PointingHandCursor)
                 msg.exec_()
            self.set_status_message(f"Failed: {result_msg}", "color: #ff6b6b;", 5000, force=True)
            
    def _scan_mp3_folder(self):
        try:
            mp3_dir = os.path.join(self.base_dir, "mp3") if self.base_dir else "mp3"
            self.unified_music_widget.load_tracks(mp3_dir)
        except Exception as ex:
            self.logger.debug(f"MP3 initial scan skipped: {ex}")
            
    def _reset_music_player(self):
        try:
            if hasattr(self, "unified_music_widget") and self.unified_music_widget:
                self.unified_music_widget.clear_playlist()
                self.set_status_message("Music reset because list is empty.", "color: #7289da;", 1200, force=True)
        except Exception as ex:
            self.logger.debug(f"Music reset skipped: {ex}")
