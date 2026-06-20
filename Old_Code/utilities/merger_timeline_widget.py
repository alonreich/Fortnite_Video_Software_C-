import os
import sys
from PyQt5.QtWidgets import QWidget
from PyQt5.QtCore import Qt, QRect, QPoint, pyqtSignal, QRectF
from PyQt5.QtGui import QPainter, QColor, QFont, QPen, QBrush, QPixmap

class MergerTimelineWidget(QWidget):
    clicked_pos = pyqtSignal(float)

    def __init__(self, parent=None):
        super().__init__(parent)
        self.total_duration = 1.0
        self.video_segments = []
        self.music_segments = []
        self.current_time = 0.0
        self.setMinimumHeight(88)
        self.setCursor(Qt.PointingHandCursor)
        self.setMouseTracking(True)
        self.setAttribute(Qt.WA_OpaquePaintEvent)
        self.setAttribute(Qt.WA_NoSystemBackground)
        self._bg_buffer = QPixmap()
        self._needs_repaint = True

    def set_data(self, total_dur, videos, music):
        self.total_duration = max(0.1, total_dur)
        self.video_segments = videos
        self.music_segments = music
        self._needs_repaint = True
        self.update()

    def set_current_time(self, t):
        new_time = max(0.0, min(self.total_duration, t))
        if abs(self.current_time - new_time) > 0.001:
            self.current_time = new_time
            self.update()

    def resizeEvent(self, event):
        self._needs_repaint = True
        super().resizeEvent(event)

    def _render_background(self):
        """Draws the static heavy content (thumbnails, waveforms) to a pixmap."""
        w = self.width()
        h = self.height()
        if w <= 0 or h <= 0: return
        if self._bg_buffer.size() != self.size():
            self._bg_buffer = QPixmap(self.size())
        self._bg_buffer.fill(QColor(15, 25, 35))
        p = QPainter(self._bg_buffer)
        p.setRenderHint(QPainter.Antialiasing, False)
        p.setRenderHint(QPainter.SmoothPixmapTransform, True)
        lane_h = 40.0
        lane_gap = 5.0
        content_h = (lane_h * 2.0) + lane_gap
        top_pad = max(0.0, min(5.0, float(h) - content_h))
        base_v_y = top_pad
        base_m_y = base_v_y + lane_h + lane_gap
        v_y = base_v_y + 2.0
        m_y = base_m_y + 5.0
        current_x = 0.0
        separator_color = QColor("#7DD3FC")
        for i, seg in enumerate(self.video_segments):
            dur = seg.get("duration", 0)
            seg_w = (dur / self.total_duration) * w
            rect_f = QRectF(current_x, v_y, seg_w, lane_h)
            p.save()
            p.setClipRect(rect_f)
            p.fillRect(rect_f, QColor(45, 65, 85))
            thumbs = seg.get("thumbs", [])
            if thumbs:
                num_thumbs = len(thumbs)
                if seg_w > 1:
                    t_render_w = seg_w / float(num_thumbs)
                    for t_idx, thumb in enumerate(thumbs):
                        t_pos_x = current_x + (t_idx * t_render_w)
                        if t_pos_x < w and t_pos_x + t_render_w > 0:
                            p.drawPixmap(QRectF(t_pos_x, v_y, t_render_w + 0.5, lane_h), thumb, QRectF(thumb.rect()))
            else:
                p.setPen(QPen(QColor(125, 211, 252, 120), 1))
                font = QFont("Arial", 10, QFont.Bold)
                p.setFont(font)
                fm = p.fontMetrics()
                txt = " 🕒 Loading... "
                tw = fm.width(txt)
                if tw > 0:
                    num_repeats = max(1, int(seg_w / tw) + 1)
                    for r in range(num_repeats):
                        p.drawText(QRectF(current_x + (r * tw), v_y, tw, lane_h), Qt.AlignCenter, txt)
            p.restore()
            if i < len(self.video_segments) - 1:
                p.setPen(QPen(separator_color, 3))
                sep_x = int(current_x + seg_w)
                p.drawLine(sep_x, int(v_y), sep_x, int(v_y + lane_h))
            current_x += seg_w
        current_x = 0.0
        for i, seg in enumerate(self.music_segments):
            dur = seg.get("duration", 0)
            seg_w = (dur / self.total_duration) * w
            rect_f = QRectF(current_x, m_y, seg_w, lane_h)
            p.fillRect(rect_f, QColor(10, 20, 24))
            wave = seg.get("wave")
            if wave and not wave.isNull():
                p.drawPixmap(rect_f, wave, QRectF(wave.rect()))
                p.fillRect(rect_f, QColor(0, 229, 255, 24))
            p.setPen(QPen(QColor(0, 229, 255, 110), 1))
            cl_y = int(m_y + lane_h/2)
            p.drawLine(int(current_x), cl_y, int(current_x + seg_w), cl_y)
            p.setPen(QPen(separator_color, 3))
            sep_x = int(current_x + seg_w)
            if sep_x < w:
                p.drawLine(sep_x, int(m_y), sep_x, int(m_y + lane_h))
            current_x += seg_w
        p.setPen(QPen(QColor("#266b89"), 2))
        p.setBrush(Qt.NoBrush)
        p.drawRect(QRectF(0, v_y, w, lane_h))
        p.drawRect(QRectF(0, m_y, w, lane_h))
        p.end()
        self._needs_repaint = False

    def paintEvent(self, event):
        if self._needs_repaint or self._bg_buffer.isNull():
            self._render_background()
        p = QPainter(self)
        p.drawPixmap(0, 0, self._bg_buffer)
        w = float(self.width())
        h = float(self.height())
        caret_x = (self.current_time / self.total_duration) * w
        caret_top = -5.0
        p.setPen(QPen(QColor(52, 152, 219, 100), 6))
        p.drawLine(int(caret_x), int(caret_top), int(caret_x), int(h))
        p.setPen(QPen(QColor(255, 255, 255), 2))
        p.drawLine(int(caret_x), int(caret_top), int(caret_x), int(h))
        p.setBrush(QColor(52, 152, 219))
        p.setPen(QPen(Qt.white, 1))
        handle_poly = [QPoint(int(caret_x) - 10, int(caret_top)), QPoint(int(caret_x) + 10, int(caret_top)), QPoint(int(caret_x), 15)]
        p.drawPolygon(*handle_poly)

    def mousePressEvent(self, event):
        if event.button() == Qt.LeftButton:
            self._process_mouse_seek(event.pos())

    def mouseMoveEvent(self, event):
        if event.buttons() & Qt.LeftButton:
            self._process_mouse_seek(event.pos())

    def _process_mouse_seek(self, pos):
        w = float(self.width())
        if w > 0:
            pct = pos.x() / w
            self.clicked_pos.emit(max(0.0, min(1.0, pct)))
