from PyQt5.QtWidgets import QListWidget, QAbstractItemView
from PyQt5.QtCore import Qt, pyqtSignal
import os

class MergerDraggableList(QListWidget):
    """
    Standardized draggable list using QListWidget's built-in InternalMove.
    Fixes performance issues (#7) and visual glitches (#10).
    """
    item_moved_signal = pyqtSignal(int, int)
    drag_started = pyqtSignal(int, str)
    drag_completed = pyqtSignal(int, int, str, str)
    drag_cancelled = pyqtSignal(int, str)
    files_dropped = pyqtSignal(list)
    
    def __init__(self, parent=None):
        super().__init__(parent)
        self.setSelectionMode(QAbstractItemView.ExtendedSelection)
        self.setSelectionRectVisible(True)
        self.setDragEnabled(True)
        self.setAcceptDrops(True)
        self.setDragDropMode(QAbstractItemView.InternalMove)
        self.setDropIndicatorShown(True)
        self.setDefaultDropAction(Qt.MoveAction)
        self.setSpacing(5)
        self.setUniformItemSizes(True)
        self.setVerticalScrollMode(QAbstractItemView.ScrollPerPixel)
        self._drag_start_row = -1
        self._drag_start_name = "Unknown"

    def startDrag(self, supportedActions):
        self._drag_start_row = self.currentRow()
        item = self.currentItem()
        name = "Unknown"
        if item:
            name = os.path.basename(item.data(Qt.UserRole)) if item.data(Qt.UserRole) else item.text()
        self._drag_start_name = name
        self.drag_started.emit(self._drag_start_row, name)
        super().startDrag(supportedActions)

    def dropEvent(self, event):
        if event.source() == self:
            start = self._drag_start_row
            before_name = self._drag_start_name
            super().dropEvent(event)
            end = self.currentRow()
            after_name = "Unknown"
            item = self.item(end)
            if item:
                after_name = os.path.basename(item.data(Qt.UserRole)) if item.data(Qt.UserRole) else item.text()
            if start != -1 and start != end:
                self.drag_completed.emit(start, end, before_name, after_name)
            elif start != -1:
                self.drag_cancelled.emit(start, before_name)
            self._drag_start_row = -1
            self._drag_start_name = "Unknown"
            event.accept()
        else:
            if event.mimeData().hasUrls():
                event.setDropAction(Qt.CopyAction)
                event.accept()
                files = [url.toLocalFile() for url in event.mimeData().urls() if url.isLocalFile()]
                if files:
                    self.files_dropped.emit(files)
            else:
                event.ignore()

    def dragEnterEvent(self, event):
        if event.source() == self:
            event.setDropAction(Qt.MoveAction)
            event.accept()
        elif event.mimeData().hasUrls():
            event.accept()
        else:
            event.ignore()

    def dragMoveEvent(self, event):
        if event.source() == self:
            event.setDropAction(Qt.MoveAction)
            event.accept()
        elif event.mimeData().hasUrls():
            event.setDropAction(Qt.CopyAction)
            event.accept()
        else:
            event.ignore()
