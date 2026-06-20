import os, sys, time
from PyQt5.QtCore import *
from PyQt5.QtGui import *
from PyQt5.QtWidgets import *
from system import diagnostic_runtime
from ui.styles import UIStyles

class MainWindowCoreAMixin:
    @property
    def freeze_images(self):
        segs = getattr(self, "speed_segments", []) or []
        result = []
        for s in segs:
            if not isinstance(s, dict): continue
            try:
                if abs(float(s.get("speed", 1.0))) < 0.001:
                    result.append(s)
            except (TypeError, ValueError):
                continue
        return result

    def _update_granular_button_state(self):
        if not hasattr(self, "granular_button"):
            return
        segs = getattr(self, "speed_segments", []) or []
        has_segments = len(segs) > 0
        has_thumbnail = bool(getattr(self, "selected_intro_abs_time", None))
        is_granular_active = False
        if hasattr(self, "granular_checkbox"):
            is_granular_active = self.granular_checkbox.isChecked()
        if (has_segments or has_thumbnail) and is_granular_active:
            self.granular_button.setText("REMOVE SPEED SEGMENTS")
            self.granular_button.setStyleSheet(UIStyles.BUTTON_DANGER + " QPushButton { font-size: 10px; padding: 0px; }")
            self.granular_button.setToolTip("Click to completely remove all granular speed segments, freeze images, and thumbnails")
        else:
            self.granular_button.setText("GRANULAR SPEED")
            self.granular_button.setStyleSheet(UIStyles.BUTTON_WIZARD_BLUE + " QPushButton { font-size: 10px; padding: 0px; }")
            self.granular_button.setToolTip("Open the detailed speed editor to add variable speeds or freeze frames")

    def _handle_granular_click(self):
        # Always open the granular speed editor dialog to allow editing or adding segments.
        self.open_granular_speed_dialog()

    def open_granular_speed_dialog(self):
        try:
            if not self.input_file_path:
                 QMessageBox.warning(self, "No Video", "Please load a video first."); return
            self._opening_granular_dialog = True; self._ignore_mpv_end_until = time.time() + 2.0; current_ms = 0
            preview_reload = False
            if self.player:
                preview_reload = True
                if not self._safe_mpv_get("pause", True): self._safe_mpv_set("pause", True)
                current_ms = max(0, int((getattr(self.player, 'time-pos', 0) or 0) * 1000))
                try:
                    current_ms = max(0, int((self._safe_mpv_get("time-pos", current_ms / 1000.0) or 0) * 1000))
                except Exception:
                    pass
                try:
                    self._safe_mpv_command("stop")
                except Exception:
                    pass
            m_p = getattr(self, "_music_preview_player", None)
            if m_p:
                try: self._safe_mpv_set("pause", True, target_player=m_p); m_p.mute = True
                except: pass
            if hasattr(self, "playPauseButton"):
                self.playPauseButton.setText("PLAY"); self.playPauseButton.setIcon(self.style().standardIcon(QStyle.SP_MediaPlay))
            self.is_playing = False
            if hasattr(self, "timer") and self.timer.isActive(): self.timer.stop()
            if hasattr(self, "positionSlider"): self.positionSlider.hide()
            if hasattr(self, "portrait_mask"): self.portrait_mask.hide()
            if hasattr(self, "timeline_overlay"): self.timeline_overlay.hide()
            if hasattr(self, "video_surface"): self.video_surface.hide()
            QCoreApplication.processEvents()

            from ui.widgets.granular_speed_editor import GranularSpeedEditor
            dlg = GranularSpeedEditor(self.input_file_path, self, self.speed_segments, base_speed=self.speed_spinbox.value(), start_time_ms=current_ms, volume=self._vol_eff())
            dlg.status_update_signal.connect(self.status_update_signal.emit)
            res = dlg.exec_(); self._opening_granular_dialog = False; self._ignore_mpv_end_until = time.time() + 1.0; QCoreApplication.processEvents()
            if hasattr(self, "video_surface"): self.video_surface.show()
            if hasattr(self, "positionSlider"): self.positionSlider.show()
            if hasattr(self, "mobile_checkbox") and self.mobile_checkbox.isChecked() and hasattr(self, "portrait_mask"):
                self.portrait_mask.show()
            if hasattr(self, "timeline_overlay"): self.timeline_overlay.show()
            if preview_reload and getattr(self, "player", None) and getattr(self, "input_file_path", None):
                try:
                    if hasattr(self, "_bind_main_player_output"): self._bind_main_player_output()
                    self._safe_mpv_command("loadfile", self.input_file_path, "replace")
                    try:
                        self._safe_mpv_set("speed", float(getattr(self, "playback_rate", self.speed_spinbox.value() if hasattr(self, "speed_spinbox") else 1.0) or 1.0))
                    except Exception:
                        pass
                    self._safe_mpv_set("pause", True)

                    def _restore_preview_seek():
                        try:
                            if getattr(self, "player", None):
                                self._safe_mpv_command("seek", current_ms / 1000.0, "absolute", "exact")
                                self._safe_mpv_set("pause", True)
                        except Exception:
                            pass
                    QTimer.singleShot(250, _restore_preview_seek)
                except Exception as preview_restore_err:
                    if hasattr(self, "logger"):
                        self.logger.warning(f"Preview restore after granular editor failed: {preview_restore_err}")
            if res == QDialog.Accepted:
                self.speed_segments = []
                for s in dlg.speed_segments:
                    self.speed_segments.append({
                        'start': s['start'] + self.trim_start_ms,
                        'end': s['end'] + self.trim_start_ms,
                        'speed': s['speed']
                    })
                self.speed_segments.sort(key=lambda item: (item['start'], item['end']))
                if hasattr(self, "granular_checkbox"): self.granular_checkbox.setChecked(bool(self.speed_segments))
                if hasattr(self, "_update_quality_label"): self._update_quality_label()
            if hasattr(self, "_update_granular_button_state"): self._update_granular_button_state()
            if hasattr(self, "positionSlider"):
                visible_segments = self.speed_segments if bool(getattr(self, "granular_checkbox", None) and self.granular_checkbox.isChecked()) else []
                self.positionSlider.set_speed_segments(visible_segments); self.positionSlider.update()
            if hasattr(self, "_save_recovery_state"): self._save_recovery_state()
            if m_p:
                try: m_p.mute = False
                except: pass
            if hasattr(self, "update_player_state"): self.update_player_state()
            if hasattr(self, "positionSlider"): self.positionSlider.show()
            if hasattr(self, "mobile_checkbox") and self.mobile_checkbox.isChecked() and hasattr(self, "portrait_mask"):
                self.portrait_mask.show()
            if hasattr(self, "timeline_overlay"): self.timeline_overlay.show()
            self.activateWindow(); self.setFocus()
        except Exception as e:
            self._opening_granular_dialog = False
            for attr in ("video_surface", "positionSlider", "timeline_overlay"):
                w = getattr(self, attr, None)
                if w:
                    try: w.show()
                    except Exception: pass
            if hasattr(self, "mobile_checkbox") and self.mobile_checkbox.isChecked() and hasattr(self, "portrait_mask"):
                try: self.portrait_mask.show()
                except Exception: pass
            m_p = getattr(self, "_music_preview_player", None)
            if m_p:
                try: m_p.mute = False
                except Exception: pass
            if locals().get("preview_reload") and getattr(self, "player", None) and getattr(self, "input_file_path", None):
                try:
                    if hasattr(self, "_bind_main_player_output"): self._bind_main_player_output()
                    self._safe_mpv_command("loadfile", self.input_file_path, "replace")
                    self._safe_mpv_set("pause", True)
                except Exception:
                    pass
            if hasattr(self, "logger"):
                self.logger.error(f"Error opening granular speed dialog: {e}")
            if hasattr(self, "statusBar"): self.statusBar().showMessage(f"❌ Error: {str(e)}", 5000)

    def _clear_speed_segments(self):
        has_segments = bool(self.speed_segments)
        has_freeze = bool(self.freeze_images)
        has_thumbnail = bool(getattr(self, "selected_intro_abs_time", None))
        if not has_segments and not has_thumbnail: return
        msg = "Are you sure you want to clear all granular speeds, freeze images, and thumbnails?"
        if has_freeze or has_thumbnail:
             msg = "Are you sure you want to clear all speed segments and freeze/thumbnail images?"
        if QMessageBox.question(self, "Clear Granular Speeds", msg, QMessageBox.Yes | QMessageBox.No) == QMessageBox.Yes:
            self.speed_segments = []
            self.selected_intro_abs_time = None
            if hasattr(self, "positionSlider"):
                self.positionSlider.set_speed_segments([])
                self.positionSlider.set_thumbnail_pos_ms(-1)
                self.positionSlider.update()
            if hasattr(self, "thumb_pick_btn"):
                self.thumb_pick_btn.setText('📸 SET THUMBNAIL 📸')
            if hasattr(self, "granular_checkbox"): self.granular_checkbox.setChecked(False)
            if hasattr(self, "_update_granular_button_state"): self._update_granular_button_state()
            if hasattr(self, "_update_quality_label"): self._update_quality_label()
            if hasattr(self, "_save_recovery_state"): self._save_recovery_state()
            if hasattr(self, "statusBar"):
                self.statusBar().showMessage("Granular speeds and frozen images cleared.", 3000)

    def show_priority_message(self, message: str, duration_ms: int = 5000, is_critical: bool = False):
        try:
            if getattr(self, "_in_transition", False): return
            now = time.time()
            if not is_critical and now < float(getattr(self, "_block_status_until", 0.0)): return
            self.statusBar().showMessage(message, duration_ms)
            if is_critical:
                self._block_status_until = now + (duration_ms / 1000.0); QCoreApplication.processEvents()
        except: pass

    def on_hardware_scan_finished(self, mode: str):
        if not hasattr(self, 'status_bar'): return
        raw_mode = str(mode or "CPU")
        upper_mode = raw_mode.upper()
        if "NVIDIA" in upper_mode:
            mode = "NVIDIA"
        elif "AMD" in upper_mode:
            mode = "AMD"
        elif "INTEL" in upper_mode:
            mode = "INTEL"
        else:
            mode = "CPU"
        isolation_active = diagnostic_runtime.is_isolation_active()
        if isolation_active:
            mode = "CPU"
        self.hardware_strategy = mode; self.scan_complete = True
        try:
            cfg = self.config_manager.config; cfg["last_hardware_strategy"] = mode; self.config_manager.save_config(cfg)
        except: pass
        if hasattr(self, 'hardware_status_label'):
            if isolation_active:
                self._set_right_status_message("🧪 CPU Isolation Mode", 8000, color="#ffa500")
            else:
                icon = "🚀" if mode in ["NVIDIA", "AMD", "INTEL"] else "⚠️"
                color = "#43b581" if mode != "CPU" else "#ffa500"
                self._set_right_status_message(f"{icon} {mode} Mode", 8000, color=color)
        if isolation_active:
            self.show_status_warning("🧪 Diagnostic isolation active: software-decoded preview active (hwdec=no) with crash containment enabled.")
            try:
                self.logger.warning("MPV DIAGNOSTIC ISOLATION ACTIVE: software-decoded visible preview active (hwdec=no, containment enabled).")
            except Exception:
                pass
        if mode == "CPU":
            self.show_status_warning("⚠️ No compatible GPU detected. CPU-only mode.")
        else:
            self.show_priority_message(f"✅ Hardware Acceleration Enabled ({mode})", 5000, is_critical=True)
        if hasattr(self, "_maybe_enable_process"): self._maybe_enable_process()
        if getattr(self, "_pending_process", False):
            self._pending_process = False
            QTimer.singleShot(100, self.start_processing)

    def show_status_warning(self, message: str):
        try:
            if hasattr(self, "_set_right_status_message"):
                self._set_right_status_message(message, 10000, color="#f39c12")
        except: pass
