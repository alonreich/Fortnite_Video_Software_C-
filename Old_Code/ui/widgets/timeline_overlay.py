from PyQt5.QtWidgets import QWidget, QVBoxLayout, QHBoxLayout
from PyQt5.QtCore import Qt, QRect, QPoint
from ui.widgets.trimmed_slider import TrimmedSlider

class TimelineOverlay(QWidget):
    def __init__(self, parent=None):
        super().__init__(parent)
        self._force_hidden = True
        flags = getattr(Qt, "Tool", 0) | getattr(Qt, "FramelessWindowHint", 0) | getattr(Qt, "WindowStaysOnTopHint", 0)
        self.setWindowFlags(flags)
        self.setAttribute(Qt.WA_TranslucentBackground, True)
        self.setAttribute(Qt.WA_ShowWithoutActivating, True)
        self.setAttribute(Qt.WA_TransparentForMouseEvents, False)
        self.layout = QVBoxLayout(self)
        self.layout.setContentsMargins(0, 0, 0, 0)
        self.layout.setSpacing(0)
        self.slider_wrap = QWidget()
        self.slider_wrap.setStyleSheet("background: transparent; border: none;")
        self.slider_layout = QHBoxLayout(self.slider_wrap)
        self.slider_layout.setContentsMargins(0, 0, 0, 0)
        self.positionSlider = TrimmedSlider(Qt.Horizontal, self)
        self.positionSlider.setFixedHeight(60)
        self.slider_layout.addWidget(self.positionSlider)
        self.layout.addWidget(self.slider_wrap)
        self.setHidden(True)

    def set_force_hidden(self, hidden):
        self._force_hidden = bool(hidden)
        if self._force_hidden: self.hide()

    def update_geometry(self, video_surface, original_res="1920x1080"):
        if self._force_hidden:
            self.hide(); return
        if not video_surface or not video_surface.isVisible() or not video_surface.window().isVisible():
            self.hide(); return
        try:
            try:
                parts = str(original_res).lower().split('x')
                orig_w, orig_h = int(parts[0]), int(parts[1])
            except:
                orig_w, orig_h = 1920, 1080
            aspect = orig_w / orig_h if orig_h > 0 else 1.77
            surf_w, surf_h = video_surface.width(), video_surface.height()
            if surf_w <= 0 or surf_h <= 0: return
            disp_w = surf_w
            disp_h = int(surf_w / aspect)
            if disp_h > surf_h:
                disp_h = surf_h
                disp_w = int(surf_h * aspect)
            offset_x = (surf_w - disp_w) // 2
            tl = video_surface.mapToGlobal(QPoint(0, 0))
            overlay_h = 60
            target_rect = QRect(tl.x() + offset_x, tl.y() + surf_h - overlay_h - 45, disp_w, overlay_h)
            if self.geometry() != target_rect:
                self.setGeometry(target_rect)
            if self.isHidden():
                self.show()
            self.raise_()
        except: pass
