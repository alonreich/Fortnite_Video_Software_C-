from PyQt5.QtCore import Qt, QRect, QPoint, pyqtSignal, QSize, QTimer
from PyQt5.QtGui import QPainter, QColor, QFont, QFontMetrics, QPen, QCursor, QPainterPath, QLinearGradient, QBrush, QPolygon
from PyQt5.QtWidgets import QSlider, QStyleOptionSlider, QStyle

class TrimmedSlider(QSlider):
    trim_times_changed = pyqtSignal(int, int)
    music_trim_changed = pyqtSignal(int, int)

    def __init__(self, orientation=Qt.Horizontal, parent=None):
        super().__init__(orientation, parent)
        self._is_destroying = False
        self._is_painting = False
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
        self._has_moved_since_press = False
        try:
            self._press_pos = QPoint()
        except TypeError:
            self._press_pos = None
        self.setMouseTracking(True)
        self.setMinimumHeight(50)
        if hasattr(self, 'setCursor'):
            self.setCursor(Qt.PointingHandCursor)
        self._show_trim_overlays = True
        self.speed_segments = []
        self.base_speed = 1.1
        self.thumbnail_pos_ms = -1

    def setValue(self, value):
        super().setValue(value)
        self.update()

    def set_speed_segments(self, segments):
        normalized = []
        for seg in segments or []:
            if not isinstance(seg, dict):
                continue
            try:
                start_ms = int(seg.get("start", seg.get("start_ms", 0)))
                end_ms = int(seg.get("end", seg.get("end_ms", 0)))
                speed = float(seg.get("speed", self.base_speed))
            except Exception:
                continue
            if end_ms > start_ms:
                normalized.append({"start": start_ms, "end": end_ms, "start_ms": start_ms, "end_ms": end_ms, "speed": speed})
        self.speed_segments = normalized
        self.update()

    def set_thumbnail_pos_ms(self, ms):
        self.thumbnail_pos_ms = int(ms)
        self.update()

    def get_thumbnail_pos_ms(self):
        return getattr(self, "thumbnail_pos_ms", 0)

    def enable_trim_overlays(self, enabled):
        self._show_trim_overlays = bool(enabled)
        self.update()

    def set_duration_ms(self, ms):
        self._duration_ms = max(0, int(ms))
        self.setRange(0, self._duration_ms)
        self.update()

    def set_trim_times(self, start_ms, end_ms):
        self.trimmed_start_ms = max(0, int(start_ms))
        self.trimmed_end_ms = max(self.trimmed_start_ms, int(end_ms))
        self.update()

    def set_music_range(self, start_ms, end_ms, visible=True):
        self.music_start_ms = int(start_ms)
        self.music_end_ms = int(end_ms)
        self._show_music = visible
        self.update()

    def set_music_times(self, start_ms, end_ms):
        self.set_music_range(start_ms, end_ms, visible=True)

    def set_music_visible(self, visible):
        self._show_music = visible
        self.update()

    def reset_music_times(self):
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
        margin = 10
        try:
            w = self.width()
            h = self.height()
            gh = 6
            gy = (h - gh) // 2
            gx = margin + 8
            gw = max(1, w - (margin * 2) - 16)
            return QRect(gx, gy, gw, gh)
        except Exception:
            return QRect(18, 23, 100, 6)

    def _get_handle_rect(self, handle_type):
        try:
            groove = self._get_groove_rect()
            val = self.trimmed_start_ms if handle_type == 'start' else self.trimmed_end_ms
            x = self._map_value_to_pos(val)
            w, h = 15, 36
            return QRect(int(x - w // 2), int(groove.center().y() - h // 2), w, h)
        except Exception:
            return QRect()

    def _get_music_handle_rect(self, handle_type):
        try:
            time_ms = self.music_start_ms if handle_type == 'start' else self.music_end_ms
            if time_ms < 0 or not self._show_music: return QRect()
            x = self._map_value_to_pos(time_ms)
            handle_size = 40
            y_center = self._get_groove_rect().center().y() + self.music_v_offset
            y_pos = y_center - (handle_size / 2)
            return QRect(int(x - handle_size // 2), int(y_pos), handle_size, handle_size)
        except Exception:
            return QRect()

    def _get_playhead_rect(self):
        try:
            groove = self._get_groove_rect()
            x = self._map_value_to_pos(self.value())
            size = 45
            return QRect(int(x - size // 2), int(groove.center().y() - size // 2), size, size)
        except Exception:
            return QRect()

    def _get_music_line_rect(self):
        try:
            if self.music_end_ms < 0 or not self._show_music:
                return QRect()
            start_x = self._map_value_to_pos(self.music_start_ms)
            end_x = self._map_value_to_pos(self.music_end_ms)
            line_height = 18
            y_center = self._get_groove_rect().center().y() + self.music_v_offset
            y_pos = y_center - (line_height / 2)
            return QRect(int(start_x), int(y_pos), int(end_x - start_x), line_height)
        except Exception:
            return QRect()

    def _map_pos_to_value(self, px):
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
        try:
            if self._is_destroying:
                return 8
            groove = self._get_groove_rect()
            if not groove.isValid() or groove.width() <= 1:
                return groove.left() if groove.isValid() else 8
            minv, maxv = self.minimum(), self.maximum()
            if maxv <= minv:
                return groove.left()
            span = maxv - minv
            if span <= 0:
                return groove.left()
            val = max(minv, min(maxv, int(value)))
            pos = ((val - minv) / float(span)) * (groove.width() - 1)
            return int(groove.left() + pos)
        except Exception:
            try: return self.rect().left() + 8
            except: return 8

    def mousePressEvent(self, event):
        if event.button() == Qt.LeftButton:
            self.setCursor(Qt.PointingHandCursor)
            self.setSliderDown(True)
            self._press_pos = event.pos()
            self._has_moved_since_press = False
            pos = event.pos()
            groove_rect = self._get_groove_rect()
            is_in_seek_zone = True 
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
            elif is_in_seek_zone:
                try:
                    val = self._map_pos_to_value(pos.x())
                    self.blockSignals(True)
                    self.setValue(val)
                    self.blockSignals(False)
                    self._dragging_handle = 'playhead'
                    self.sliderMoved.emit(val)
                except Exception: pass
            self.update()

    def mouseMoveEvent(self, event):
        pos = event.pos()
        if not self._has_moved_since_press:
            if (pos - self._press_pos).manhattanLength() > 5:
                self._has_moved_since_press = True
        if self._dragging_music_handle:
            self.setCursor(Qt.PointingHandCursor)
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
                new_start_ms = new_val_ms
                new_start_ms = max(video_trim_start_ms, new_start_ms)
                self.music_start_ms = min(new_start_ms, self.music_end_ms - 100)
            elif self._dragging_music_handle == 'end':
                new_end_ms = new_val_ms
                new_end_ms = min(video_trim_end_ms, new_end_ms)
                self.music_end_ms = max(self.music_start_ms + 100, new_end_ms)
            self.music_trim_changed.emit(self.music_start_ms, self.music_end_ms)
            self.update()
            return
        if self._dragging_handle:
            self.setCursor(Qt.PointingHandCursor)
            val = self._map_pos_to_value(pos.x())
            if self._dragging_handle == 'start':
                self.trimmed_start_ms = min(val, self.trimmed_end_ms - 100)
                if self._show_music and self.music_start_ms >= 0:
                    new_m_start = max(self.trimmed_start_ms, self.music_start_ms)
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
            self.setCursor(Qt.PointingHandCursor)
            if new_hover != self._hovering_handle or new_hover_music != self._hovering_music_handle:
                self._hovering_handle = new_hover
                self._hovering_music_handle = new_hover_music
                self.update()

    def mouseReleaseEvent(self, event):
        active_handle = getattr(self, "_dragging_handle", None)
        self.setSliderDown(False)
        self._dragging_handle = None
        self._dragging_music_handle = None
        self.update()
        if active_handle == 'playhead':
            pos = event.pos()
            val = self._map_pos_to_value(pos.x())
            self.setValue(val)
            self.sliderMoved.emit(val)

    def paintEvent(self, event):
        if getattr(self, '_is_destroying', False) or getattr(self, '_is_painting', False):
            return
        self._is_painting = True
        try:
            p = QPainter(self)
            try:
                p.fillRect(self.rect(), QColor(0, 0, 0, 1))
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
                p.setBrush(QColor(112, 112, 112, 191))
                p.drawRoundedRect(groove_rect, 3, 3)
                try:
                    dur = self._duration_ms if self._duration_ms > 0 else self.maximum()
                    if self._show_trim_overlays and self.trimmed_start_ms >= 0 and self.trimmed_end_ms > 0 and dur > 0:
                        fill_color = QColor("#59B1D5")
                        fill_color.setAlpha(150)
                        p.setBrush(fill_color)
                        kept_left = self._map_value_to_pos(self.trimmed_start_ms)
                        kept_right = self._map_value_to_pos(self.trimmed_end_ms)
                        kept_rect = QRect(int(kept_left), int(groove_rect.y()), int(max(1, kept_right - kept_left)), int(groove_rect.height()))
                        p.drawRect(kept_rect)
                except Exception: pass
                try:
                    if self._show_music and self.music_start_ms >= 0 and self.music_end_ms > self.music_start_ms:
                        music_color = QColor(255, 105, 180, 180)
                        p.setBrush(music_color)
                        p.setPen(Qt.NoPen)
                        m_left = self._map_value_to_pos(self.music_start_ms)
                        m_right = self._map_value_to_pos(self.music_end_ms)
                        music_rect = QRect(int(m_left), int(groove_rect.y() + self.music_v_offset), int(max(1, m_right - m_left)), 4)
                        if music_rect.isValid():
                            p.drawRect(music_rect)
                except Exception: pass
                try:
                    if self.speed_segments:
                        line_height = 20
                        y_top = groove_rect.bottom() + 2
                        for seg in self.speed_segments:
                            start_ms = seg['start']
                            end_ms = seg['end']
                            speed = seg['speed']
                            is_freeze = abs(float(speed)) < 0.001
                            if is_freeze:
                                seg_color = QColor("#9b59b6")
                            elif speed > self.base_speed + 0.001:
                                seg_color = QColor("#2ecc71")
                            elif speed < self.base_speed - 0.001:
                                seg_color = QColor("#e74c3c")
                            else:
                                seg_color = QColor("#95a5a6")
                            seg_color.setAlpha(160 if is_freeze else 100)
                            p.setBrush(seg_color)
                            p.setPen(Qt.NoPen)
                            left = self._map_value_to_pos(start_ms)
                            right = self._map_value_to_pos(end_ms)
                            seg_rect = QRect(int(left), int(y_top), int(max(1, right - left)), int(line_height))
                            p.drawRect(seg_rect)
                            if is_freeze and seg_rect.width() >= 12:
                                try:
                                    icon = self.style().standardIcon(QStyle.SP_DialogHelpButton)
                                    icon_size = min(14, line_height - 4)
                                    icon_rect = QRect(seg_rect.center().x() - icon_size // 2, seg_rect.center().y() - icon_size // 2, icon_size, icon_size)
                                    icon.paint(p, icon_rect)
                                except Exception: pass
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
                                p.drawLine(int(x), int(groove_rect.bottom() + 2), int(x), int(groove_rect.bottom() + 2 + tick_len))
                                time_str = self._fmt(sec * 1000)
                                text_width = fm.horizontalAdvance(time_str)
                                p.setPen(QColor("#FFFFFF" if is_minute else "#AAAAAA"))
                                p.drawText(int(x - text_width // 2), int(groove_rect.bottom() + 22), time_str)
                        else:
                            major_tick_pixels = 120
                            num_major_ticks = max(1, int(round(groove_rect.width() / major_tick_pixels)))
                            for i in range(num_major_ticks + 1):
                                ratio = i / float(num_major_ticks)
                                ms = dur_ms * ratio
                                x = groove_rect.left() + int(ratio * (groove_rect.width() - 1))
                                p.setPen(QColor(180, 180, 180))
                                p.drawLine(int(x), int(groove_rect.bottom() + 1), int(x), int(groove_rect.bottom() + 6))
                                time_str = self._fmt(int(ms))
                                text_width = fm.horizontalAdvance(time_str)
                                p.drawText(int(x - text_width // 2), int(groove_rect.bottom() + 18), time_str)
                except Exception: pass
                try:
                    for handle_type in ['start', 'end']:
                        handle_rect = self._get_handle_rect(handle_type)
                        if not handle_rect.isValid(): continue
                        color = QColor("#318181")
                        color.setAlpha(191)
                        if self._hovering_handle == handle_type or self._dragging_handle == handle_type:
                            color = QColor(230, 126, 34, 191)
                        p.setPen(Qt.NoPen)
                        p.setBrush(color)
                        p.drawRoundedRect(handle_rect, 6, 6)
                except Exception: pass
                try:
                    playhead_rect = self._get_playhead_rect()
                    if playhead_rect.isValid():
                        is_active = (self._dragging_handle == 'playhead')
                        size = 45 if is_active else 35
                        cx = playhead_rect.center().x()
                        cy = playhead_rect.center().y()
                        p.setPen(QPen(QColor(150, 0, 0, 200), 1.5))
                        p.setBrush(QColor(231, 76, 60, 255))
                        p.drawEllipse(QPoint(int(cx), int(cy)), int(size // 2), int(size // 2))
                        p.setBrush(Qt.white)
                        p.setPen(Qt.NoPen)
                        p.drawEllipse(QPoint(int(cx), int(cy)), 2, 2)
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
                try:
                    if self.thumbnail_pos_ms >= 0:
                        tx = self._map_value_to_pos(self.thumbnail_pos_ms)
                        ty = groove_rect.center().y()
                        tw, th = 33, 24
                        t_rect = QRect(int(tx - tw // 2), int(ty - th // 2), int(tw), int(th))
                        p.setPen(QPen(Qt.black, 1.5))
                        p.setBrush(QColor("#7DD3FC"))
                        p.drawRoundedRect(t_rect, 3, 3)
                        p.setBrush(Qt.black)
                        p.drawEllipse(t_rect.center(), 6, 6)
                        p.setBrush(QColor("#7DD3FC"))
                        p.drawEllipse(t_rect.center(), 3, 3)
                        lens_rect = QRect(int(t_rect.right() - 8), int(t_rect.top() + 3), 5, 4)
                        p.setBrush(Qt.white)
                        p.drawRect(lens_rect)
                except Exception: pass
            except Exception: pass
            finally:
                if p.isActive(): p.end()
                self._is_painting = False
        except Exception:
            self._is_painting = False
