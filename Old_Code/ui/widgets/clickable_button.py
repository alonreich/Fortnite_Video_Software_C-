from PyQt5.QtWidgets import QPushButton
from PyQt5.QtCore import Qt

class ClickableButton(QPushButton):
    def __init__(self, *args, **kwargs):
        super().__init__(*args, **kwargs)
        self.setCursor(Qt.PointingHandCursor)