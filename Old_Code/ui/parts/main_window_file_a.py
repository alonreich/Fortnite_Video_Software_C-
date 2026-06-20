import os
from PyQt5.QtCore import *
from PyQt5.QtGui import *
from PyQt5.QtWidgets import *

class MainWindowFileAMixin:
    def select_file(self):
        self._set_upload_hint_active(False)
        had_existing_media = bool(getattr(self, "input_file_path", None))
        was_playing_before_dialog = False
        try:
            if getattr(self, "player", None):
                was_playing_before_dialog = not bool(self._safe_mpv_get("pause", True))
                self._safe_mpv_set("pause", True)
            if getattr(self, "timer", None) and self.timer.isActive():
                self.timer.stop()
        except Exception as e:
            try:
                self.logger.error("FILE: failed to pause before dialog: %s", e)
            except Exception:
                pass

        from ui.widgets.custom_file_dialog import CustomFileDialog
        if hasattr(self, "set_overlays_force_hidden"):
            self.set_overlays_force_hidden(True)
        dialog = CustomFileDialog(
            None, 
            "Select Video File(s)",
            self.last_dir,
            "Video Files (*.mp4 *.mkv *.mov *.avi *.webm *.m4v *.flv *.wmv *.3gp *.mpeg *.mpg *.asf);;All Files (*.*)",
            config=self.config_manager,
        )
        dialog.setWindowModality(Qt.ApplicationModal)
        file_paths = []
        if dialog.exec_():
            file_paths = dialog.selectedFiles()
        if hasattr(self, "set_overlays_force_hidden"):
            self.set_overlays_force_hidden(False)
        if hasattr(self, "_update_portrait_mask_overlay_state"):
            self._update_portrait_mask_overlay_state()
        self.refresh_ui_styles()
        if file_paths:
            file_to_load = file_paths[0]
            if len(file_paths) > 1:
                self.logger.warning(f"User selected {len(file_paths)} files. Loading only the first one.")
                QMessageBox.information(self, "Multiple Files Selected", f"You selected {len(file_paths)} files. Only the first file will be loaded.")
            self.logger.info("FILE: selected via dialog: %s", file_to_load)
            self._set_upload_hint_active(False)
            self.handle_file_selection(file_to_load)
        else:
            self.logger.info("FILE: dialog canceled")
            if had_existing_media:
                try:
                    if getattr(self, "player", None):
                        self._safe_mpv_set("pause", not was_playing_before_dialog)
                    if was_playing_before_dialog:
                        self.is_playing = True
                        self.wants_to_play = True
                        if hasattr(self, "playPauseButton"):
                            self.playPauseButton.setText("PAUSE")
                            self.playPauseButton.setIcon(self.style().standardIcon(QStyle.SP_MediaPause))
                        if hasattr(self, "timer") and not self.timer.isActive():
                            self.timer.start(40)
                    else:
                        self.is_playing = False
                        self.wants_to_play = False
                        if hasattr(self, "playPauseButton"):
                            self.playPauseButton.setText("PLAY")
                            self.playPauseButton.setIcon(self.style().standardIcon(QStyle.SP_MediaPlay))
                except Exception as restore_err:
                    self.logger.debug("FILE: failed restoring playback state: %s", restore_err)
            if not getattr(self, 'input_file_path', None):
                self._set_upload_hint_active(True)

    def handle_file_selection(self, file_path):
        is_restoring = getattr(self, "_restoring_recovery_state", False)
        try:
            if self.player:
                try:
                    self._safe_mpv_set("pause", True)
                except Exception:
                    pass
                try:
                    self._safe_mpv_command("stop")
                except Exception:
                    try:
                        self.player.stop()
                    except Exception:
                        pass
            timer = getattr(self, "timer", None)
            if timer and timer.isActive():
                timer.stop()
        except Exception as stop_err:
            self.logger.error("Error stopping existing player: %s", stop_err)
        if not is_restoring:
            try:
                self.reset_app_state()
            except Exception as reset_err:
                self.logger.error("Error during UI reset: %s", reset_err)
        try:
            from system.temp_video_workspace import cleanup_workspace, is_workspace_path, stage_video_file
            real_path = os.path.abspath(str(file_path))
            if not is_workspace_path(real_path) and not is_restoring:
                cleanup_workspace(self.logger)
            staged_path = stage_video_file(real_path, self.logger)
            self.source_file_path = real_path if not is_workspace_path(real_path) else getattr(self, "source_file_path", real_path)
            self.input_file_path = staged_path
            self._loaded_display_path = self.source_file_path
        except Exception as stage_err:
            self.logger.error("FILE: failed to stage selected video: %s", stage_err)
            QMessageBox.critical(self, "File Copy Failed", f"The selected file could not be copied to the working folder:\n{stage_err}")
            self.input_file_path = None
            self.drop_label.setText('Drag & Drop\r\na Video File Here:')
            self._set_upload_hint_active(True)
            self._set_video_controls_enabled(False)
            return
        self.logger.info("FILE: loading staged copy for playback: %s", self.input_file_path)
        self._set_upload_hint_active(False)
        if hasattr(self, "positionSlider"):
            self.positionSlider.set_thumbnail_pos_ms(-1)
        self.drop_label.setWordWrap(True)
        self.drop_label.setText(os.path.basename(getattr(self, "_loaded_display_path", self.input_file_path)))
        dir_path = os.path.dirname(file_path)
        if os.path.isdir(dir_path):
            self.last_dir = dir_path
        p = os.path.abspath(str(self.input_file_path))
        if not os.path.isfile(p):
            self.logger.error("Selected file not found: %s", p)
            if not is_restoring:
                QMessageBox.critical(self, "File Not Found", f"The selected file no longer exists:\n{p}")
            self.input_file_path = None
            self.drop_label.setText('Drag & Drop\r\na Video File Here:')
            self._set_upload_hint_active(True)
            self._set_video_controls_enabled(False)
            return
        if self.player:
            try:
                self._bind_main_player_output()
                self._safe_mpv_command("loadfile", p, "replace")
                try:
                    current_rate = float(self.speed_spinbox.value()) if hasattr(self, "speed_spinbox") else float(getattr(self, "playback_rate", 1.1) or 1.1)
                    self.playback_rate = current_rate
                    self.player.speed = current_rate
                except Exception as rate_err:
                    self.logger.debug(f"FILE: speed apply skipped: {rate_err}")
                self._safe_mpv_set("pause", False)
                self.is_playing = True
                self.wants_to_play = True
                if hasattr(self, "playPauseButton"):
                    self.playPauseButton.setText("PAUSE")
                    self.playPauseButton.setIcon(self.style().standardIcon(QStyle.SP_MediaPause))
                if hasattr(self, "timer") and not self.timer.isActive():
                    self.timer.start(50)

                if hasattr(self, "apply_master_volume"):
                    self._suspend_volume_sync = False
                    self.apply_master_volume()
                    QTimer.singleShot(400, self.apply_master_volume)
                if hasattr(self, "_on_mobile_toggled"):
                    QTimer.singleShot(150, lambda: self._on_mobile_toggled(self.mobile_checkbox.isChecked()))
            except Exception as e:
                self.logger.error("Failed to play media with MPV: %s", e)
                if not is_restoring:
                    QMessageBox.critical(self, "Preview Failed", f"The video could not be opened in the preview player:\n{e}")
                self.input_file_path = None
                self.drop_label.setText('Drag & Drop\r\na Video File Here:')
                self._set_upload_hint_active(True)
                self._set_video_controls_enabled(False)
                return
        else:
            self.logger.warning("MPV not available. Skipping playback. (CPU Mode)")
            try:
                self.statusBar().showMessage("Preview player unavailable. Export settings remain available; preview-only tools are disabled.", 5000)
            except Exception:
                pass
        self.get_video_info()
        self._update_portrait_mask_overlay_state()
        if hasattr(self, "set_overlays_force_hidden"):
            self.set_overlays_force_hidden(False)
        if hasattr(self, "_update_overlay_positions"):
            self._update_overlay_positions()
            QTimer.singleShot(100, self._update_overlay_positions)
            QTimer.singleShot(500, self._update_overlay_positions)
        self._set_video_controls_enabled(True)
        if hasattr(self, "_set_preview_controls_available"):
            self._set_preview_controls_available(bool(self.player))
        if hasattr(self, "_save_recovery_state") and not is_restoring:
            self._save_recovery_state()
