import os
from PyQt5.QtCore import pyqtSignal, Qt
from PyQt5.QtWidgets import QFrame
from ui.styles import UIStyles

class DropAreaFrame(QFrame):
    file_dropped = pyqtSignal(str)
    clicked = pyqtSignal()

    def __init__(self, parent=None):
        super().__init__(parent)
        self.setAcceptDrops(True)
        self.setObjectName('dropArea')
        self.setStyleSheet(UIStyles.get_drop_area_style(False))
        self._press_pos = None

    def dragEnterEvent(self, event):
        if event.mimeData().hasUrls():
            event.acceptProposedAction()
            self.setStyleSheet(UIStyles.get_drop_area_style(True))

    def dragLeaveEvent(self, event):
        self.setStyleSheet(UIStyles.get_drop_area_style(False))

    def dragMoveEvent(self, event):
        if event.mimeData().hasUrls():
            event.acceptProposedAction()

    def dropEvent(self, event):
        self.setStyleSheet(UIStyles.get_drop_area_style(False))
        for url in event.mimeData().urls():
            file_path = url.toLocalFile()
            if os.path.isfile(file_path):
                self.file_dropped.emit(file_path)
                return

    def mousePressEvent(self, event):
        if event.button() == Qt.LeftButton:
            self._press_pos = event.pos()
            event.accept()
            return
        super().mousePressEvent(event)

    def mouseReleaseEvent(self, event):
        if event.button() == Qt.LeftButton and self._press_pos is not None:
            release_pos = event.pos()
            press_pos = self._press_pos
            self._press_pos = None
            if self.rect().contains(release_pos) and (release_pos - press_pos).manhattanLength() <= 8:
                self.clicked.emit()
                event.accept()
                return
        super().mouseReleaseEvent(event)