import os
import sys
import time
import subprocess
import threading
import logging
from PyQt5.QtCore import Qt, QTimer, QCoreApplication, QObject, pyqtSignal, QPropertyAnimation, QUrl, QEasingCurve
from PyQt5.QtGui import QIcon, QFont, QDesktopServices, QPixmap, QPainter
from PyQt5.QtWidgets import (QStyle, QApplication, QDialog, QVBoxLayout, QLabel,
                             QGridLayout, QPushButton, QMessageBox, QSizePolicy, QFrame, QHBoxLayout)

from processing.worker import ProcessThread
from processing.system_utils import kill_process_tree
from ui.styles import UIStyles
from system.utils import UIManager, MediaProber

class FfmpegMixin:
    def _log_ffmpeg_ui_exc(self, context: str, err: Exception):
        log = getattr(self, "logger", None)
        if log is not None:
            log.exception("FfmpegMixin %s: %s", context, err)
        else:
            logging.getLogger("Main_App").exception("FfmpegMixin %s", context)

    def _quit_application(self, dialog_to_close):
        if dialog_to_close: dialog_to_close.accept()
        if hasattr(self, "cleanup_and_exit"): self.cleanup_and_exit()
        else: QCoreApplication.instance().quit()

    def _safe_status(self, text: str, color: str = "white"):
        try:
            if hasattr(self, "hardware_status_label") and self.hardware_status_label is not None:
                self.hardware_status_label.setText(text)
                hex_color = color if str(color).startswith("#") else {"white": "#ecf0f1", "orange": "#f39c12", "red": "#e74c3c"}.get(str(color).lower(), "#ecf0f1")
                self.hardware_status_label.setStyleSheet(f"color: {hex_color}; font-weight: bold;")
                return
        except Exception as err:
            self._log_ffmpeg_ui_exc("_safe_status hardware_status_label", err)
        try:
            sb = getattr(self, "status_bar", None) or (self.statusBar() if hasattr(self, "statusBar") else None)
            if sb is not None:
                sb.showMessage(text, 6000)
                return
        except Exception as err2:
            self._log_ffmpeg_ui_exc("_safe_status statusBar", err2)
        try:
            self.status_update_signal.emit(text)
        except Exception as err3:
            self._log_ffmpeg_ui_exc("_safe_status status_update_signal", err3)

    def _safe_set_phase(self, name: str, ok: bool | None = None):
        try:
            self.on_phase_update(name)
        except Exception as err:
            self._log_ffmpeg_ui_exc("on_phase_update", err)

    def _safe_set_duration_text(self, text: str):
        try:
            if hasattr(self, "resolution_label") and self.resolution_label is not None:
                self.resolution_label.setText(text)
                return
        except Exception as err:
            self._log_ffmpeg_ui_exc("resolution_label.setText", err)

    def show_message(self, title: str, message: str):
        try:
            if str(title).strip().lower() in {"error", "critical"}:
                QMessageBox.critical(self, title, message)
            elif str(title).strip().lower() in {"warning", "warn"}:
                QMessageBox.warning(self, title, message)
            else:
                QMessageBox.information(self, title, message)
        except Exception as err:
            self._log_ffmpeg_ui_exc("show_message", err)

    def cancel_processing(self):
        if not self.is_processing:
            return
        if hasattr(self, "cancel_button"):
            self.cancel_button.setEnabled(False)
            self.cancel_button.setText("Stopping...")
        QApplication.processEvents()
        th = getattr(self, "process_thread", None)
        if th and th.isRunning():
            th.cancel()
            th.wait(5000)
        if th and th.isRunning():
            th.cancel()
            th.wait(4000)
        if th and th.isRunning() and getattr(th, "current_process", None):
            try:
                kill_process_tree(th.current_process.pid, getattr(self, "logger", None))
            except Exception as err:
                self._log_ffmpeg_ui_exc("kill_process_tree on cancel", err)
            th.wait(3000)
        if th and th.isRunning():
            try:
                self.logger.error("ProcessThread did not stop cleanly; terminating thread.")
            except Exception:
                pass
            th.terminate()
            th.wait(2000)
        if hasattr(self, "cancel_button"):
            self.cancel_button.setEnabled(True)
            self.cancel_button.setText("Cancel")
        self.on_process_finished(False, "Processing was canceled by the user.")
        self._save_app_state_and_config()

    def start_processing(self):
        try:
            if self.is_processing:
                self.show_message("Info", "A video is already being processed. Please wait.")
                return
            if not self.input_file_path or not os.path.exists(self.input_file_path):
                self.show_message("Error", "Please select a valid video file first.")
                return

            from processing.system_utils import check_disk_space
            out_dir = os.path.join(os.path.expanduser("~"), "Downloads")
            os.makedirs(out_dir, exist_ok=True)
            if not check_disk_space(out_dir, 2.0):
                self.show_message("Disk Space Low", "You have less than 2GB free on the output drive. Please free up space before processing.")
                return
            if self.trim_start_ms > 0 or self.trim_end_ms > 0:
                start_time_ms = self.trim_start_ms; end_time_ms = self.trim_end_ms
            else:
                start_time_ms = (self.start_minute_input.value() * 60 * 1000) + (self.start_second_input.value() * 1000) + self.start_ms_input.value()
                end_time_ms = (self.end_minute_input.value() * 60 * 1000) + (self.end_second_input.value() * 1000) + self.end_ms_input.value()
            total_duration_ms = self.original_duration_ms
            if total_duration_ms <= 0: total_duration_ms = 99999 * 1000
            start_time_ms = max(0, min(start_time_ms, total_duration_ms))
            end_time_ms = max(0, min(end_time_ms, total_duration_ms))
            MIN_DURATION_MS = 500
            if end_time_ms < start_time_ms + MIN_DURATION_MS:
                end_time_ms = min(total_duration_ms, start_time_ms + MIN_DURATION_MS)
                if end_time_ms < start_time_ms + MIN_DURATION_MS: start_time_ms = max(0, end_time_ms - MIN_DURATION_MS)
            self.trim_start_ms = start_time_ms; self.trim_end_ms = end_time_ms
            is_mobile_format = self.mobile_checkbox.isChecked()
            speed_factor = float(self.speed_spinbox.value())
            if speed_factor < 0.1 or speed_factor > 4.0:
                self.show_message("Invalid Speed", "Allowed speed range is 0.1x to 4.0x."); self.is_processing = False
                self.process_button.setEnabled(True); return
            stored_speed_segments = list(getattr(self, "speed_segments", []) or [])
            checkbox = getattr(self, "granular_checkbox", None)
            is_checked = getattr(checkbox, "isChecked", None)
            checkbox_enabled = bool(is_checked()) if callable(is_checked) else bool(stored_speed_segments)
            if stored_speed_segments and not checkbox_enabled:
                try:
                    self.logger.warning("GRANULAR: hidden checkbox was off while segments exist; exporting stored segments anyway.")
                except Exception:
                    pass
                try:
                    checkbox.setChecked(True)
                except Exception:
                    pass
            raw_segments = stored_speed_segments
            segments = []; speed_segments_for_worker = []
            for seg in raw_segments:
                try:
                    s_ms = int(seg.get("start_ms", seg.get("start", 0))); e_ms = int(seg.get("end_ms", seg.get("end", 0))); spd = float(seg.get("speed", speed_factor))
                    if e_ms <= s_ms: continue
                    segments.append({"start": s_ms, "end": e_ms, "speed": spd}); speed_segments_for_worker.append({"start_ms": s_ms, "end_ms": e_ms, "speed": spd})
                except: continue
            segments.sort(key=lambda item: (item["start"], item["end"]))
            speed_segments_for_worker.sort(key=lambda item: (item["start_ms"], item["end_ms"]))
            try:
                self.logger.info(
                    "GRANULAR_EXPORT_STATE: stored=%d worker=%d base_speed=%.3f trim=%d-%d segments=%s",
                    len(stored_speed_segments), len(speed_segments_for_worker), speed_factor,
                    start_time_ms, end_time_ms, speed_segments_for_worker,
                )
            except Exception:
                pass
            self.positionSlider.set_trim_times(self.trim_start_ms, self.trim_end_ms)
            music_path = None; music_offset_s = 0.0; music_tracks_for_worker = []
            linear_video_vol = self._get_master_eff() / 100.0
            if hasattr(self, "_video_volume_pct"):
                linear_video_vol = float(getattr(self, "_video_volume_pct", 80)) / 100.0
            for track in list(getattr(self, "_wizard_tracks", []) or []):
                try:
                    if isinstance(track, dict):
                        t_path = track.get("path")
                        t_offset = float(track.get("offset_sec", track.get("offset", track.get("file_offset_sec", 0.0))) or 0.0)
                        t_dur = float(track.get("duration_sec", track.get("duration", track.get("dur", 0.0))) or 0.0)
                    else:
                        t_path = track[0]
                        t_offset = float(track[1]) if len(track) > 1 else 0.0
                        t_dur = float(track[2]) if len(track) > 2 else 0.0
                    if not t_path or not os.path.exists(str(t_path)):
                        continue
                    if t_dur <= 0.001:
                        t_dur = max(0.001, (end_time_ms - start_time_ms) / 1000.0)
                    music_tracks_for_worker.append((str(t_path), max(0.0, t_offset), max(0.001, t_dur)))
                except Exception:
                    continue
            if getattr(self, "_wizard_tracks", None) and not music_tracks_for_worker:
                self.show_message(
                    "Music unavailable",
                    "Music is selected, but none of the selected music files are available. Please reselect the music before processing.",
                )
                return
            if music_tracks_for_worker:
                music_path = music_tracks_for_worker[0][0]; music_offset_s = music_tracks_for_worker[0][1]
            music_vol_linear = self._music_eff() / 100.0 if music_path else 0.0
            if music_path and hasattr(self, "_music_volume_pct"):
                music_vol_linear = float(getattr(self, "_music_volume_pct", 80)) / 100.0
            q_level = int(self.quality_slider.value())
            target_mb = None if q_level >= 20 else float(5 + q_level * 5)
            if self.logger:
                self.logger.info(f"QUALITY_EXPORT_STATE: slider={q_level} target_mb={target_mb if target_mb is not None else 'CQ'}")
            if hasattr(self, 'portrait_mask_overlay'): self.portrait_mask_overlay.hide()
            if hasattr(self, 'set_overlays_force_hidden'): self.set_overlays_force_hidden(True)
            self.is_processing = True; self._proc_start_ts = time.time(); self._pulse_phase = 0
            self.process_button.setEnabled(False); self.cancel_button.setVisible(True); self.cancel_button.setEnabled(True)
            self.progress_bar.setRange(0, 0); self.progress_bar.setValue(0); self._pulse_timer.start(250)
            self.process_button.setText("PROCESSING")
            try:
                reload_icon = getattr(QStyle, "SP_BrowserReload", None)
                if reload_icon is not None:
                    self.process_button.setIcon(self.style().standardIcon(reload_icon))
            except Exception as icon_err:
                self._log_ffmpeg_ui_exc("set processing icon", icon_err)
            self._safe_set_phase("Processing"); self._show_processing_overlay(); self._safe_status("Preparing... (probing/seek)...", "white")
            self.progress_update_signal.emit(0)
            cfg = dict(self.config_manager.config); cfg['last_speed'] = float(speed_factor); cfg['mobile_checked'] = bool(is_mobile_format); cfg['teammates_checked'] = bool(self.teammates_checkbox.isChecked())
            self.config_manager.save_config(cfg)
            m_start_ms = int(getattr(self, 'music_timeline_start_ms', self.trim_start_ms)); m_end_ms = int(getattr(self, 'music_timeline_end_ms', self.trim_end_ms))
            if music_path and m_end_ms <= m_start_ms:
                m_start_ms = self.trim_start_ms
                m_end_ms = self.trim_end_ms
            v_wall_start = self._calculate_wall_clock_time(start_time_ms, segments, speed_factor)
            m_wall_start = self._calculate_wall_clock_time(m_start_ms, segments, speed_factor)
            m_wall_end = self._calculate_wall_clock_time(m_end_ms, segments, speed_factor)
            m_proj_start_sec = max(0.0, (m_wall_start - v_wall_start) / 1000.0)
            m_proj_end_sec = max(0.0, (m_wall_end - v_wall_start) / 1000.0)
            music_conf = {'path': music_path, 'ducking_threshold': 0.15, 'ducking_ratio': 2.5, 'eq_enabled': True, 'main_vol': linear_video_vol, 'music_vol': music_vol_linear if music_path else 1.0, 'timeline_start_sec': m_proj_start_sec, 'timeline_end_sec': m_proj_end_sec, 'file_offset_sec': music_offset_s}
            try:
                self.logger.info(
                    "MUSIC_EXPORT_STATE: enabled=%s tracks=%d path=%s offset=%.3fs music_vol=%.2f video_vol=%.2f timeline_ms=%d-%d project_sec=%.3f-%.3f",
                    bool(music_path), len(music_tracks_for_worker), music_path, music_offset_s,
                    music_vol_linear, linear_video_vol, m_start_ms, m_end_ms, m_proj_start_sec, m_proj_end_sec,
                )
            except Exception:
                pass
            p_text = None
            if is_mobile_format and hasattr(self, 'portrait_text_input'):
                raw_text = self.portrait_text_input.text().strip()
                if raw_text: p_text = raw_text
            intro_abs_time = getattr(self, 'selected_intro_abs_time', 0.0)
            if not intro_abs_time or intro_abs_time <= 0:
                segment_duration = (end_time_ms - start_time_ms) / 1000.0
                intro_abs_time = (start_time_ms / 1000.0) + (segment_duration * 0.66)
            intro_abs_time_ms = int(intro_abs_time * 1000)
            v_norm_db = getattr(self, "volume_normalize_db", 0.0)
            self.process_thread = ProcessThread(input_path=self.input_file_path, start_time_ms=start_time_ms, end_time_ms=end_time_ms, original_resolution=self.original_resolution, is_mobile_format=is_mobile_format, speed_factor=speed_factor, base_dir=self.base_dir, progress_signal=self.progress_update_signal, status_signal=self.status_update_signal, finished_signal=self.process_finished_signal, logger=self.logger, is_boss_hp=self.boss_hp_checkbox.isChecked(), show_teammates_overlay=(is_mobile_format and self.teammates_checkbox.isChecked()), show_spectating_overlay=is_mobile_format, quality_level=q_level, bg_music_path=music_path, bg_music_volume=music_vol_linear, bg_music_offset_ms=int(music_offset_s * 1000), original_total_duration_ms=self.original_duration_ms, disable_fades=self.no_fade_checkbox.isChecked(), intro_still_sec=0.1, intro_from_midpoint=(intro_abs_time_ms <= 0), intro_abs_time_ms=intro_abs_time_ms if intro_abs_time_ms > 0 else None, portrait_text=p_text, music_config=music_conf, speed_segments=speed_segments_for_worker, hardware_strategy=getattr(self, 'hardware_strategy', 'CPU'), music_tracks=music_tracks_for_worker, target_mb_override=target_mb, volume_normalize_db=v_norm_db)
            self.process_thread.start()
        except Exception as e:
            try:
                self.logger.exception(f"Could not start processing: {e}")
            except Exception:
                pass
            self.is_processing = False
            try:
                self._pulse_timer.stop()
            except Exception:
                pass
            try:
                self._hide_processing_overlay()
            except Exception:
                pass
            self.progress_bar.setRange(0, 100)
            self.progress_bar.setValue(0)
            self.cancel_button.setVisible(False)
            if hasattr(self, "_maybe_enable_process"):
                self._maybe_enable_process()
            else:
                self.process_button.setEnabled(True)
            self.show_message("Error", f"Could not start processing:\n{e}")

    def _show_error_with_log(self, message):
        msg = QMessageBox(self); msg.setIcon(QMessageBox.Critical); msg.setWindowTitle("Processing Error"); msg.setText("An error occurred during processing."); msg.setInformativeText(message)
        UIManager.style_and_size_msg_box(msg, message)
        details_btn = msg.addButton("Show Technical Logs", QMessageBox.ActionRole); ok_btn = msg.addButton(QMessageBox.Ok)
        for btn in msg.buttons(): btn.setCursor(Qt.PointingHandCursor)
        msg.exec_()
        if msg.clickedButton() == details_btn:
            log_path = os.path.join(self.base_dir, "logs", "main_app.log"); log_content = "Log file not found."
            if os.path.exists(log_path):
                try:
                    with open(log_path, "r", encoding="utf-8", errors="ignore") as f:
                        lines = f.readlines(); log_content = "".join(lines[-100:])
                except Exception as err:
                    self._log_ffmpeg_ui_exc("read main_app.log tail", err)
            d = QDialog(self); d.setWindowTitle("Technical Logs (Last 100 Lines)"); d.resize(900, 600); l = QVBoxLayout(d)

            from PyQt5.QtWidgets import QTextEdit
            t = QTextEdit(); t.setFont(QFont("Consolas", 10)); t.setReadOnly(True); t.setPlainText(log_content); l.addWidget(t); d.exec_()

    def share_via_whatsapp(self, file_path: str = None):
        try:
            QDesktopServices.openUrl(QUrl("https://web.whatsapp.com"))
            if file_path:
                self.open_output_in_explorer(file_path)
        except Exception as err:
            self._log_ffmpeg_ui_exc("share_via_whatsapp", err)

    def open_output_in_explorer(self, file_path: str):
        full_path = os.path.abspath(file_path)
        if not os.path.exists(full_path): return
        try:
            if os.name == "nt":
                subprocess.run(['explorer', '/select,', os.path.normpath(full_path)], check=False)
            elif sys.platform == "darwin":
                subprocess.Popen(["open", "-R", full_path])
            else:
                subprocess.Popen(["xdg-open", os.path.dirname(full_path)])
        except Exception as err:
            self._log_ffmpeg_ui_exc("open_output_in_explorer", err)

    def _dialog_button_style(self, color: str, pressed: str, *, font_size: int = 12) -> str:
        return f"QPushButton {{ background: qlineargradient(x1:0, y1:0, x2:0, y2:1, stop:0 {color}, stop:1 {pressed}); color: white; font-weight: bold; font-family: Arial; font-size: {font_size}px; border-radius: 8px; border: 1px solid rgba(0,0,0,0.45); padding: 0px; text-align: center; min-width: 180px; max-width: 180px; min-height: 45px; max-height: 45px; }} QPushButton:hover {{ border: 1px solid #7DD3FC; }} QPushButton:pressed {{ background: qlineargradient(x1:0, y1:0, x2:0, y2:1, stop:0 {pressed}, stop:1 {color}); }}"

    def on_process_finished(self, success, message):
        self.is_processing = False; self._proc_start_ts = None; self._phase_is_processing = False
        if hasattr(self, "_pulse_timer"): self._pulse_timer.stop()
        self._hide_processing_overlay(); self.cancel_button.setVisible(False)
        self.process_button.setText("PROCESS"); self.process_button.setIcon(self.style().standardIcon(QStyle.SP_DialogApplyButton))
        self.progress_bar.setRange(0, 100); self.progress_bar.setValue(0); self.selected_intro_abs_time = None
        if hasattr(self, "_maybe_enable_process"): self._maybe_enable_process()
        else: self.process_button.setEnabled(True)
        if success: self._safe_set_phase("Done", ok=True)
        else:
            if "canceled by user" in message.lower(): self._safe_set_phase("Canceled", ok=False)
            else: self._safe_set_phase("Error", ok=False)
        self.status_update_signal.emit("Ready.")
        if success:
            if hasattr(self, "set_overlays_force_hidden"):
                self.set_overlays_force_hidden(True)
            output_path = message; self._block_portrait_overlay = True
            if hasattr(self, "_cleanup_staged_input_workspace"):
                self._cleanup_staged_input_workspace(clear_input=True)
            try:
                if hasattr(self, "timer") and self.timer.isActive(): self.timer.stop()
                if hasattr(self, "_log_flush_timer") and getattr(self, "_log_flush_timer", None) and self._log_flush_timer.isActive(): self._log_flush_timer.stop()
                if hasattr(self, "_stats_timer") and getattr(self, "_stats_timer", None) and self._stats_timer.isActive(): self._stats_timer.stop()
                if hasattr(self, "_color_pulse_timer") and getattr(self, "_color_pulse_timer", None) and self._color_pulse_timer.isActive(): self._color_pulse_timer.stop()
            except Exception:
                pass

            class FinishedDialog(QDialog):
                def __init__(self, parent=None):
                    super().__init__(parent)
                    self.setWindowFlags(self.windowFlags() | Qt.FramelessWindowHint)
                    self.setAttribute(Qt.WA_TranslucentBackground, True)
                    self.setWindowOpacity(0.0)
                    self._closing = False
                    self._anim = None
                    self._pulse = None

                def showEvent(self, e):
                    super().showEvent(e)
                    QTimer.singleShot(0, self.fade_in)

                def fade_in(self):
                    self._anim = QPropertyAnimation(self, b"windowOpacity", self)
                    self._anim.setDuration(1500)
                    self._anim.setStartValue(0.0)
                    self._anim.setEndValue(1.0)
                    self._anim.setEasingCurve(QEasingCurve.InOutQuad)
                    self._anim.finished.connect(self.start_pulse)
                    self._anim.start()

                def start_pulse(self):
                    if self._closing: return
                    self._pulse = QPropertyAnimation(self, b"windowOpacity", self)
                    self._pulse.setDuration(4000)
                    self._pulse.setStartValue(1.0)
                    self._pulse.setKeyValueAt(0.5, 0.3)
                    self._pulse.setEndValue(1.0)
                    self._pulse.setEasingCurve(QEasingCurve.InOutSine)
                    self._pulse.setLoopCount(-1)
                    self._pulse.start()

                def fade_done(self, result):
                    if self._closing: return
                    self._closing = True
                    if self._pulse: self._pulse.stop()
                    self._anim = QPropertyAnimation(self, b"windowOpacity", self)
                    self._anim.setDuration(1000)
                    self._anim.setStartValue(float(self.windowOpacity()))
                    self._anim.setEndValue(0.0)
                    self._anim.setEasingCurve(QEasingCurve.InOutQuad)
                    self._anim.finished.connect(lambda: QDialog.done(self, result))
                    self._anim.start()

                def fade_accept(self): self.fade_done(QDialog.Accepted)

                def fade_reject(self): self.fade_done(QDialog.Rejected)

                def accept(self): self.fade_accept()

                def reject(self): self.fade_accept()

                def closeEvent(self, e):
                    if self._closing:
                        super().closeEvent(e); return
                    e.ignore(); self.fade_accept()
            dialog = FinishedDialog(self); dialog.setWindowTitle("Done! Video Processed Successfully!"); dialog.setModal(True); dialog.setFixedSize(760, 420)
            outer = QVBoxLayout(dialog); outer.setContentsMargins(0, 0, 0, 0); outer.setSpacing(0)
            frame = QFrame(dialog); frame.setObjectName("finishedFrame")
            frame.setStyleSheet("QFrame#finishedFrame { background-color: #0b141d; border: 2px solid #7DD3FC; border-radius: 14px; }")
            outer.addWidget(frame)
            layout = QVBoxLayout(frame); layout.setContentsMargins(28, 18, 28, 30); layout.setSpacing(18)
            top_row = QHBoxLayout(); top_row.setContentsMargins(0, 0, 0, 0); top_row.addStretch(1)
            close_btn = QPushButton("X", frame)
            close_btn.setFixedSize(60, 52)
            close_btn.setStyleSheet("QPushButton { background-color: transparent; color: #ff4d4d; font-size: 42px; font-weight: bold; border: none; } QPushButton:hover { color: #ff0000; }")
            close_btn.setCursor(Qt.PointingHandCursor)
            close_btn.clicked.connect(dialog.fade_accept)
            top_row.addWidget(close_btn)
            layout.addLayout(top_row)
            label = QLabel(f"File successfully saved to:\n{output_path}"); label.setStyleSheet("font-size: 16px; font-weight: bold; color: #7DD3FC;")
            label.setAlignment(Qt.AlignCenter); layout.addWidget(label)
            grid = QGridLayout(); grid.setHorizontalSpacing(42); grid.setVerticalSpacing(28); grid.setContentsMargins(80, 18, 80, 8)
            whatsapp_button = QPushButton("✆  WHATSAPP SHARE  ✆"); whatsapp_button.setStyleSheet(self._dialog_button_style("#3CA557", "#2B7D40"))
            whatsapp_button.clicked.connect(lambda: self.share_via_whatsapp(output_path))
            whatsapp_button.clicked.connect(lambda: dialog.fade_done(999))
            open_folder_button = QPushButton("OPEN FOLDER"); open_folder_button.setStyleSheet(self._dialog_button_style("#2e82a0", "#1e648c"))
            open_folder_button.clicked.connect(lambda: self.open_output_in_explorer(output_path))
            open_folder_button.clicked.connect(lambda: dialog.fade_done(999))
            new_file_button = QPushButton("📂  UPLOAD NEW  📂"); new_file_button.setStyleSheet(self._dialog_button_style("#2e82a0", "#1e648c"))
            new_file_button.clicked.connect(dialog.fade_reject)
            exit_button = QPushButton("EXIT APP!"); exit_button.setStyleSheet(self._dialog_button_style("#c0392b", "#a93226"))
            exit_button.clicked.connect(lambda: dialog.fade_done(999))
            for b in [whatsapp_button, open_folder_button, new_file_button, exit_button]: b.setFixedSize(180, 45); b.setCursor(Qt.PointingHandCursor)
            grid.addWidget(whatsapp_button, 0, 0, alignment=Qt.AlignCenter); grid.addWidget(open_folder_button, 0, 1, alignment=Qt.AlignCenter); grid.addWidget(new_file_button, 1, 0, alignment=Qt.AlignCenter); grid.addWidget(exit_button, 1, 1, alignment=Qt.AlignCenter); layout.addLayout(grid)
            result = dialog.exec_(); self._block_portrait_overlay = False
            if hasattr(self, "_hide_processing_overlay"):
                self._hide_processing_overlay()
            if hasattr(self, "set_overlays_force_hidden"):
                self.set_overlays_force_hidden(False)
            if hasattr(self, "timeline_overlay") and self.timeline_overlay:
                self.timeline_overlay.show()
            if hasattr(self, "_set_video_controls_enabled"):
                self._set_video_controls_enabled(bool(getattr(self, "input_file_path", None)))
            if self.mobile_checkbox.isChecked() and hasattr(self, "_update_portrait_mask_overlay_state"):
                self._update_portrait_mask_overlay_state()
            if result == 999:
                self._quit_application(None)
            elif result == QDialog.Rejected: self.handle_new_file()
        else:
            if "canceled by user" not in message.lower(): self._show_error_with_log(message)
            if hasattr(self, "_cleanup_staged_input_workspace"):
                self._cleanup_staged_input_workspace(clear_input=True)

    def get_video_info(self):
        if not self.input_file_path or not os.path.exists(self.input_file_path): return
        self._safe_status("Analyzing video...", "orange")

        def _bg_worker(p):
            try:
                d, r = MediaProber.probe_metadata(self.bin_dir, p)
                return True, d, r
            except Exception as e: return False, 0.0, str(e)

        def _on_worker_finished(result):
            success, duration_s, res_or_err = result
            if not success:
                self._safe_status("Video analysis failed.", "red")
                self._safe_set_duration_text("Duration: unavailable")
                self.process_button.setEnabled(False)
                self.show_message("Error", f"Could not analyze this video:\n{res_or_err}")
                return
            duration_ms = int(duration_s * 1000)
            try:
                self.original_duration_ms = duration_ms; self.original_resolution = res_or_err
                self.positionSlider.setRange(0, duration_ms); self.positionSlider.set_duration_ms(duration_ms)
                self._update_trim_inputs(); self._safe_set_duration_text(f"Duration: {duration_s:.2f} s | Res: {self.original_resolution}")
                self.trim_start_ms = 0; self.trim_end_ms = duration_ms; self._update_trim_widgets_from_trim_times(); self.positionSlider.set_trim_times(self.trim_start_ms, self.trim_end_ms); self._safe_status("Video loaded.", "white")
                if hasattr(self, "_maybe_enable_process"):
                    self._maybe_enable_process()
                QTimer.singleShot(500, self.analyze_volume)
            except Exception as e:
                try:
                    self.logger.exception(f"Failed to update UI after probe: {e}")
                except Exception:
                    pass
                self.show_message("Error", f"Video loaded, but the UI could not be updated:\n{e}")
            finally:
                if hasattr(self, "timer") and not self.timer.isActive(): self.timer.start(100)

        class _ProbeBridge(QObject):
            done = pyqtSignal(object)
        self._probe_bridge = _ProbeBridge(); self._probe_bridge.done.connect(_on_worker_finished)

        def _thread_target():
            result = _bg_worker(str(self.input_file_path)); self._probe_bridge.done.emit(result)
        threading.Thread(target=_thread_target, daemon=True).start()

    def analyze_volume(self):
        if not self.input_file_path or not os.path.exists(self.input_file_path): return
        self._safe_status("Analyzing audio levels...", "orange")

        def _bg_worker(p):
            try:
                mean, max_v = MediaProber.probe_volume(self.bin_dir, p)
                return True, mean, max_v
            except Exception: return False, 0.0, 0.0

        def _on_worker_finished(result):
            success, mean, max_v = result
            if not success:
                self._safe_status("Audio analysis failed.", "red")
                return
            
            self._safe_status("Video and audio analyzed.", "white")
            if mean < -25.0 or mean > -10.0:
                recommended = -18.0 - mean
                if abs(recommended) > 1.5:
                    msg = f"Audio level detected: {mean:.1f} dB (Peak: {max_v:.1f} dB)\n\n" \
                          f"This seems {'quiet' if recommended > 0 else 'loud'}. " \
                          f"Would you like to normalize it by {recommended:+.1f} dB for the final export?"
                    reply = QMessageBox.question(self, "Voice Stabilization", msg, QMessageBox.Yes | QMessageBox.No, QMessageBox.Yes)
                    if reply == QMessageBox.Yes:
                        self.volume_normalize_db = recommended
                        self._safe_status(f"Normalization active: {recommended:+.1f} dB", "green")
                    else:
                        self.volume_normalize_db = 0.0
            else:
                self.volume_normalize_db = 0.0

        class _VolumeBridge(QObject):
            done = pyqtSignal(object)
        self._volume_bridge = _VolumeBridge(); self._volume_bridge.done.connect(_on_worker_finished)

        def _thread_target():
            result = _bg_worker(str(self.input_file_path)); self._volume_bridge.done.emit(result)
        threading.Thread(target=_thread_target, daemon=True).start()

    def on_progress(self, value: int):
        if self.progress_bar.maximum() == 0: self.progress_bar.setRange(0, 100)
        self.progress_bar.setValue(int(max(0, min(100, value))))

    def _calculate_wall_clock_time(self, video_ms, segments, base_speed):
        accumulated_wall_time = 0.0; current_v = 0.0
        for seg in sorted(segments or [], key=lambda item: (item.get('start', 0), item.get('end', 0))):
            if video_ms <= seg['start']: break
            if seg['start'] > current_v:
                if base_speed < 0.001: accumulated_wall_time += (seg['start'] - current_v)
                else: accumulated_wall_time += (seg['start'] - current_v) / base_speed
            partial_dur = min(video_ms, seg['end']) - seg['start']
            if seg['speed'] < 0.001: accumulated_wall_time += partial_dur
            else: accumulated_wall_time += partial_dur / seg['speed']
            current_v = seg['end']
        if video_ms > current_v:
            if base_speed < 0.001: accumulated_wall_time += (video_ms - current_v)
            else: accumulated_wall_time += (video_ms - current_v) / base_speed
        return accumulated_wall_time
