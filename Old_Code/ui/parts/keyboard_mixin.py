from PyQt5.QtCore import Qt, QEvent
from PyQt5.QtWidgets import (
    QWidget,
    QApplication,
    QLineEdit,
    QTextEdit,
    QPlainTextEdit,
    QAbstractSpinBox,
    QComboBox,
)

class KeyboardMixin:
    def _is_editing_widget_focused(self) -> bool:
        fw = QApplication.focusWidget()
        if fw is None: return False
        if isinstance(fw, (QLineEdit, QTextEdit, QPlainTextEdit, QAbstractSpinBox)):
            return True
        if isinstance(fw, QComboBox) and fw.isEditable():
            return True
        return False

    def handle_global_key_press(self, event):
        if QApplication.activeModalWidget() is not None:
            return False
        if self._is_editing_widget_focused():
            return False
        key = event.key()
        mods = event.modifiers()
        if key == Qt.Key_F12:
            if hasattr(self, 'launch_crop_tool'):
                self.launch_crop_tool()
                return True
        if key == Qt.Key_Space:
            if hasattr(self, "toggle_play_pause"):
                self.toggle_play_pause()
                return True
        if key == Qt.Key_BracketLeft:
            if hasattr(self, "set_start_time"):
                self.set_start_time()
                return True
        if key == Qt.Key_BracketRight:
            if hasattr(self, "set_end_time"):
                self.set_end_time()
                return True
        if key in (Qt.Key_Plus, Qt.Key_Equal, Qt.Key_Minus, Qt.Key_Up, Qt.Key_Down):
            if self._handle_volume_keys(key, mods):
                return True
        if key in (Qt.Key_Left, Qt.Key_Right):
            ms = 1000
            if mods == Qt.ControlModifier: ms = 100
            elif mods == Qt.ShiftModifier: ms = 3000
            if key == Qt.Key_Left: ms = -ms
            if hasattr(self, "seek_relative_time"):
                self.seek_relative_time(ms)
                return True
        if key in (Qt.Key_Return, Qt.Key_Enter):
            if hasattr(self, "_on_process_clicked"):
                self._on_process_clicked()
                return True
        return False

    def _handle_volume_keys(self, key, modifiers) -> bool:
        try:
            if key in (Qt.Key_Plus, Qt.Key_Equal, Qt.Key_Minus):
                vol_step = 15
                if key == Qt.Key_Minus: vol_step = -15
            else:
                vol_step = 15 if modifiers == Qt.ShiftModifier else 1
                if key == Qt.Key_Down: vol_step = -vol_step
            use_music_target = (
                getattr(self, "volume_shortcut_target", "main") == 'music'
                and hasattr(self, "music_volume_slider")
                and hasattr(self, "_on_music_volume_changed")
            )
            if use_music_target:
                slider = self.music_volume_slider
                eff_func = getattr(self, "_music_eff", lambda: 100)
                callback = self._on_music_volume_changed
                log_name = "Music"
            else:
                slider = getattr(self, "volume_slider", None)
                if not slider: return False
                eff_func = getattr(self, "_vol_eff", lambda: 100)
                callback = getattr(self, "_on_master_volume_changed", lambda x: None)
                log_name = "Main"
            current_eff_vol = eff_func()
            new_eff_vol = max(0, min(100, current_eff_vol + vol_step))
            if slider.invertedAppearance():
                new_raw_val = slider.maximum() + slider.minimum() - new_eff_vol
            else:
                new_raw_val = new_eff_vol
            new_raw_val = max(slider.minimum(), min(slider.maximum(), new_raw_val))
            slider.setValue(new_raw_val)
            callback(new_raw_val)
            return True
        except Exception: return False

    def seek_relative_time(self, ms_offset: int):
        if not getattr(self, "player", None): return
        try:
            current_time = (getattr(self.player, "time-pos", 0) or 0) * 1000
            new_time = max(0, current_time + ms_offset)
            max_time = (getattr(self.player, "duration", 0) or 0) * 1000
            if max_time > 0: new_time = min(new_time, max_time)
            if hasattr(self, "set_player_position"): self.set_player_position(new_time)
            if getattr(self.player, "pause", True) and hasattr(self, "positionSlider"):
                self.positionSlider.setValue(int(new_time))
        except Exception: pass
