from PyQt5.QtWidgets import QListWidget, QWidget, QHBoxLayout, QLabel
from PyQt5.QtCore import Qt, QTimer, pyqtSignal

class SearchableListWidget(QListWidget):
    buffer_changed = pyqtSignal(str)

    def __init__(self, parent=None):
        super().__init__(parent)
        self._search_buffer = ""
        self._buffer_timer = QTimer(self)
        self._buffer_timer.setSingleShot(True)
        self._buffer_timer.setInterval(1500)
        self._buffer_timer.timeout.connect(self._clear_buffer)
        self._overlay_label = QLabel(self)
        self._overlay_label.setStyleSheet("""
            background-color: rgba(0, 0, 0, 180);
            color: #7DD3FC;
            font-size: 24px;
            font-weight: bold;
            padding: 10px 20px;
            border-radius: 8px;
            border: 2px solid #3498db;
        """)
        self._overlay_label.hide()
        self._overlay_label.setAttribute(Qt.WA_TransparentForMouseEvents)

    def _clear_buffer(self):
        self._search_buffer = ""
        self.buffer_changed.emit("")
        self._overlay_label.hide()

    def resizeEvent(self, event):
        super().resizeEvent(event)
        if self._overlay_label.isVisible():
            self._center_overlay()

    def _center_overlay(self):
        sz = self._overlay_label.sizeHint()
        x = (self.width() - sz.width()) // 2
        y = (self.height() - sz.height()) // 2
        self._overlay_label.move(x, y)
        self._overlay_label.adjustSize()

    def keyPressEvent(self, event):
        text = event.text()
        if event.key() == Qt.Key_Backspace:
            if self._search_buffer:
                self._search_buffer = self._search_buffer[:-1]
                self._update_search()
            return
        if text and len(text) == 1 and event.modifiers() == Qt.NoModifier:
            self._buffer_timer.stop()
            self._search_buffer += text.lower()
            self._update_search()
            return
        super().keyPressEvent(event)

    def _update_search(self):
        self.buffer_changed.emit(self._search_buffer)
        if self._search_buffer:
            self._overlay_label.setText(self._search_buffer.upper())
            self._overlay_label.adjustSize()
            self._center_overlay()
            self._overlay_label.show()
            self._overlay_label.raise_()
        else:
            self._overlay_label.hide()
        self._buffer_timer.start()
        for i in range(self.count()):
            item = self.item(i)
            if not item.isHidden():
                w = self.itemWidget(item)
                if w and hasattr(w, 'name_lbl'):
                    clean_text = w.name_lbl.text().lower()
                    if clean_text.startswith(self._search_buffer):
                        self.setCurrentItem(item)
                        self.scrollToItem(item)
                        return

class MusicItemWidget(QWidget):
    def __init__(self, filename, parent=None):
        super().__init__(parent)
        self.setAttribute(Qt.WA_NoSystemBackground)
        self.setStyleSheet("background: transparent; border: none;")
        layout = QHBoxLayout(self)
        layout.setContentsMargins(15, 2, 10, 2)
        layout.setSpacing(10)
        self.note_lbl = QLabel("♪")
        self.note_lbl.setStyleSheet("font-size: 18px; color: #7DD3FC; font-weight: bold; background: transparent;")
        self.name_lbl = QLabel(filename)
        self.name_lbl.setStyleSheet("font-size: 14px; color: #ecf0f1; background: transparent;")
        layout.addWidget(self.note_lbl)
        layout.addWidget(self.name_lbl, 1)
        self.setLayout(layout)
