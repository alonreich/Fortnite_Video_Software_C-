from PyQt5.QtCore import QObject, QEvent, QPoint
from PyQt5.QtGui import QCursor
from PyQt5.QtWidgets import QToolTip

class ToolTipManager(QObject):
    def __init__(self, parent=None):
        super().__init__(parent)
        self._tooltips = {}

    def add_tooltip(self, widget, text):
        if widget is None:
            return
        widget.installEventFilter(self)
        self._tooltips[widget.objectName()] = text

    def eventFilter(self, obj, event):
        try:
            if obj is None: return False
            if event.type() == QEvent.ToolTip:
                tooltip_text = obj.toolTip()
                if tooltip_text:
                    offset = QPoint(30, 25) 
                    QToolTip.showText(QCursor.pos() + offset, tooltip_text, obj)
                    return True 
            elif event.type() in (QEvent.Leave, QEvent.MouseButtonPress, QEvent.MouseButtonRelease):
                QToolTip.hideText()
        except (RuntimeError, AttributeError):
            pass
        return super().eventFilter(obj, event)