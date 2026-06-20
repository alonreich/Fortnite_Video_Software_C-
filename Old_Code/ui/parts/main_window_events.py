from PyQt5.QtCore import *
from PyQt5.QtGui import *
from PyQt5.QtWidgets import *

class MainWindowEventsMixin:
    def keyPressEvent(self, event):
        if event.key() == Qt.Key_F11:
            btn = getattr(self, "adv_editor_btn", None)
            if btn is None or btn.isEnabled():
                self.launch_advanced_editor()
        elif event.key() == Qt.Key_F12:
            self.launch_crop_tool()
        else:
            QMainWindow.keyPressEvent(self, event)

    def mousePressEvent(self, event):
        try:
            if event.button() == Qt.LeftButton:
                self.setFocus(Qt.MouseFocusReason)
        except Exception as e:
            if hasattr(self, 'logger'):
                self.logger.error("MousePress error: %s", e)
        QMainWindow.mousePressEvent(self, event)

    def moveEvent(self, event):
        if hasattr(self, 'handle_persistence_event'):
            self.handle_persistence_event()
        if hasattr(self, "_update_overlay_positions"):
            self._update_overlay_positions()
        QMainWindow.moveEvent(self, event)

    def resizeEvent(self, event):
        if hasattr(self, "_update_upload_hint_responsive"):
            self._update_upload_hint_responsive()
            QTimer.singleShot(0, self._update_upload_hint_responsive)
        if hasattr(self, "_update_overlay_positions"):
            self._update_overlay_positions()
        if hasattr(self, '_resize_timer'):
            self._resize_timer.start()
        else:
            if hasattr(self, '_delayed_resize_event'):
                self._delayed_resize_event()
        QMainWindow.resizeEvent(self, event)

    def _delayed_resize_event(self):
        try:
            if hasattr(self, "_update_upload_hint_responsive"):
                self._update_upload_hint_responsive()
            if hasattr(self, "_update_volume_badge"):
                self._update_volume_badge()
            if hasattr(self, "_resize_overlay"):
                self._resize_overlay()
            if hasattr(self, "_adjust_trim_margins"):
                self._adjust_trim_margins()
            if hasattr(self, "_update_portrait_mask_overlay_state"):
                self._update_portrait_mask_overlay_state()
            if hasattr(self, "_update_overlay_positions"):
                self._update_overlay_positions()
        except Exception:
            pass

    def closeEvent(self, event):
        self._shutting_down = True
        if hasattr(self, 'save_geometry'):
            self.save_geometry()
        if getattr(self, "is_processing", False):
            reply = QMessageBox.question(self, "Quit During Processing",
                "A video is currently being processed. Closing now will cancel all progress. Quit anyway?",
                QMessageBox.Yes | QMessageBox.No, QMessageBox.No)
            if reply == QMessageBox.No:
                event.ignore()
                return
        self.blockSignals(True)
        if not getattr(self, "_preserve_child_processes_on_close", False):
            try:
                import psutil
                current_process = psutil.Process()
                children = current_process.children(recursive=True)
                for child in children:
                    try:
                        if hasattr(self, 'logger'):
                            self.logger.info(f"EXIT: Killing child process {child.pid} ({child.name()})")
                        child.kill()
                    except: pass
            except: pass
        if hasattr(self, 'cleanup_and_exit'):
            try:
                self.cleanup_and_exit()
            except Exception as e:
                if hasattr(self, 'logger'): self.logger.error(f"EXIT: Cleanup error: {e}")
        event.accept()
        QMainWindow.closeEvent(self, event)
