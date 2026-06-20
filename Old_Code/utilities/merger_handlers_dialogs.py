from PyQt5.QtWidgets import QDialog, QVBoxLayout, QHBoxLayout, QLabel, QGridLayout, QPushButton, QApplication, QSizePolicy, QMessageBox, QFrame
from PyQt5.QtCore import QUrl, Qt, QPropertyAnimation, QTimer, QEasingCurve
from PyQt5.QtGui import QDesktopServices
from pathlib import Path
import os
import sys
import subprocess
from utilities.merger_utils import _human

class MergerHandlersDialogsMixin:
    def _dialog_button_style(self, color: str, pressed: str, *, font_size: int = 12) -> str:
        return f"""
            QPushButton {{
                background: qlineargradient(x1:0, y1:0, x2:0, y2:1, stop:0 {color}, stop:1 {pressed});
                color: white;
                font-weight: bold;
                font-family: Arial;
                font-size: {font_size}px;
                border-radius: 8px;
                border: 1px solid rgba(0,0,0,0.45);
                padding: 0px;
                text-align: center;
                min-width: 180px;
                max-width: 180px;
                min-height: 45px;
                max-height: 45px;
            }}
            QPushButton:hover {{ border: 1px solid #7DD3FC; }}
            QPushButton:pressed {{
                background: qlineargradient(x1:0, y1:0, x2:0, y2:1, stop:0 {pressed}, stop:1 {color});
            }}
        """

    def open_output_in_explorer(self, file_path: str):
        full_path = os.path.abspath(file_path)
        if not os.path.exists(full_path):
            self.logger.warning("OPEN_EXPLORER: File does not exist: %s", full_path)
            return
        try:
            if os.name == "nt":
                subprocess.run(['explorer', '/select,', os.path.normpath(full_path)], check=False)
            elif sys.platform == "darwin":
                subprocess.Popen(["open", "-R", full_path])
            else:
                subprocess.Popen(["xdg-open", os.path.dirname(full_path)])
            self.logger.info("OPEN_EXPLORER: Opened and selected %s", full_path)
        except Exception as e:
            self.logger.error("OPEN_EXPLORER: Failed to open explorer for %s | Error: %s", full_path, e)

    def open_music_wizard(self):
        from utilities.merger_music_wizard import MergerMusicWizard
        import os
        total_sec = self.parent.estimate_total_duration_seconds()
        if total_sec <= 0:
            QMessageBox.warning(self.parent, "No Videos", "Please add at least one video first so we know how much music you need!")
            return
        mp3_dir = os.path.join(self.parent.base_dir, "mp3")
        wizard = MergerMusicWizard(
            self.parent, 
            getattr(self.parent, "player", None), 
            self.parent.bin_dir, 
            mp3_dir, 
            total_sec
        )
        if wizard.exec_() == QDialog.Accepted:
            if hasattr(self.parent, "unified_music_widget"):
                m_vol = wizard.music_vol_slider.value()
                v_vol = wizard.video_vol_slider.value()
                self.parent.unified_music_widget.set_wizard_tracks(wizard.selected_tracks, music_vol=m_vol, video_vol=v_vol)
                if hasattr(self.parent, "set_status_message"):
                    self.parent.set_status_message("✅ Music selection applied!", "color: #43b581; font-weight: bold;", 3000, force=True)

    def share_via_whatsapp(self, file_path: str = None):
        try:
            QDesktopServices.openUrl(QUrl("https://web.whatsapp.com"))
            if file_path:
                self.open_output_in_explorer(file_path)
        except Exception as err:
            self.logger.error("share_via_whatsapp error: %s", err)

    def show_success_dialog(self, output_path: str):
        """Displays success dialog using the synced ultra-polished layout/feel from main app."""

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

        dialog = FinishedDialog(self.parent)
        dialog.setWindowTitle("Done! Video Processed Successfully!")
        dialog.setModal(True)
        dialog.setFixedSize(760, 420)
        
        outer = QVBoxLayout(dialog)
        outer.setContentsMargins(0, 0, 0, 0)
        outer.setSpacing(0)
        
        frame = QFrame(dialog)
        frame.setObjectName("finishedFrame")
        frame.setStyleSheet("QFrame#finishedFrame { background-color: #0b141d; border: 2px solid #7DD3FC; border-radius: 14px; }")
        outer.addWidget(frame)
        
        layout = QVBoxLayout(frame)
        layout.setContentsMargins(28, 18, 28, 30)
        layout.setSpacing(18)
        
        top_row = QHBoxLayout()
        top_row.setContentsMargins(0, 0, 0, 0)
        top_row.addStretch(1)
        
        close_btn = QPushButton("X", frame)
        close_btn.setFixedSize(60, 52)
        close_btn.setStyleSheet("QPushButton { background-color: transparent; color: #ff4d4d; font-size: 42px; font-weight: bold; border: none; } QPushButton:hover { color: #ff0000; }")
        close_btn.setCursor(Qt.PointingHandCursor)
        close_btn.clicked.connect(dialog.fade_accept)
        top_row.addWidget(close_btn)
        layout.addLayout(top_row)
        
        label = QLabel(f"File successfully saved to:\n{output_path}")
        label.setStyleSheet("font-size: 16px; font-weight: bold; color: #7DD3FC;")
        label.setAlignment(Qt.AlignCenter)
        layout.addWidget(label)
        
        grid = QGridLayout()
        grid.setHorizontalSpacing(42)
        grid.setVerticalSpacing(28)
        grid.setContentsMargins(80, 18, 80, 8)
        
        whatsapp_button = QPushButton("✆  WHATSAPP SHARE  ✆")
        whatsapp_button.setStyleSheet(self._dialog_button_style("#3CA557", "#2B7D40"))
        whatsapp_button.clicked.connect(lambda: self.share_via_whatsapp(output_path))
        whatsapp_button.clicked.connect(lambda: dialog.fade_done(QDialog.Accepted))
        
        open_folder_button = QPushButton("OPEN FOLDER")
        open_folder_button.setStyleSheet(self._dialog_button_style("#2e82a0", "#1e648c"))
        open_folder_button.clicked.connect(lambda: self.open_output_in_explorer(output_path))
        open_folder_button.clicked.connect(lambda: dialog.fade_done(QDialog.Accepted))
        
        new_file_button = QPushButton("📂  UPLOAD NEW  📂")
        new_file_button.setStyleSheet(self._dialog_button_style("#2e82a0", "#1e648c"))
        new_file_button.clicked.connect(dialog.fade_reject)
        
        exit_button = QPushButton("EXIT APP!")
        exit_button.setStyleSheet(self._dialog_button_style("#c0392b", "#a93226"))
        exit_button.clicked.connect(lambda: dialog.fade_done(999))
        
        for b in [whatsapp_button, open_folder_button, new_file_button, exit_button]:
            b.setFixedSize(180, 45)
            b.setCursor(Qt.PointingHandCursor)
            
        grid.addWidget(whatsapp_button, 0, 0, alignment=Qt.AlignCenter)
        grid.addWidget(open_folder_button, 0, 1, alignment=Qt.AlignCenter)
        grid.addWidget(new_file_button, 1, 0, alignment=Qt.AlignCenter)
        grid.addWidget(exit_button, 1, 1, alignment=Qt.AlignCenter)
        layout.addLayout(grid)
        
        result = dialog.exec_()
        
        try:
            out_sz = Path(output_path).stat().st_size if output_path else 0
            self.logger.info("MERGE_DONE: output='%s' | size=%s", output_path, _human(out_sz))
        except Exception:
            pass
            
        return result
