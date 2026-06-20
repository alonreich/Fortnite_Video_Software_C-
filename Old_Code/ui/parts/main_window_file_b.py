from PyQt5.QtCore import *
from PyQt5.QtGui import *
from PyQt5.QtWidgets import *

class MainWindowFileBMixin:
    def reset_app_state(self):
        self.input_file_path = None
        self.source_file_path = None
        self._loaded_display_path = None
        self.original_resolution = None
        if hasattr(self, 'set_resolution_text'):
            self.set_resolution_text("")
        elif hasattr(self, 'resolution_label'):
            self.resolution_label.setText("")
        self.original_duration_ms = 0
        self.trim_start_ms = 0
        self.trim_end_ms = 0
        self.speed_segments = []
        if hasattr(self, 'granular_checkbox'):
            self.granular_checkbox.blockSignals(True)
            self.granular_checkbox.setChecked(False)
            self.granular_checkbox.blockSignals(False)
        self.process_button.setEnabled(False)
        self._set_video_controls_enabled(False)
        self.progress_update_signal.emit(0)
        self.on_phase_update("Please upload a new video file.")
        try:
            self.positionSlider.setRange(0, 0)
            self.positionSlider.setValue(0)
            self.positionSlider.set_duration_ms(0)
            self.positionSlider.set_trim_times(0, 0)
            self.positionSlider.reset_music_times()
        except AttributeError:
            pass
        try:
            self._reset_music_player()
        except Exception:
            pass
        self.drop_label.setText("Drag & Drop\r\nVideo File Here:")
        if hasattr(self, 'portrait_mask_overlay') and self.portrait_mask_overlay:
            self.portrait_mask_overlay.hide()
        self._update_portrait_mask_overlay_state()

    def handle_new_file(self):
        self.reset_app_state()
        self.select_file()

    def _cleanup_staged_input_workspace(self, clear_input=True):
        try:
            from system.temp_video_workspace import cleanup_workspace
            if getattr(self, "player", None):
                try: self._safe_mpv_set("pause", True)
                except Exception: pass
                try: self._safe_mpv_command("stop")
                except Exception:
                    try: self.player.stop()
                    except Exception: pass
            cleanup_workspace(getattr(self, "logger", None))
            if clear_input:
                self.input_file_path = None
                self.source_file_path = None
                self._loaded_display_path = None
                if hasattr(self, "_set_video_controls_enabled"): self._set_video_controls_enabled(False)
        except Exception as e:
            try: self.logger.warning("TEMP_WORKSPACE: staged cleanup failed: %s", e)
            except Exception: pass
