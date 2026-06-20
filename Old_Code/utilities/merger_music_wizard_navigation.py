import os
import time
from PyQt5.QtWidgets import QMessageBox, QStyle, QWidget, QPushButton
from PyQt5.QtCore import Qt, QTimer

class MergerMusicWizardNavigationMixin:
    def _on_nav_cancel_clicked(self):
        self.logger.info("WIZARD: User cancelled background music wizard")
        self.stop_previews()
        self.reject()

    def _on_page_changed(self, index):
        if not hasattr(self, 'btn_nav_next'): return
        self._apply_step_geometry(index)
        if hasattr(self, '_play_timer') and self._play_timer:
            try:
                self._play_timer.stop()
            except Exception:
                pass
        if index in (1, 2):
            self.btn_play_video.setVisible(True)
            self.btn_play_video.setText("  PLAY")
            self.btn_play_video.setIcon(self.style().standardIcon(QStyle.SP_MediaPlay))
        else:
            self.btn_play_video.setVisible(False)
        if index == 2:
            self.btn_nav_next.setText("✓  DONE")
            self._bind_video_output()
            self._prepare_timeline_data()
            start_time = 0.0
            self.timeline.set_current_time(start_time)
            if hasattr(self, "video_container") and self.player:
                try:
                    wid = int(self.video_container.winId())
                    self._safe_mpv_set(self.player, 'wid', wid)
                except Exception: pass
            self._sync_all_players_to_time(start_time, force_playing=False)
            self._last_clock_ts = time.time()
            self._last_seek_ts = 0.0
            self._step3_end_finalize_pending = False
            self.btn_play_video.setText("  PLAY")
            self.btn_play_video.setIcon(self.style().standardIcon(QStyle.SP_MediaPlay))
            if hasattr(self, '_play_timer') and self._play_timer:
                self._play_timer.stop()
        elif index == 1:
            self.btn_nav_next.setText("NEXT")
            self.update_coverage_ui()
        elif index == 0:
            self.btn_nav_next.setText("NEXT")
            self.update_coverage_ui()
            if getattr(self, "_auto_reset_step1", False):
                self._auto_reset_step1 = False
                self.track_list.clearSelection()
                self.search_input.clear()
        self._sync_caret()

    def _resize_from_center(self, w, h):
        try:
            if abs(self.width() - w) < 5 and abs(self.height() - h) < 5:
                return
            old_center = self.geometry().center()
            new_geom = self.geometry()
            new_geom.setWidth(w)
            new_geom.setHeight(h)
            new_geom.moveCenter(old_center)
            self.setGeometry(new_geom)
        except:
            self.resize(w, h)

    def _on_nav_next_clicked(self):
        idx = self.stack.currentIndex()
        self.logger.info(f"WIZARD: User clicked NEXT on step {idx + 1}")
        if idx == 0:
            self.go_to_offset_step()
        elif idx == 1:
            self.confirm_current_track()
        elif idx == 2:
            self.logger.info("WIZARD: User clicked DONE on Timeline. Finishing.")
            try:
                self.final_timeline_time = float(getattr(self.timeline, "current_time", 0.0))
            except Exception:
                self.final_timeline_time = 0.0
            self._save_step_geometry() 
            self.stop_previews()
            self.accept()

    def _on_nav_back_clicked(self):
        idx = self.stack.currentIndex()
        if idx > 0:
            if idx == 1:
                self._editing_track_index = -1
                self.btn_back.hide()
            self.stack.setCurrentIndex(idx - 1)
            self.stop_previews()

    def go_to_offset_step(self, edit_index: int | None = None):
        if not isinstance(edit_index, int):
            edit_index = None
        selected_item = self.track_list.currentItem()
        selected_path = selected_item.data(Qt.UserRole) if selected_item else None
        initial_offset_ms = 0
        if edit_index is not None:
            if edit_index < 0 or edit_index >= len(self.selected_tracks):
                return
            path, offset_sec, _dur = self.selected_tracks[edit_index]
            self._editing_track_index = int(edit_index)
            self.current_track_path = path
            initial_offset_ms = max(0, int(float(offset_sec or 0.0) * 1000.0))
            self.logger.info(
                "WIZARD: Editing selected timeline track %s at %.2fs",
                os.path.basename(path),
                float(offset_sec or 0.0),
            )
        else:
            if not selected_path:
                QMessageBox.warning(self, "No Selection", "Please click on a song first!")
                return
            self._editing_track_index = -1
            self.current_track_path = selected_path
            self.logger.info(f"WIZARD: User selected song: {os.path.basename(self.current_track_path)}")
        try:
            self.stop_previews()
        except Exception:
            pass
        self._dragging = False
        self._wave_dragging = False
        self._last_good_mpv_ms = initial_offset_ms
        self._pending_offset_ms = initial_offset_ms
        try:
            self.offset_slider.blockSignals(True)
            self.offset_slider.setValue(initial_offset_ms)
            self.offset_slider.blockSignals(False)
        except Exception:
            pass
        self._sync_caret()
        self.stack.setCurrentIndex(1)
        self._safe_mpv_loadfile(self.player, self.current_track_path, start_sec=initial_offset_ms / 1000.0)
        self._safe_mpv_set(self.player, "pause", True)
        self.start_waveform_generation()
        self.btn_back.show()

    def confirm_current_track(self):
        """Records the current track's offset selection and checks coverage."""
        if self._player: self._player.stop()
        if hasattr(self, '_play_timer') and self._play_timer:
            try:
                self._play_timer.stop()
            except Exception:
                pass
        offset = self.offset_slider.value() / 1000.0
        actual_dur = max(0.0, self.current_track_dur - offset)
        self.logger.info(
            f"WIZARD: User confirmed track '{os.path.basename(self.current_track_path)}' starting at {offset:.1f}s (Covers {actual_dur:.1f}s)"
        )
        if hasattr(self, "_editing_track_index") and self._editing_track_index >= 0 and self._editing_track_index < len(self.selected_tracks):
            self.selected_tracks[self._editing_track_index] = (self.current_track_path, offset, actual_dur)
            self.logger.info("WIZARD: Updated timeline track #%d", self._editing_track_index + 1)
            self._editing_track_index = -1
        else:
            self.selected_tracks.append((self.current_track_path, offset, actual_dur))
        self.update_coverage_ui()
        if hasattr(self, "_refresh_selected_tracks_ui"):
            self._refresh_selected_tracks_ui()
        covered = sum(t[2] for t in self.selected_tracks)
        if covered < self.total_video_sec - 0.5:
            missing = self.total_video_sec - covered
            self.logger.info(f"WIZARD: Incomplete coverage ({covered:.1f}s / {self.total_video_sec:.1f}s). Prompting user.")
            msg = QMessageBox(self)
            msg.setIcon(QMessageBox.Question)
            msg.setWindowTitle("Almost There!")
            msg.setText(f"You've added {covered:.1f}s of music.\nYou still need {missing:.1f}s to cover the full video.\n\nWould you like to add more music or continue to the timeline as is?")
            btn_add = msg.addButton("Add More Music", QMessageBox.ActionRole)
            btn_cont = msg.addButton("Continue to Timeline", QMessageBox.AcceptRole)
            msg.setDefaultButton(btn_add)
            for b in msg.findChildren(QPushButton): b.setCursor(Qt.PointingHandCursor)
            msg.exec_()
            if msg.clickedButton() == btn_add:
                self._auto_reset_step1 = True
                self.stack.setCurrentIndex(0)
                self.btn_back.hide()
            else:
                self.logger.info("WIZARD: User chose to continue with partial coverage.")
                self.stack.setCurrentIndex(2)
        else:
            self.logger.info("WIZARD: Coverage complete. Moving to Step 3 Timeline.")
            self.stack.setCurrentIndex(2)
