from PyQt5.QtCore import Qt, QRect, QTimer, QMimeData, QPoint
from PyQt5.QtGui import QPainter, QColor, QLinearGradient, QDrag, QPixmap
from PyQt5.QtWidgets import QListWidget, QAbstractItemView, QApplication

class DraggableListWidget(QListWidget):
    def __init__(self, parent=None):
        super().__init__(parent)
        self.setDragDropMode(QAbstractItemView.InternalMove)
        self.drop_indicator_rect = None
        self.drop_preview_rect = None
        self.enable_custom_drop_preview = False
        self.setCursor(Qt.PointingHandCursor)
        self.auto_scroll_timer = QTimer()
        self.auto_scroll_timer.timeout.connect(self._auto_scroll)
        self.auto_scroll_direction = 0
        self.drag_scroll_margin = 120
        self.scroll_speed = 15
        self._drag_item_index = -1
        self._last_drag_pos = None
        self.setDragEnabled(True)
        self.setAcceptDrops(True)
        self.setDropIndicatorShown(True)
        self.setSelectionMode(QAbstractItemView.SingleSelection)

    def dragMoveEvent(self, event):
        try:
            super().dragMoveEvent(event)
            if not self.enable_custom_drop_preview:
                return
            index = self.indexAt(event.pos())
            pos = self.dropIndicatorPosition()
            self.drop_indicator_rect = None
            self.drop_preview_rect = None
            if not index.isValid() or pos == QAbstractItemView.OnViewport:
                if self.viewport():
                    self.viewport().update()
                return
            rect = self.visualRect(index)
            if rect.isNull():
                return
            item_height = max(24, rect.height())
            if pos == QAbstractItemView.OnItem:
                pos = QAbstractItemView.AboveItem if event.pos().y() < rect.center().y() else QAbstractItemView.BelowItem
            if pos == QAbstractItemView.AboveItem:
                self.drop_indicator_rect = QRect(rect.left(), rect.top() - 2, rect.width(), 4)
                self.drop_preview_rect = QRect(rect.left(), rect.top() - item_height, rect.width(), item_height)
            elif pos == QAbstractItemView.BelowItem:
                self.drop_indicator_rect = QRect(rect.left(), rect.bottom() - 2, rect.width(), 4)
                self.drop_preview_rect = QRect(rect.left(), rect.bottom(), rect.width(), item_height)
            self._handle_auto_scroll_during_drag(event.pos())
            if self.viewport():
                self.viewport().update()
        except Exception:
            self.drop_indicator_rect = None
            self.drop_preview_rect = None
            self.auto_scroll_timer.stop()
            self.auto_scroll_direction = 0
            if self.viewport():
                self.viewport().update()

    def _handle_auto_scroll_during_drag(self, pos):
        viewport = self.viewport()
        viewport_rect = viewport.rect()
        viewport_height = viewport_rect.height()
        mouse_y = pos.y()
        scroll_factor = 0
        if mouse_y < self.drag_scroll_margin:
            self.auto_scroll_direction = -1
            scroll_factor = 1.0 - (mouse_y / self.drag_scroll_margin)
            self.scroll_speed = int(8 + (scroll_factor * 22))
            if not self.auto_scroll_timer.isActive():
                self.auto_scroll_timer.start(30)
        elif mouse_y > viewport_height - self.drag_scroll_margin:
            self.auto_scroll_direction = 1
            distance_from_edge = viewport_height - mouse_y
            scroll_factor = 1.0 - (distance_from_edge / self.drag_scroll_margin)
            self.scroll_speed = int(8 + (scroll_factor * 22))
            if not self.auto_scroll_timer.isActive():
                self.auto_scroll_timer.start(30)
        else:
            self.auto_scroll_direction = 0
            self.auto_scroll_timer.stop()

    def _auto_scroll(self):
        if self.auto_scroll_direction == -1:
            self.verticalScrollBar().setValue(
                self.verticalScrollBar().value() - self.scroll_speed
            )
        elif self.auto_scroll_direction == 1:
            self.verticalScrollBar().setValue(
                self.verticalScrollBar().value() + self.scroll_speed
            )
        else:
            self.auto_scroll_timer.stop()

    def dragLeaveEvent(self, event):
        self.drop_indicator_rect = None
        self.drop_preview_rect = None
        self.auto_scroll_timer.stop()
        self.auto_scroll_direction = 0
        if self.viewport():
            self.viewport().update()
        super().dragLeaveEvent(event)

    def dropEvent(self, event):
        self.drop_indicator_rect = None
        self.drop_preview_rect = None
        self.auto_scroll_timer.stop()
        self.auto_scroll_direction = 0
        super().dropEvent(event)
        if self.viewport():
            self.viewport().update()

    def paintEvent(self, event):
        super().paintEvent(event)
        if not self.enable_custom_drop_preview:
            return
        if not self.viewport():
            return
        preview_rect = self.drop_preview_rect if self.drop_preview_rect and self.drop_preview_rect.isValid() else None
        indicator_rect = self.drop_indicator_rect if self.drop_indicator_rect and self.drop_indicator_rect.isValid() else None
        if not preview_rect and not indicator_rect:
            return
        painter = QPainter(self.viewport())
        try:
            if preview_rect:
                preview_color = QColor(80, 140, 220, 90)
                painter.fillRect(preview_rect, preview_color)
                pen = painter.pen()
                pen.setColor(QColor(120, 180, 255, 170))
                pen.setWidth(1)
                painter.setPen(pen)
                painter.drawRect(preview_rect.adjusted(0, 0, -1, -1))
            if indicator_rect:
                gradient = QLinearGradient(indicator_rect.topLeft(), indicator_rect.bottomLeft())
                gradient.setColorAt(0, QColor(0, 170, 255, 255))
                gradient.setColorAt(1, QColor(0, 110, 200, 255))
                painter.fillRect(indicator_rect, gradient)
                pen = painter.pen()
                pen.setColor(QColor(255, 255, 255, 220))
                pen.setWidth(1)
                painter.setPen(pen)
                painter.drawRect(indicator_rect.adjusted(0, 0, -1, -1))
        finally:
            painter.end()
