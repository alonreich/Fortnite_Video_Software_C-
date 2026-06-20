from PyQt5.QtWidgets import QWidget
from PyQt5.QtGui import QPainter, QColor, QPen
from PyQt5.QtCore import Qt, QRect
import logging

class PortraitMaskOverlay(QWidget):
    def __init__(self, parent=None):
        super().__init__(parent)
        self._force_hidden = False
        self.setWindowFlags(Qt.Tool | Qt.FramelessWindowHint)
        self.setAttribute(Qt.WA_TransparentForMouseEvents, True)
        self.setAttribute(Qt.WA_TranslucentBackground, True)
        self.setAttribute(Qt.WA_ShowWithoutActivating, True)
        self.original_video_resolution = "1920x1080"
        self.current_video_frame_size = None
        self.setHidden(True)

    def set_force_hidden(self, hidden):
        self._force_hidden = bool(hidden)
        if self._force_hidden: self.hide()

    def set_video_info(self, resolution_str: str, frame_size):
        if self.original_video_resolution != resolution_str or self.current_video_frame_size != frame_size:
            self.original_video_resolution = resolution_str
            self.current_video_frame_size = frame_size
            self.update()

    def paintEvent(self, event):
        if self._force_hidden or not self.isVisible() or not self.current_video_frame_size:
            return
        painter = QPainter(self)
        try:
            painter.setRenderHint(QPainter.Antialiasing)
            painter.setCompositionMode(QPainter.CompositionMode_Clear)
            painter.fillRect(self.rect(), Qt.transparent)
            painter.setCompositionMode(QPainter.CompositionMode_SourceOver)
            try:
                if not self.original_video_resolution:
                    orig_w, orig_h = 1920, 1080
                else:
                    parts = self.original_video_resolution.lower().split('x')
                    orig_w, orig_h = int(parts[0]), int(parts[1])
            except Exception:
                orig_w, orig_h = 1920, 1080
            if orig_w > 0 and orig_h > 0:
                aspect_ratio = orig_w / orig_h
                if aspect_ratio < 1.0: 
                    return
            else:
                aspect_ratio = 1.77
            frame_w = self.width()
            frame_h = self.height()
            if frame_w <= 0 or frame_h <= 0:
                return
            video_display_w = frame_w
            video_display_h = int(frame_w / aspect_ratio)
            video_x = 0
            video_y = 0
            if video_display_h > frame_h:
                video_display_h = frame_h
                video_display_w = int(frame_h * aspect_ratio)
            video_x = (frame_w - video_display_w) // 2
            video_y = (frame_h - video_display_h) // 2
            portrait_ratio = 1280.0 / 1920.0
            clear_w = int(video_display_h * portrait_ratio)
            if clear_w > video_display_w:
                clear_w = video_display_w
            clear_x_offset = (video_display_w - clear_w) // 2
            clear_rect = QRect(video_x + clear_x_offset, video_y, clear_w, video_display_h)
            dim_color = QColor(0, 0, 0, 165)
            if clear_rect.left() > 0:
                painter.fillRect(0, 0, clear_rect.left(), frame_h, dim_color)
            if clear_rect.right() < frame_w - 1:
                painter.fillRect(clear_rect.right() + 1, 0, frame_w - (clear_rect.right() + 1), frame_h, dim_color)
            pen = QPen(QColor("#2D7F41"))
            pen.setStyle(Qt.DashLine)
            pen.setWidth(2)
            painter.setPen(pen)
            painter.drawLine(clear_rect.left(), video_y, clear_rect.left(), video_y + video_display_h)
            painter.drawLine(clear_rect.right(), video_y, clear_rect.right(), video_y + video_display_h)
        except Exception as e:
            logging.getLogger("PortraitMaskOverlay").error("Overlay draw failed: %s", e)
        finally:
            painter.end()