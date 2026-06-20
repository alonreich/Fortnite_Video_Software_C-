import os, sys, time, threading, logging, subprocess, traceback
from PyQt5.QtCore import *
from PyQt5.QtGui import *
from PyQt5.QtWidgets import *

class MainWindowUiHelpersAMixin:
    def set_style(self):
        try:
            from ui.styles import UIStyles
            self.setStyleSheet(UIStyles.GLOBAL_STYLE)
            if hasattr(self, "_update_upload_hint_responsive"):
                self._update_upload_hint_responsive()
            if hasattr(self, "process_button") and self.process_button:
                self.process_button.setFixedSize(125, 65)
                self.process_button.setStyleSheet("min-width: 106px; max-width: 106px; min-height: 54px; max-height: 54px;")
            if hasattr(self, "playPauseButton") and self.playPauseButton:
                self.playPauseButton.setFixedSize(85, 40)
                self.playPauseButton.setStyleSheet("min-width: 66px; max-width: 66px; min-height: 29px; max-height: 29px;")
            if hasattr(self, "start_trim_button") and self.start_trim_button:
                self.start_trim_button.setFixedSize(90, 37)
                self.start_trim_button.setStyleSheet("min-height: 26px; max-height: 26px; min-width: 72px; max-width: 72px;")
            if hasattr(self, "end_trim_button") and self.end_trim_button:
                self.end_trim_button.setFixedSize(90, 37)
                self.end_trim_button.setStyleSheet("min-height: 26px; max-height: 26px; min-width: 72px; max-width: 72px;")
            for b in (getattr(self, "merge_btn", None), getattr(self, "crop_tool_btn", None), getattr(self, "adv_editor_btn", None)):
                if b:
                    b.setFixedSize(145, 32)
                    b.setStyleSheet("font-size: 10px; padding: 2px 4px; min-height: 25px; max-height: 25px;")
        except Exception as e:
            print(f"Style Load Error: {e}")

    def refresh_ui_styles(self):
        try:
            self.set_style()
            self.style().unpolish(self)
            self.style().polish(self)
            for widget in self.findChildren(QWidget):
                widget.style().unpolish(widget)
                widget.style().polish(widget)
            self.update()
        except Exception:
            pass

    def _init_upload_hint_blink(self):
        if not hasattr(self, 'hint_group_container') or self.hint_group_container is None:
            return

        from PyQt5.QtWidgets import QGraphicsOpacityEffect
        from PyQt5.QtCore import QPropertyAnimation, QAbstractAnimation
        if not hasattr(self, '_hint_opacity_effect'):
            self._hint_opacity_effect = QGraphicsOpacityEffect(self.hint_group_container)
            self._hint_opacity_effect.setOpacity(1.0)
            self.hint_group_container.setGraphicsEffect(self._hint_opacity_effect)
        if not hasattr(self, '_hint_anim'):
            self._hint_anim = QPropertyAnimation(self._hint_opacity_effect, b"opacity")
            self._hint_anim.setDuration(1500)
            self._hint_anim.setStartValue(1.0)
            self._hint_anim.setEndValue(0.2)
            self._hint_anim.setLoopCount(-1)
            self._hint_anim.setDirection(QAbstractAnimation.Backward)
        timer_active = None
        if getattr(self, '_upload_hint_active', False) and (not callable(timer_active) or not timer_active()):
            if self._hint_anim.state() != QAbstractAnimation.Running:
                self._hint_anim.start()
        else:
            self._hint_anim.stop()
            self._hint_opacity_effect.setOpacity(1.0)
