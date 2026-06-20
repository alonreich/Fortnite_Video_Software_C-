from PyQt5.QtCore import Qt, QRect, QPoint, pyqtSignal, QSize
from PyQt5.QtGui import QPainter, QColor, QFont, QFontMetrics, QPen, QCursor, QPainterPath, QLinearGradient, QBrush
from PyQt5.QtWidgets import QSlider, QStyleOptionSlider, QStyle

class TrimmedSlider(QSlider):
    """
    A custom slider that supports:
    1. Visualizing a 'kept' (trimmed) range.
    2. Visualizing a 'music' range overlay.
    3. Handles for start/end trimming.
    4. Note-shaped music handles.
    5. Enhanced stability for Windows/Python 3.13 environments.
    """
    trim_times_changed = pyqtSignal(int, int)
    music_trim_changed = pyqtSignal(int, int)

    def __init__(self, parent=None):
        super().__init__(Qt.Horizontal, parent)
        self._is_destroying = False
        self.music_v_offset = -6
        self.trimmed_start_ms = 0
        self.trimmed_end_ms = 0
        self._duration_ms = 0
        self.music_start_ms = -1
        self.music_end_ms = -1
        self._show_music = False
        self._hovering_handle = None
        self._dragging_handle = None
        self._hovering_music_handle = None
        self._dragging_music_handle = None
        self._music_drag_offset_ms = 0
        self.setMouseTracking(True)
        self.setMinimumHeight(50)
        if hasattr(self, 'setCursor'):
            self.setCursor(Qt.ArrowCursor)
        self._show_trim_overlays = True

    def enable_trim_overlays(self, enabled):
        """Toggle visualization of the 'kept' (trimmed) range."""
        self._show_trim_overlays = bool(enabled)
        self.update()

    def __del__(self):
        self._is_destroying = True

    def set_duration_ms(self, ms):
        self._duration_ms = max(0, int(ms))
        self.setRange(0, self._duration_ms)
        self.update()

    def set_trim_times(self, start_ms, end_ms):
        self.trimmed_start_ms = max(0, int(start_ms))
        self.trimmed_end_ms = max(self.trimmed_start_ms, int(end_ms))
        self.update()

    def set_music_range(self, start_ms, end_ms, visible=True):
        """Standard method for setting music range."""
        self.music_start_ms = int(start_ms)
        self.music_end_ms = int(end_ms)
        self._show_music = visible
        self.update()

    def set_music_times(self, start_ms, end_ms):
        """[RESTORED] Alias for compatibility."""
        self.set_music_range(start_ms, end_ms, visible=True)

    def set_music_visible(self, visible):
        """[RESTORED] Explicitly set music visibility."""
        self._show_music = visible
        self.update()

    def reset_music_times(self):
        """[RESTORED] Clear music visualization."""
        self.music_start_ms = -1
        self.music_end_ms = -1
        self._show_music = False
        self.update()

    def _fmt(self, ms: int) -> str:
        s = max(0, int(ms) // 1000)
        h, s = divmod(s, 3600)
        m, s = divmod(s, 60)
        if h > 0:
            return f"{h}:{m:02d}:{s:02d}"
        return f"{m}:{s:02d}"

    def _get_groove_rect(self):
        """Returns the actual drawing area for the slider groove."""
        try:
            if getattr(self, '_is_destroying', False):
                return QRect(8, self.height() // 2 - 2, max(1, self.width() - 16), 4)
            opt = QStyleOptionSlider()
            self.initStyleOption(opt)
            style = self.style()
            if not style:
                return QRect(8, self.height() // 2 - 2, max(1, self.width() - 16), 4)
            rect = style.subControlRect(QStyle.CC_Slider, opt, QStyle.SC_SliderGroove, self)
            if rect.height() < 2 or rect.y() < 2:
                return QRect(8, self.height() // 2 - 2, max(1, self.width() - 16), 4)
            return rect
        except Exception:
            return QRect(8, self.height() // 2 - 2, max(1, self.width() - 16), 4)

    def _get_handle_rect(self, handle_type):
        """Returns the rect for start or end trim handles."""
        try:
            groove = self._get_groove_rect()
            val = self.trimmed_start_ms if handle_type == 'start' else self.trimmed_end_ms
            x = self._map_value_to_pos(val)
            w, h = 10, 24
            return QRect(x - w // 2, groove.center().y() - h // 2, w, h)
        except Exception:
            return QRect()

    def _get_music_handle_rect(self, handle_type):
        """Returns the rect for music note handles."""
        try:
            time_ms = self.music_start_ms if handle_type == 'start' else self.music_end_ms
            if time_ms < 0 or not self._show_music: return QRect()
            x = self._map_value_to_pos(time_ms)
            handle_size = 30
            y_center = self._get_groove_rect().center().y() + self.music_v_offset
            y_pos = y_center - (handle_size / 2)
            return QRect(x - handle_size // 2, int(y_pos), handle_size, handle_size)
        except Exception:
            return QRect()

    def _get_playhead_rect(self):
        """Returns the rect for the current position playhead."""
        try:
            groove = self._get_groove_rect()
            x = self._map_value_to_pos(self.value())
            w, h = 15, 40
            return QRect(x - w // 2, groove.center().y() - h // 2, w, h)
        except Exception:
            return QRect()

    def _get_music_line_rect(self):
        """Returns the rect for the music overlay line."""
        try:
            if self.music_end_ms < 0 or not self._show_music:
                return QRect()
            start_x = self._map_value_to_pos(self.music_start_ms)
            end_x = self._map_value_to_pos(self.music_end_ms)
            line_height = 12
            y_center = self._get_groove_rect().center().y() + self.music_v_offset
            y_pos = y_center - (line_height / 2)
            return QRect(start_x, int(y_pos), end_x - start_x, line_height)
        except Exception:
            return QRect()

    def _map_pos_to_value(self, px):
        """Maps pixel position back to slider value."""
        try:
            groove = self._get_groove_rect()
            if groove.width() <= 1: return self.minimum()
            pos = px - groove.left()
            ratio = pos / float(groove.width() - 1)
            val = self.minimum() + ratio * (self.maximum() - self.minimum())
            return int(max(self.minimum(), min(self.maximum(), val)))
        except Exception:
            return self.minimum()

    def _map_value_to_pos(self, value):
        """Safely map a value to pixel position with comprehensive error handling."""
        try:
            if getattr(self, '_is_destroying', False):
                return 8
            groove = self._get_groove_rect()
            if not groove.isValid() or groove.width() <= 1:
                return groove.left() if groove.isValid() else 8
            try:
                minv = int(self.minimum())
                maxv = int(self.maximum())
            except (RuntimeError, AttributeError, ValueError):
                return groove.left()
            if maxv <= minv:
                return groove.left()
            val = max(minv, min(maxv, int(value)))
            span = maxv - minv
            pos = ((val - minv) / float(span)) * (groove.width() - 1)
            return int(groove.left() + pos)
        except Exception:
            try: return self.rect().left() + 8
            except: return 8

    def mousePressEvent(self, event):
        if event.button() == Qt.LeftButton:
            pos = event.pos()
            if self._show_music:
                if self._get_music_handle_rect('start').contains(pos):
                    self._dragging_music_handle = 'start'
                    self.update()
                    return
                if self._get_music_handle_rect('end').contains(pos):
                    self._dragging_music_handle = 'end'
                    self.update()
                    return
                if self._get_music_line_rect().contains(pos):
                    self._dragging_music_handle = 'body'
                    click_time_ms = self._map_pos_to_value(pos.x())
                    self._music_drag_offset_ms = self.music_start_ms - click_time_ms
                    self.update()
                    return
            if self._get_handle_rect('start').contains(pos):
                self._dragging_handle = 'start'
            elif self._get_handle_rect('end').contains(pos):
                self._dragging_handle = 'end'
            elif self._get_playhead_rect().contains(pos):
                self._dragging_handle = 'playhead'
            else:
                val = self._map_pos_to_value(pos.x())
                self.setValue(val)
                self.sliderMoved.emit(val)
                self._dragging_handle = 'playhead'
            self.update()

    def mouseMoveEvent(self, event):
        pos = event.pos()
        if self._dragging_music_handle:
            new_val_ms = self._map_pos_to_value(pos.x())
            video_trim_start_ms = self.trimmed_start_ms
            video_trim_end_ms = self.trimmed_end_ms if self.trimmed_end_ms > 0 else self.maximum()
            if self._dragging_music_handle == 'body':
                duration_ms = self.music_end_ms - self.music_start_ms
                new_start_ms = new_val_ms + self._music_drag_offset_ms
                new_start_ms = max(video_trim_start_ms, new_start_ms)
                if new_start_ms + duration_ms > video_trim_end_ms:
                    new_start_ms = video_trim_end_ms - duration_ms
                self.music_start_ms = new_start_ms
                self.music_end_ms = self.music_start_ms + duration_ms
            elif self._dragging_music_handle == 'start':
                new_start_ms = max(video_trim_start_ms, new_val_ms)
                self.music_start_ms = min(new_start_ms, self.music_end_ms - 100)
            elif self._dragging_music_handle == 'end':
                new_end_ms = min(video_trim_end_ms, new_val_ms)
                self.music_end_ms = max(self.music_start_ms + 100, new_end_ms)
            self.music_trim_changed.emit(self.music_start_ms, self.music_end_ms)
            self.update()
            return
        if self._dragging_handle:
            val = self._map_pos_to_value(pos.x())
            if self._dragging_handle == 'start':
                self.trimmed_start_ms = min(val, self.trimmed_end_ms - 100)
                if self._show_music and self.music_start_ms >= 0:
                    new_m_start = max(self.trimmed_start_ms, self.music_start_ms)
                    dur = self.music_end_ms - self.music_start_ms
                    self.music_start_ms = new_m_start
                    self.music_end_ms = max(self.music_end_ms, new_m_start + 100)
                self.trim_times_changed.emit(self.trimmed_start_ms, self.trimmed_end_ms)
            elif self._dragging_handle == 'end':
                self.trimmed_end_ms = max(val, self.trimmed_start_ms + 100)
                if self._show_music and self.music_end_ms >= 0:
                    new_m_end = min(self.trimmed_end_ms, self.music_end_ms)
                    self.music_end_ms = new_m_end
                    self.music_start_ms = min(self.music_start_ms, new_m_end - 100)
                self.trim_times_changed.emit(self.trimmed_start_ms, self.trimmed_end_ms)
            elif self._dragging_handle == 'playhead':
                self.setValue(val)
                self.sliderMoved.emit(val)
            self.update()
        else:
            new_hover = None
            new_hover_music = None
            if self._show_music:
                if self._get_music_handle_rect('start').contains(pos):
                    new_hover_music = 'start'
                elif self._get_music_handle_rect('end').contains(pos):
                    new_hover_music = 'end'
                elif self._get_music_line_rect().contains(pos):
                    new_hover_music = 'body'
            if not new_hover_music:
                if self._get_handle_rect('start').contains(pos):
                    new_hover = 'start'
                elif self._get_handle_rect('end').contains(pos):
                    new_hover = 'end'
                elif self._get_playhead_rect().contains(pos):
                    new_hover = 'playhead'
            over_groove = self._get_groove_rect().contains(pos)
            if new_hover != self._hovering_handle or new_hover_music != self._hovering_music_handle:
                self._hovering_handle = new_hover
                self._hovering_music_handle = new_hover_music
                self.update()
            if self._hovering_handle or self._hovering_music_handle:
                self.setCursor(Qt.PointingHandCursor)
            elif over_groove:
                self.setCursor(Qt.PointingHandCursor)
            else:
                self.setCursor(Qt.ArrowCursor)

    def mouseReleaseEvent(self, event):
        self._dragging_handle = None
        self._dragging_music_handle = None
        self.update()

    def paintEvent(self, event):
        """Custom paint event with restored high-detail metallic playhead."""
        if getattr(self, '_is_destroying', False):
            return
        p = QPainter(self)
        try:
            p.setRenderHint(QPainter.Antialiasing)
            is_wizard = (self.property("is_wizard_slider") is True or getattr(self, "is_wizard", False) is True)
            if is_wizard:
                groove_rect = QRect(12, 40, self.width() - 24, 6)
                p.setBrush(QColor(25, 35, 45, 220))
                p.setPen(Qt.NoPen)
                p.drawRoundedRect(self.rect(), 10, 10)
            else:
                groove_rect = self._get_groove_rect()
            if not groove_rect.isValid():
                return
            p.setPen(Qt.NoPen)
            p.setBrush(QColor("#3d3d3d"))
            p.drawRoundedRect(groove_rect, 3, 3)
            try:
                dur = self._duration_ms if self._duration_ms > 0 else self.maximum()
                if self._show_trim_overlays and self.trimmed_start_ms >= 0 and self.trimmed_end_ms > 0 and dur > 0:
                    fill_color = QColor("#59B1D5")
                    fill_color.setAlpha(150)
                    p.setBrush(fill_color)
                    kept_left = self._map_value_to_pos(self.trimmed_start_ms)
                    kept_right = self._map_value_to_pos(self.trimmed_end_ms)
                    kept_rect = QRect(kept_left, groove_rect.y(), max(1, kept_right - kept_left), groove_rect.height())
                    p.drawRect(kept_rect)
            except Exception: pass
            try:
                if self._show_music and self.music_start_ms >= 0 and self.music_end_ms > 0:
                    music_color = QColor(255, 105, 180, 100)
                    p.setBrush(music_color)
                    p.setPen(Qt.NoPen)
                    music_rect = self._get_music_line_rect()
                    if music_rect.isValid():
                        p.drawRect(music_rect)
            except Exception: pass
            try:
                f = QFont(self.font())
                f.setPointSize(10)
                p.setFont(f)
                fm = QFontMetrics(f)
                dur_ms = self._duration_ms if self._duration_ms > 0 else self.maximum()
                if dur_ms > 0 and groove_rect.width() > 10:
                    duration_sec = dur_ms / 1000.0
                    if is_wizard:
                        if duration_sec < 60: sub_interval = 10
                        elif duration_sec < 300: sub_interval = 30
                        elif duration_sec < 900: sub_interval = 60
                        else: sub_interval = 120
                        for sec in range(0, int(duration_sec) + 1, sub_interval):
                            ratio = sec / duration_sec
                            x = groove_rect.left() + int(ratio * (groove_rect.width() - 1))
                            is_minute = (sec % 60 == 0)
                            p.setPen(QPen(QColor("#7DD3FC") if is_minute else QColor("#666666"), 1.5 if is_minute else 1))
                            tick_len = 10 if is_minute else 5
                            p.drawLine(x, groove_rect.bottom() + 2, x, groove_rect.bottom() + 2 + tick_len)
                            time_str = self._fmt(sec * 1000)
                            text_width = fm.horizontalAdvance(time_str)
                            p.setPen(QColor("#FFFFFF" if is_minute else "#AAAAAA"))
                            p.drawText(x - text_width // 2, groove_rect.bottom() + 22, time_str)
                    else:
                        major_tick_pixels = 120
                        num_major_ticks = max(1, int(round(groove_rect.width() / major_tick_pixels)))
                        for i in range(num_major_ticks + 1):
                            ratio = i / float(num_major_ticks)
                            ms = dur_ms * ratio
                            x = groove_rect.left() + int(ratio * (groove_rect.width() - 1))
                            p.setPen(QColor(180, 180, 180))
                            p.drawLine(x, groove_rect.bottom() + 1, x, groove_rect.bottom() + 6)
                            time_str = self._fmt(int(ms))
                            text_width = fm.horizontalAdvance(time_str)
                            p.drawText(x - text_width // 2, groove_rect.bottom() + 18, time_str)
            except Exception: pass
            try:
                for handle_type in ['start', 'end']:
                    handle_rect = self._get_handle_rect(handle_type)
                    if not handle_rect.isValid(): continue
                    color = QColor(0, 0, 0, 150)
                    if self._hovering_handle == handle_type or self._dragging_handle == handle_type:
                        color = QColor(230, 126, 34, 200)
                    p.setPen(Qt.NoPen)
                    p.setBrush(color)
                    p.drawRoundedRect(handle_rect, 4, 4)
            except Exception: pass
            try:
                playhead_rect = self._get_playhead_rect()
                if playhead_rect.isValid():
                    knob_w, knob_h = 15, 40
                    cx = playhead_rect.center().x()
                    cy = groove_rect.center().y()
                    knob_rect = QRect(cx - knob_w // 2, cy - knob_h // 2, knob_w, knob_h)
                    g = QLinearGradient(knob_rect.left(), knob_rect.top(), knob_rect.left(), knob_rect.bottom())
                    c1, c2 = QColor("#5a5a5a"), QColor("#9a9a9a")
                    if self._hovering_handle == 'playhead' or self._dragging_handle == 'playhead':
                        c1 = c1.lighter(110); c2 = c2.lighter(110)
                        p.setPen(QPen(QColor("#7DD3FC"), 2))
                    else:
                        p.setPen(QPen(QColor("#111111"), 1))
                    g.setColorAt(0.0, c1); g.setColorAt(0.35, c2)
                    g.setColorAt(0.38, Qt.black); g.setColorAt(0.42, Qt.black)
                    g.setColorAt(0.45, c2); g.setColorAt(0.48, Qt.black)
                    g.setColorAt(0.52, Qt.black); g.setColorAt(0.55, c2)
                    g.setColorAt(0.58, Qt.black); g.setColorAt(0.62, Qt.black)
                    g.setColorAt(0.65, c2); g.setColorAt(1.0, c1)
                    p.setBrush(QBrush(g))
                    p.drawRoundedRect(knob_rect, 2, 2)
            except Exception: pass
            try:
                if self._show_music:
                    for handle_type in ['start', 'end']:
                        handle_rect = self._get_music_handle_rect(handle_type)
                        if not handle_rect.isValid(): continue
                        color = QColor(255, 105, 180, 220)
                        if self._hovering_music_handle == handle_type or self._dragging_music_handle == handle_type:
                            color = QColor(255, 20, 147, 255)
                        p.setPen(color.darker(120))
                        p.setBrush(color)
                        path = QPainterPath()
                        path.addEllipse(handle_rect.x() + handle_rect.width() * 0.1, 
                                        handle_rect.y() + handle_rect.height() * 0.5, 
                                        handle_rect.width() * 0.5, handle_rect.height() * 0.5)
                        path.addRect(handle_rect.x() + handle_rect.width() * 0.5, 
                                     handle_rect.y(), handle_rect.width() * 0.1, 
                                     handle_rect.height() * 0.8)
                        p.drawPath(path)
            except Exception: pass
        except Exception: pass
        finally:
            if p.isActive(): p.end()
MergerTrimmedSlider = TrimmedSlider
