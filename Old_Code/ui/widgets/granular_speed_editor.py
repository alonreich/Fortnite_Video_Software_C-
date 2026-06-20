try:
    import mpv
except Exception:
    mpv = None

import sys
import os
import threading
import time as import_time
from PyQt5.QtWidgets import (QDialog, QVBoxLayout, QHBoxLayout, QPushButton,
                             QLabel, QDoubleSpinBox, QWidget, QStyle, QLineEdit,
                             QTextEdit, QPlainTextEdit, QAbstractSpinBox, QComboBox,
                             QMessageBox, QApplication, QSpinBox, QGridLayout, QFrame)

from PyQt5.QtCore import Qt, QTimer, pyqtSignal, QRect, QPoint, QSize, QEvent
from PyQt5.QtGui import QPainter, QColor, QBrush, QPen, QLinearGradient, QCursor, QIcon, QPixmap, QFont
from ui.widgets.trimmed_slider import TrimmedSlider
from system.utils import MPVSafetyManager
from ui.styles import UIStyles
SEGMENT_GAP_MS = 0
AUTO_START_AFTER_PREVIOUS_MS = 1000
MIN_SEGMENT_MS = 10
PENDING_PREVIEW_MS = 1000
SEEK_THROTTLE_MS = 50
SEEK_DUPLICATE_WINDOW_MS = 80
FREEZE_MIN_SEC = 0.5
FREEZE_MAX_SEC = 30.0
FREEZE_STEP_SEC = 0.5
FREEZE_DEFAULT_SEC = 1.0

class ClickableLabel(QLabel):
    clicked_signal = pyqtSignal()

    def __init__(self, text="", parent=None):
        super().__init__(text, parent)
        self.clicked_callback = None

    def mousePressEvent(self, ev):
        if ev.button() == Qt.LeftButton:
            if self.clicked_callback: self.clicked_callback()
            self.clicked_signal.emit()

class GranularTimelineSlider(TrimmedSlider):
    def __init__(self, parent=None):
        super().__init__(Qt.Horizontal, parent)
        self.segments = []
        self.active_segment_index = -1
        self.m_ants_timer = QTimer(self)
        self.m_ants_timer.setInterval(100)
        self.m_ants_timer.timeout.connect(self._update_ants)
        self.m_ants_offset = 0
        self.pending_start = -1
        self.pending_end = -1
        self.pending_speed = 1.1
        self.setMouseTracking(True)
        self._hovering_handle = None
        self.view_start_ms = None
        self.view_end_ms = None
        self._blocked_edge_ms = None
        self._blocked_flash_timer = QTimer(self)
        self._blocked_flash_timer.setSingleShot(True)
        self._blocked_flash_timer.timeout.connect(self._clear_blocked_flash)

    def _visible_range(self):
        min_v, max_v = int(self.minimum()), int(self.maximum())
        if self.view_start_ms is None or self.view_end_ms is None:
            return min_v, max_v
        start = max(min_v, min(max_v, int(self.view_start_ms)))
        end = max(start + 1, min(max_v, int(self.view_end_ms)))
        return start, end

    def is_zoomed(self):
        return self.view_start_ms is not None and self.view_end_ms is not None

    def set_view_range(self, start, end):
        min_v, max_v = int(self.minimum()), int(self.maximum())
        full_span = max(1, max_v - min_v)
        span = max(1000, int(end) - int(start))
        if span >= full_span:
            self.fit_view()
            return
        span = min(full_span, span)
        center = (int(start) + int(end)) // 2
        new_start = max(min_v, min(max_v - span, center - span // 2))
        self.view_start_ms = int(new_start)
        self.view_end_ms = int(new_start + span)
        self.update()

    def fit_view(self):
        self.view_start_ms = None
        self.view_end_ms = None
        self.update()

    def ensure_value_visible(self, value):
        if not self.is_zoomed():
            return
        start, end = self._visible_range()
        value = max(self.minimum(), min(self.maximum(), int(value)))
        if start <= value <= end:
            return
        span = end - start
        self.set_view_range(value - span // 2, value + span // 2)

    def ensure_range_visible(self, start, end):
        if not self.is_zoomed():
            return
        view_start, view_end = self._visible_range()
        start, end = int(start), int(end)
        if view_start <= start and end <= view_end:
            return
        current_span = view_end - view_start
        target_span = max(current_span, (end - start) + 500)
        center = (start + end) // 2
        self.set_view_range(center - target_span // 2, center + target_span // 2)

    def _map_pos_to_value(self, px):
        try:
            groove = self._get_groove_rect()
            if groove.width() <= 1:
                return self._visible_range()[0]
            pos = px - groove.left()
            ratio = pos / float(groove.width() - 1)
            min_v, max_v = self._visible_range()
            val = min_v + ratio * (max_v - min_v)
            return int(max(min_v, min(max_v, val)))
        except Exception:
            return self.minimum()

    def _map_value_to_pos(self, value):
        try:
            groove = self._get_groove_rect()
            if not groove.isValid() or groove.width() <= 1:
                return groove.left() if groove.isValid() else 8
            min_v, max_v = self._visible_range()
            if max_v <= min_v:
                return groove.left()
            val = max(min_v, min(max_v, int(value)))
            pos = ((val - min_v) / float(max_v - min_v)) * (groove.width() - 1)
            return int(groove.left() + pos)
        except Exception:
            return 8

    def flash_blocked_edge(self, value_ms):
        self._blocked_edge_ms = max(self.minimum(), min(self.maximum(), int(value_ms)))
        self._blocked_flash_timer.start(650)
        self.update()

    def _clear_blocked_flash(self):
        self._blocked_edge_ms = None
        self.update()

    def _update_ants(self):
        self.m_ants_offset = (self.m_ants_offset + 2) % 12
        self.update()

    def start_ants(self):
        if not self.m_ants_timer.isActive():
            self.m_ants_timer.start()

    def stop_ants(self):
        self.m_ants_timer.stop()
        self.update()

    def set_segments(self, segments):
        self.segments = segments
        self.update()

    def set_pending_segment(self, start, end, speed):
        self.pending_start = start
        self.pending_end = end
        self.pending_speed = speed
        self.update()

    def mousePressEvent(self, e):
        if e.button() == Qt.LeftButton:
            is_over_handle = self._get_handle_rect('start').contains(e.pos()) or \
                             self._get_handle_rect('end').contains(e.pos()) or \
                             self._get_playhead_rect().contains(e.pos())
            if not is_over_handle:
                click_x = e.pos().x()
                found_seg = -1
                for i, seg in enumerate(self.segments):
                    if abs(seg.get('speed', 1.0)) < 0.001:
                        if self._get_freeze_marker_rect(seg).contains(e.pos()):
                            found_seg = i
                            break
                        continue
                    s_pos = self._map_value_to_pos(seg['start'])
                    e_pos = self._map_value_to_pos(seg['end'])
                    if s_pos > e_pos: s_pos, e_pos = e_pos, s_pos
                    hit_margin = 4
                    if s_pos - hit_margin <= click_x <= e_pos + hit_margin:
                        found_seg = i
                        break
                if found_seg != -1:
                    self.active_segment_index = found_seg
                    if hasattr(self.parent(), 'edit_segment'):
                        self.parent().edit_segment(found_seg)
                        self.parent().seek_video(self.segments[found_seg]['start'], exact=True)
                    e.accept()
                    return
                else:
                    self.active_segment_index = -1
                    if hasattr(self.parent(), 'on_selection_cleared'):
                        self.parent().on_selection_cleared()
        super().mousePressEvent(e)

    def mouseMoveEvent(self, e):
        if self._dragging_handle == 'start':
            requested = self._map_pos_to_value(e.pos().x())
            val = requested
            parent = self.parent()
            if hasattr(parent, "_clamp_selection_start"):
                val = parent._clamp_selection_start(val, self.trimmed_end_ms, self.active_segment_index)
                if val != requested:
                    self.flash_blocked_edge(val)
            else:
                low = self.minimum()
                for i, seg in enumerate(self.segments):
                    if i != self.active_segment_index and seg['end'] <= self.trimmed_start_ms:
                        low = max(low, seg['end'] + SEGMENT_GAP_MS)
                val = max(low, min(val, self.trimmed_end_ms - MIN_SEGMENT_MS))
            if val != self.trimmed_start_ms:
                self.trimmed_start_ms = val
                self.trim_times_changed.emit(self.trimmed_start_ms, self.trimmed_end_ms)
                self.update()
            return
        elif self._dragging_handle == 'end':
            requested = self._map_pos_to_value(e.pos().x())
            val = requested
            parent = self.parent()
            if hasattr(parent, "_clamp_selection_end"):
                val = parent._clamp_selection_end(val, self.trimmed_start_ms, self.active_segment_index)
                if val != requested:
                    self.flash_blocked_edge(val)
            else:
                high = self.maximum()
                for i, seg in enumerate(self.segments):
                    if i != self.active_segment_index and seg['start'] >= self.trimmed_end_ms:
                        high = min(high, seg['start'] - SEGMENT_GAP_MS)
                val = min(high, max(val, self.trimmed_start_ms + MIN_SEGMENT_MS))
            if val != self.trimmed_end_ms:
                self.trimmed_end_ms = val
                self.trim_times_changed.emit(self.trimmed_start_ms, self.trimmed_end_ms)
                self.update()
            return
        super().mouseMoveEvent(e)
        if not self._dragging_handle and not self._hovering_handle:
            for seg in self.segments:
                if abs(seg.get('speed', 1.0)) < 0.001 and self._get_freeze_marker_rect(seg).contains(e.pos()):
                    self.setCursor(QCursor(Qt.PointingHandCursor))
                    return
            val = self._map_pos_to_value(e.pos().x())
            for seg in self.segments:
                if abs(seg.get('speed', 1.0)) < 0.001:
                    continue
                if seg['start'] <= val <= seg['end']:
                    self.setCursor(QCursor(Qt.PointingHandCursor))
                    break

    def _get_groove_rect(self):
        h = 10
        top = (self.height() - h) // 2
        return QRect(8, top, max(1, self.width() - 16), h)

    def paintEvent(self, event):
        p = QPainter(self)
        from developer_tools.config import UI_COLORS
        try:
            p.setRenderHint(QPainter.Antialiasing)
            groove_rect = self._get_groove_rect()
            p.setPen(QPen(QColor("#1f3545"), 1))
            p.setBrush(QColor("#142d37"))
            p.drawRoundedRect(groove_rect, 4, 4)

            view_start, view_end = self._visible_range()
            for i, seg in enumerate(self.segments):
                if abs(seg.get('speed', 1.0)) < 0.001:
                    continue
                is_active = (i == self.active_segment_index)
                if seg['end'] < view_start or seg['start'] > view_end:
                    continue
                self._draw_segment_on_groove(p, groove_rect, seg['start'], seg['end'], seg['speed'], is_active=is_active)

            for i, seg in enumerate(self.segments):
                if abs(seg.get('speed', 1.0)) >= 0.001:
                    continue
                if seg['start'] < view_start or seg['start'] > view_end:
                    continue
                self._draw_freeze_marker(p, seg, i == self.active_segment_index)

            if self.pending_start != -1 and self.pending_end != -1:
                self._draw_segment_on_groove(p, groove_rect, self.pending_start, self.pending_end, self.pending_speed, is_pending=True)

            min_v, max_v = self._visible_range()
            fm = p.fontMetrics()
            if max_v > min_v:
                step = 10000 if (max_v - min_v) > 60000 else 5000 if (max_v - min_v) > 30000 else 1000
                for ms in range(int(min_v // step * step), int(max_v + 1), step):
                    if ms < min_v: continue
                    x = self._map_value_to_pos(ms)
                    is_obscured = False
                    for seg in self.segments:
                        if abs(seg.get('speed', 1.0)) < 0.001:
                            continue
                        if seg['start'] <= ms <= seg['end']: is_obscured = True; break
                    if not is_obscured:
                        p.setPen(QColor("#7f8c8d"))
                        p.drawLine(x, groove_rect.bottom() + 2, x, groove_rect.bottom() + 6)
                        p.setFont(QFont("Segoe UI", 8))
                        p.drawText(x - fm.horizontalAdvance(self._fmt(int(ms))) // 2, groove_rect.bottom() + 18, self._fmt(int(ms)))

            try:
                playhead_rect = self._get_playhead_rect()
                if playhead_rect and playhead_rect.isValid():
                    knob_w, knob_h = 12, 36
                    cx = playhead_rect.center().x()
                    cy = groove_rect.center().y()
                    knob_rect = QRect(cx - knob_w // 2, cy - knob_h // 2, knob_w, knob_h)

                    g = QLinearGradient(knob_rect.left(), knob_rect.top(), knob_rect.left(), knob_rect.bottom())
                    c1, c2 = QColor("#3a8db0"), QColor("#1a5276")
                    if self._hovering_handle == 'playhead' or self._dragging_handle == 'playhead':
                        p.setPen(QPen(QColor(UI_COLORS.BORDER_ACCENT), 2))
                    else:
                        p.setPen(QPen(QColor("#111111"), 1))

                    g.setColorAt(0.0, c1); g.setColorAt(0.5, c2); g.setColorAt(1.0, c1)
                    p.setBrush(QBrush(g))
                    p.drawRoundedRect(knob_rect, 3, 3)
            except Exception: pass

            for handle_type in ['start', 'end']:
                handle_rect = self._get_handle_rect(handle_type)
                if not handle_rect.isValid(): continue
                color = QColor("#e67e22") if self._hovering_handle == handle_type or self._dragging_handle == handle_type else QColor("#d35400")
                p.setPen(QPen(Qt.black, 1)); p.setBrush(color); p.drawRoundedRect(handle_rect, 3, 3)
            if self._blocked_edge_ms is not None:
                x = self._map_value_to_pos(self._blocked_edge_ms)
                p.setPen(QPen(QColor("#ff4d4d"), 3))
                p.drawLine(x, groove_rect.top() - 18, x, groove_rect.bottom() + 24)
        finally:
            if p.isActive(): p.end()

    def _get_freeze_marker_rect(self, seg):
        try:
            tx = self._map_value_to_pos(int(seg.get('start', 0)))
            ty = self._get_groove_rect().center().y()
            tw, th = 33, 24
            return QRect(int(tx - tw // 2), int(ty - th // 2), int(tw), int(th))
        except Exception:
            return QRect()

    def _draw_freeze_marker(self, p, seg, is_active=False):
        t_rect = self._get_freeze_marker_rect(seg)
        if not t_rect.isValid():
            return
        border = QColor("#7DD3FC") if is_active else QColor(Qt.black)
        p.setPen(QPen(border, 2 if is_active else 1.5))
        p.setBrush(QColor("#9b59b6"))
        p.drawRoundedRect(t_rect, 3, 3)
        p.setBrush(Qt.black)
        p.drawEllipse(t_rect.center(), 6, 6)
        p.setBrush(QColor("#9b59b6"))
        p.drawEllipse(t_rect.center(), 3, 3)
        lens_rect = QRect(int(t_rect.right() - 8), int(t_rect.top() + 3), 5, 4)
        p.setBrush(Qt.white)
        p.drawRect(lens_rect)

    def _draw_segment_on_groove(self, p, groove_rect, start_ms, end_ms, speed, is_pending=False, is_active=False):
        if end_ms <= start_ms: return
        is_freeze = abs(speed) < 0.001
        if is_freeze: seg_color = QColor("#9b59b6")
        else: seg_color = QColor("#2ecc71") if speed > 1.101 else QColor("#e74c3c") if speed < 1.099 else QColor("#7f8c8d")

        alpha = 190 if is_active else 175 if is_pending else 140
        seg_color.setAlpha(alpha); p.setBrush(seg_color)
        p.setPen(QPen(QColor("#7DD3FC"), 2) if is_active else QPen(Qt.black, 0.5))

        s_pos, e_pos = self._map_value_to_pos(start_ms), self._map_value_to_pos(end_ms)
        if s_pos > e_pos: s_pos, e_pos = e_pos, s_pos

        h, y = 18, groove_rect.center().y() - 9
        seg_rect = QRect(s_pos, y, max(1, e_pos - s_pos), h)
        p.drawRoundedRect(seg_rect, 2, 2)

        if is_pending:
            p.setBrush(Qt.NoBrush)
            pen = QPen(QColor("white"), 1.5, Qt.DashLine)
            if self.m_ants_timer.isActive():
                pen.setDashOffset(self.m_ants_offset)
            p.setPen(pen)
            p.drawRoundedRect(seg_rect.adjusted(-1,-1,1,1), 2, 2)

class GranularSpeedEditor(QDialog):
    BUTTON_BLUE = UIStyles.BUTTON_WIZARD_BLUE
    BUTTON_GREEN = UIStyles.BUTTON_WIZARD_GREEN
    BUTTON_CANCEL = UIStyles.BUTTON_CANCEL
    BUTTON_DANGER = UIStyles.BUTTON_DANGER
    BUTTON_STANDARD = UIStyles.BUTTON_STANDARD
    BUTTON_PRESET = UIStyles.BUTTON_WIZARD_BLUE + "QPushButton { font-size: 9px; padding: 1px 3px; border-radius: 5px; min-width: 0px; } QPushButton:pressed:!disabled { padding: 2px 4px 0px 2px; }"
    status_update_signal = pyqtSignal(str)
    @property
    def freeze_images(self):
        return [seg for seg in self.speed_segments if abs(seg['speed']) < 0.001]

    def _safe_mpv_set(self, prop, value):
        if not getattr(self, "player", None): return False
        lock = self._mpv_lock
        try:
            if not lock.acquire(timeout=self._mpv_lock_timeout): return False
            try:
                if prop == "wid": self.player.wid = value
                elif prop == "pause": self.player.pause = value
                elif prop == "speed": self.player.speed = value
                elif prop == "volume": self.player.volume = value
                elif prop == "mute": self.player.mute = value
                else: self.player.set_property(prop, value)
                return True
            finally: lock.release()
        except: return False

    def _safe_mpv_get(self, prop, default=None):
        if not getattr(self, "player", None): return default
        lock = self._mpv_lock
        try:
            if not lock.acquire(timeout=self._mpv_lock_timeout):
                if prop == 'time-pos':
                    last_pos = getattr(self, "last_position_ms", 0)
                    return (last_pos + self.abs_trim_start) / 1000.0
                return default
            try: return getattr(self.player, prop, default)
            finally: lock.release()
        except: return default

    def _safe_mpv_command(self, *args):
        if not getattr(self, "player", None): return False
        lock = self._mpv_lock
        try:
            if not lock.acquire(timeout=self._mpv_lock_timeout): return False
            try:
                if not args: return False
                return MPVSafetyManager.safe_mpv_command(self.player, args[0], *args[1:], max_attempts=1)
            finally: lock.release()
        except: return False

    def _init_state(self):
        self.selection_modified = False
        self.list_modified = False
        self._suppress_speed_change_flag = False

    def __init__(self, input_file_path, parent=None, initial_segments=None, base_speed=1.1, start_time_ms=0, volume=100):
        super().__init__(None)
        self._mpv_lock = threading.RLock()
        self._init_state()
        self.setWindowTitle("Granular Speed Editor")
        self.setWindowFlags(Qt.Window | Qt.WindowMinimizeButtonHint | Qt.WindowMaximizeButtonHint | Qt.WindowCloseButtonHint)
        self.input_file_path = input_file_path
        self.parent_app = parent
        self.base_speed = max(0.1, min(4.0, float(base_speed)))
        self.abs_trim_start = int(getattr(parent, "trim_start_ms", 0) or 0)
        self.abs_trim_end = int(getattr(parent, "trim_end_ms", 0) or 0)
        clip_dur = max(100, self.abs_trim_end - self.abs_trim_start)
        self.speed_segments = []
        if initial_segments:
            for seg in initial_segments:
                rel_s = seg['start'] - self.abs_trim_start
                rel_e = seg['end'] - self.abs_trim_start
                if rel_e > 0 and rel_s < clip_dur:
                    self.speed_segments.append({
                        'start': max(0, rel_s),
                        'end': min(clip_dur, rel_e),
                        'speed': seg['speed']
                    })
        self.speed_segments.sort(key=lambda item: (item['start'], item['end']))
        self.start_time_ms = max(0, int(start_time_ms) - self.abs_trim_start)
        self.last_position_ms = self.start_time_ms
        self.volume = volume
        self.duration = clip_dur
        self.player = None
        self.timer = QTimer(self)
        self.timer.setInterval(40)
        self._mpv_lock_timeout = 0.20
        self.timer.timeout.connect(self.update_ui)
        self._seek_timer = QTimer(self)
        self._seek_timer.setSingleShot(True)
        self._seek_timer.timeout.connect(self._flush_pending_seek)
        self._pending_seek = None
        self._last_seek_command_ts = 0.0
        self._last_seek_target_ms = None
        self._last_seek_exact = False
        self.is_playing = False
        self._last_rate_update = 0
        self._updating_ui = False
        self._start_manually_set = False
        self._pending_segment_active = False
        self._in_freeze_segment = False
        self._freeze_seg_idx = -1
        self._last_preview_bind_ts = 0
        self.restore_geometry()
        self.init_ui()
        if sys.platform == 'win32': os.environ["LC_NUMERIC"] = "C"
        QTimer.singleShot(100, self.setup_player)

    def restore_geometry(self):
        def_w, def_h = 1500, 800
        if self.parent_app and hasattr(self.parent_app, 'config_manager'):
            geom = self.parent_app.config_manager.config.get('granular_editor_geometry')
            if geom and isinstance(geom, dict):
                w, h = geom.get('w', def_w), geom.get('h', def_h); x, y = geom.get('x', -1), geom.get('y', -1)
                if x == -1 or y == -1: self.resize(w, h); self._center_on_screen()
                else:
                    screen = QApplication.screenAt(QPoint(x, y)) or QApplication.primaryScreen(); avail = screen.availableGeometry()
                    w, h = min(w, avail.width()), min(h, avail.height()); x, y = max(avail.x(), min(x, avail.right() - w)), max(avail.y(), min(y, avail.bottom() - h))
                    self.setGeometry(x, y, w, h)
            else: self.resize(def_w, def_h); self._center_on_screen()
        else: self.resize(def_w, def_h); self._center_on_screen()

    def _center_on_screen(self):
        screen_geo = QApplication.primaryScreen().availableGeometry(); self.move(screen_geo.x() + (screen_geo.width() - self.width()) // 2, screen_geo.y() + (screen_geo.height() - self.height()) // 2)

    def save_geometry(self):
        if self.parent_app and hasattr(self.parent_app, 'config_manager'):
            cfg = dict(getattr(self.parent_app.config_manager, 'config', {}) or {}); cfg['granular_editor_geometry'] = {'x': self.geometry().x(), 'y': self.geometry().y(), 'w': self.geometry().width(), 'h': self.geometry().height()}
            try: self.parent_app.config_manager.save_config(cfg)
            except Exception: pass

    def _update_clear_all_btn_state(self):
        has_segments = len(self.speed_segments) > 0
        self.clear_all_btn.setEnabled(has_segments)
        self._set_button_class(self.clear_all_btn, 'danger' if has_segments else 'primary')
        self._update_segment_counter()
        self._update_apply_button_label()

    def _update_segment_counter(self):
        count = sum(1 for seg in self.speed_segments if abs(seg['speed'] - self.base_speed) > 0.01)
        self.setWindowTitle(f"Granular Speed Editor    CURRENT DIFFERENT SPEED SEGMENTS: {count}")

    def _set_default_pending_range(self):
        if not hasattr(self, "timeline"):
            return
        self.timeline.active_segment_index = -1
        self._start_manually_set = False
        self._pending_segment_active = True
        self.timeline.set_trim_times(0, int(self.duration))
        self.timeline.set_pending_segment(0, int(self.duration), self.speed_spin.value())
        self.timeline.start_ants()
        self._update_selection_readout()
        self._update_apply_button_label()

    def _apply_speed_preset(self, value):
        self.speed_spin.setValue(float(value))
        self._update_preset_button_styles()

    def _sorted_segments(self, exclude_idx=-1):
        items = []
        for i, seg in enumerate(self.speed_segments):
            if i == exclude_idx:
                continue
            try:
                items.append((i, int(seg['start']), int(seg['end'])))
            except Exception:
                continue
        return sorted(items, key=lambda item: item[1])

    def _gap_for_point(self, point, exclude_idx=-1):
        point = max(0, min(int(self.duration), int(point)))
        low, high = 0, int(self.duration)
        for _i, start, end in self._sorted_segments(exclude_idx):
            if start <= point < end:
                return None
            if end <= point:
                low = max(low, end + SEGMENT_GAP_MS)
            elif start > point:
                high = min(high, start - SEGMENT_GAP_MS)
                break
        return low, high

    def _clamp_selection_start(self, requested_start, current_end, exclude_idx=-1):
        current_end = max(0, min(int(self.duration), int(current_end)))
        low = 0
        for _i, start, end in self._sorted_segments(exclude_idx):
            if end <= current_end:
                low = max(low, end + SEGMENT_GAP_MS)
            elif start < current_end:
                low = max(low, end + SEGMENT_GAP_MS)
            else:
                break
        high = max(low, current_end - MIN_SEGMENT_MS)
        return max(low, min(int(requested_start), high))

    def _clamp_selection_end(self, requested_end, current_start, exclude_idx=-1):
        current_start = max(0, min(int(self.duration), int(current_start)))
        high = int(self.duration)
        for _i, start, end in self._sorted_segments(exclude_idx):
            if end <= current_start:
                continue
            if start >= current_start:
                high = min(high, start - SEGMENT_GAP_MS)
                break
        low = min(high, current_start + MIN_SEGMENT_MS)
        return min(high, max(int(requested_end), low))

    def _auto_range_ending_at(self, end):
        end = max(0, min(int(self.duration), int(end)))
        for _i, start, seg_end in self._sorted_segments():
            if start < end <= seg_end:
                end = start
                break
        gap = self._gap_for_point(max(0, end - 1))
        if gap is None:
            low = 0
        else:
            low, _high = gap
        start = low + AUTO_START_AFTER_PREVIOUS_MS
        if start >= end:
            start = low
        end = self._clamp_selection_end(end, start)
        return start, end

    def _make_legend_chip(self, label, color, dashed=False, outline=False):
        chip = QFrame()
        border_style = "dashed" if dashed else "solid"
        chip.setStyleSheet("QFrame { background: transparent; border: none; } QLabel { background: transparent; border: none; }")
        layout = QHBoxLayout(chip)
        layout.setContentsMargins(0, 0, 0, 0)
        layout.setSpacing(4)
        swatch = QLabel()
        swatch.setFixedSize(16, 8)
        swatch_border = "#7DD3FC" if outline else color
        swatch.setStyleSheet(f"background: {color}; border: 1px {border_style} {swatch_border}; border-radius: 2px; padding: 0px;")
        text = QLabel(label)
        text.setStyleSheet("color: #bdc3c7; font-size: 9px; font-weight: bold; padding: 0px;")
        layout.addWidget(swatch)
        layout.addWidget(text)
        return chip

    def _make_readout_label(self, text):
        label = QLabel(text)
        label.setMinimumWidth(100)
        label.setAlignment(Qt.AlignCenter)
        label.setStyleSheet("color: #ecf0f1; font-size: 10px; font-weight: bold; background: #142d37; border: 1px solid #1f3545; border-radius: 4px; padding: 3px 6px;")
        return label

    def _fmt_duration(self, ms):
        ms = max(0, int(round(ms)))
        if ms < 1000:
            return f"{ms}ms"
        return f"{ms / 1000.0:.2f}s"

    def _selection_values(self):
        idx = getattr(self.timeline, "active_segment_index", -1) if hasattr(self, "timeline") else -1
        if 0 <= idx < len(self.speed_segments):
            seg = self.speed_segments[idx]
            return int(seg["start"]), int(seg["end"]), float(seg["speed"])
        if not hasattr(self, "timeline"):
            return None
        if not (getattr(self, "_pending_segment_active", False) or getattr(self, "_start_manually_set", False)):
            return None
        s = int(getattr(self.timeline, "trimmed_start_ms", -1))
        e = int(getattr(self.timeline, "trimmed_end_ms", -1))
        if s < 0 or e <= s:
            return None
        return s, e, float(self.speed_spin.value())

    def _update_selection_readout(self):
        labels = getattr(self, "readout_labels", None)
        if not labels:
            return
        values = self._selection_values()
        if not values:
            labels["start"].setText("START --")
            labels["end"].setText("END --")
            labels["duration"].setText("DUR --")
            labels["speed"].setText("SPEED --")
            labels["output"].setText("OUT --")
            return
        start, end, speed = values
        duration_ms = max(0, end - start)
        output_ms = duration_ms if abs(speed) < 0.001 else duration_ms / max(0.001, speed)
        speed_text = "FREEZE" if abs(speed) < 0.001 else f"{speed:.1f}x"
        labels["start"].setText(f"START {self._fmt(start)}")
        labels["end"].setText(f"END {self._fmt(end)}")
        labels["duration"].setText(f"DUR {self._fmt_duration(duration_ms)}")
        labels["speed"].setText(f"SPEED {speed_text}")
        labels["output"].setText(f"OUT {self._fmt_duration(output_ms)}")

    def _segment_counts_for_apply(self):
        freeze_count = sum(1 for seg in self.speed_segments if abs(float(seg.get("speed", 1.0))) < 0.001)
        speed_count = max(0, len(self.speed_segments) - freeze_count)
        values = self._selection_values()
        active_idx = getattr(getattr(self, "timeline", None), "active_segment_index", -1)
        if active_idx == -1 and values:
            _s, _e, speed = values
            if getattr(self, "_start_manually_set", False) or getattr(self, "selection_modified", False) or abs(speed - self.base_speed) > 0.01:
                speed_count += 1
        return freeze_count, speed_count

    def _update_apply_button_label(self):
        if not hasattr(self, "save_btn"):
            return
        freeze_count, speed_count = self._segment_counts_for_apply()
        parts = []
        if freeze_count:
            parts.append(f"{freeze_count} FREEZE" + ("" if freeze_count == 1 else "S"))
        if speed_count:
            parts.append(f"{speed_count} SPEED" + ("" if speed_count == 1 else "S"))
        self.save_btn.setText("APPLY " + " + ".join(parts) if parts else "APPLY")

    def _update_preset_button_styles(self):
        current = float(self.speed_spin.value()) if hasattr(self, "speed_spin") else self.base_speed
        for btn, preset_value in getattr(self, "speed_preset_buttons", []):
            self._set_button_class(btn, 'success' if abs(float(preset_value) - current) < 0.01 else 'primary')

    def _update_zoom_button_state(self):
        if not hasattr(self, "timeline"):
            return
        zoomed = self.timeline.is_zoomed()
        if hasattr(self, "zoom_fit_btn"):
            self.zoom_fit_btn.setEnabled(zoomed)

    def _zoom_timeline(self, factor):
        if not hasattr(self, "timeline"):
            return
        if not self.timeline.is_zoomed():
            start, end = 0, int(self.duration)
        else:
            start, end = self.timeline._visible_range()
        center = int(getattr(self, "last_position_ms", self.timeline.value()) or self.timeline.value())
        if not (start <= center <= end):
            center = (start + end) // 2
        new_span = max(1000, int((end - start) * float(factor)))
        if new_span >= int(self.duration):
            self.timeline.fit_view()
        else:
            self.timeline.set_view_range(center - new_span // 2, center + new_span // 2)
        self._update_zoom_button_state()

    def _timeline_zoom_wheel_zone_contains(self, point):
        try:
            video = getattr(self, "video_frame", None)
            if video is None or not video.isVisible():
                return False
            x = int(point.x())
            y = int(point.y())
            if x < 0 or x > self.width():
                return False
            video_top = video.mapTo(self, QPoint(0, 0)).y()
            zone_top = video_top + max(1, video.height() // 2)
            control_tops = []
            for attr in ("freeze_group", "trim_group"):
                widget = getattr(self, attr, None)
                if widget is not None and widget.isVisible():
                    control_tops.append(widget.mapTo(self, QPoint(0, 0)).y())
            if not control_tops:
                return False
            zone_bottom = min(control_tops)
            return zone_top <= y < zone_bottom
        except Exception:
            return False

    def _handle_timeline_zoom_wheel(self, event):
        try:
            global_pos = event.globalPos() if hasattr(event, "globalPos") else None
            local_pos = self.mapFromGlobal(global_pos) if global_pos is not None else event.pos()
            if not self._timeline_zoom_wheel_zone_contains(local_pos):
                return False
            delta = event.angleDelta().y()
            if delta == 0:
                delta = event.pixelDelta().y()
            if delta == 0:
                return False
            steps = max(1.0, abs(float(delta)) / 120.0)
            factor = (0.8 ** steps) if delta > 0 else (1.25 ** steps)
            self._zoom_timeline(factor)
            event.accept()
            return True
        except Exception:
            return False

    def _install_timeline_zoom_wheel_filters(self):
        widgets = [
            getattr(self, "video_frame", None),
            getattr(self, "timeline", None),
            getattr(self, "video_legend_overlay", None),
            getattr(self, "video_zoom_overlay", None),
            getattr(self, "zoom_out_btn", None),
            getattr(self, "zoom_fit_btn", None),
            getattr(self, "zoom_in_btn", None),
        ]
        widgets.extend(list(getattr(self, "readout_labels", {}).values()))
        for widget in widgets:
            if widget is None:
                continue
            try:
                widget.installEventFilter(self)
            except Exception:
                pass

    def _fit_timeline(self):
        if hasattr(self, "timeline"):
            self.timeline.fit_view()
        self._update_zoom_button_state()

    def _set_button_class(self, button, class_name):
        if not button:
            return
        button.setProperty('class', class_name)
        button.setStyleSheet("")
        try:
            button.style().unpolish(button)
            button.style().polish(button)
        except Exception:
            pass
        button.update()

    def _build_video_overlays(self):
        from developer_tools.config import UI_LAYOUT
        self.video_legend_overlay = QFrame(self)
        self.video_legend_overlay.setObjectName("granularLegendOverlay")
        self.video_legend_overlay.setStyleSheet(
            "QFrame#granularLegendOverlay { background-color: rgba(20, 45, 55, 190); "
            "border: 1px solid #1f3545; border-radius: 5px; }"
        )
        legend_l = QVBoxLayout(self.video_legend_overlay)
        legend_l.setContentsMargins(7, 7, 7, 7)
        legend_l.setSpacing(6)
        for chip in (
            self._make_legend_chip("FASTER", "#2ecc71"),
            self._make_legend_chip("SLOWER", "#e74c3c"),
            self._make_legend_chip("FREEZE", "#9b59b6"),
            self._make_legend_chip("PENDING", "#34495e", dashed=True),
            self._make_legend_chip("ACTIVE", "#34495e", outline=True),
        ):
            legend_l.addWidget(chip)

        self.video_zoom_overlay = QFrame(self)
        self.video_zoom_overlay.setObjectName("granularZoomOverlay")
        self.video_zoom_overlay.setStyleSheet(
            "QFrame#granularZoomOverlay { background-color: rgba(20, 45, 55, 190); "
            "border: 1px solid #1f3545; border-radius: 5px; }"
        )
        zoom_l = QHBoxLayout(self.video_zoom_overlay)
        zoom_l.setContentsMargins(5, 4, 5, 4)
        zoom_l.setSpacing(4)
        self.zoom_out_btn = QPushButton("-")
        self.zoom_fit_btn = QPushButton("FIT")
        self.zoom_in_btn = QPushButton("+")
        for btn, tip in (
            (self.zoom_out_btn, "Zoom out on the timeline"),
            (self.zoom_fit_btn, "Fit the full clip in the timeline"),
            (self.zoom_in_btn, "Zoom in around the current playhead"),
        ):
            btn.setFixedSize(34 if btn is not self.zoom_fit_btn else 42, UI_LAYOUT.BUTTON_HEIGHT)
            btn.setCursor(Qt.PointingHandCursor)
            btn.setFocusPolicy(Qt.NoFocus)
            btn.setToolTip(tip)
            self._set_button_class(btn, 'primary')
            zoom_l.addWidget(btn)
        self.zoom_out_btn.clicked.connect(lambda: self._zoom_timeline(2.0))
        self.zoom_fit_btn.clicked.connect(self._fit_timeline)
        self.zoom_in_btn.clicked.connect(lambda: self._zoom_timeline(0.5))

    def _update_video_overlays(self):
        if not hasattr(self, "video_frame"):
            return
        for attr in ("video_legend_overlay", "video_zoom_overlay"):
            overlay = getattr(self, attr, None)
            if overlay:
                overlay.adjustSize()
                overlay.show()
                overlay.raise_()
        legend = getattr(self, "video_legend_overlay", None)
        zoom = getattr(self, "video_zoom_overlay", None)
        geo = self.video_frame.geometry()
        if legend:
            legend.move(geo.left() + 10, geo.top() + 12)
        if zoom:
            zoom.move(geo.right() - zoom.width() - 12, geo.bottom() - zoom.height() - 12)

    def init_ui(self):
        from developer_tools.config import UI_LAYOUT, UI_COLORS
        self.setStyleSheet(f"QDialog {{ background-color: {UI_COLORS.BACKGROUND_MEDIUM}; color: {UI_COLORS.TEXT_PRIMARY}; font-family: 'Segoe UI', sans-serif; }} QToolTip {{ border: 1px solid {UI_COLORS.BORDER_ACCENT}; background-color: {UI_COLORS.BACKGROUND_DARK}; color: white; }}")
        main_layout = QVBoxLayout(self)
        main_layout.setContentsMargins(16, 16, 16, 20)
        main_layout.setSpacing(12)

        self.video_frame = QWidget()
        self.video_frame.setStyleSheet(f"background-color: {UI_COLORS.BACKGROUND_DARK}; border: 1px solid {UI_COLORS.BORDER_MEDIUM}; border-radius: 4px;")
        self.video_frame.setMinimumHeight(360)
        self.video_frame.setFocusPolicy(Qt.NoFocus)
        main_layout.addWidget(self.video_frame, stretch=1)
        self._build_video_overlays()

        self.timeline = GranularTimelineSlider(self)
        self.timeline.setRange(0, int(self.duration))
        self.timeline.setFixedHeight(64)
        self.timeline.sliderMoved.connect(self._on_timeline_moved)
        self.timeline.sliderReleased.connect(self._on_timeline_released)
        self.timeline.trim_times_changed.connect(self.on_trim_changed)
        self.timeline.setToolTip("Drag the playhead, drag orange handles, click a colored segment to edit it")
        main_layout.addWidget(self.timeline)

        readout_row = QHBoxLayout()
        readout_row.setContentsMargins(2, 0, 2, 0)
        readout_row.setSpacing(8)
        self.readout_labels = {
            "start": self._make_readout_label("START --"),
            "end": self._make_readout_label("END --"),
            "duration": self._make_readout_label("DUR --"),
            "speed": self._make_readout_label("SPEED --"),
            "output": self._make_readout_label("OUT --"),
        }
        readout_row.addStretch(1)
        for label in self.readout_labels.values():
            readout_row.addWidget(label)
        readout_row.addStretch(1)
        main_layout.addLayout(readout_row)

        # Control Cluster
        controls_row = QHBoxLayout()
        controls_row.setSpacing(8)

        # Freeze Group
        self.freeze_group = QFrame()
        self.freeze_group.setStyleSheet(f"QFrame {{ background: {UI_COLORS.BACKGROUND_DARK}; border: 1px solid {UI_COLORS.BORDER_MEDIUM}; border-radius: 6px; padding: 2px; }}")
        fg_l = QHBoxLayout(self.freeze_group)
        fg_l.setContentsMargins(6, 2, 6, 2)
        fg_l.setSpacing(8)

        self.freeze_btn = QPushButton("FREEZE IMAGE")
        self.freeze_btn.setProperty('class', 'primary')
        self.freeze_btn.setFixedSize(126, UI_LAYOUT.BUTTON_HEIGHT)
        self.freeze_btn.setCursor(Qt.PointingHandCursor)
        self.freeze_btn.setFocusPolicy(Qt.NoFocus)
        self.freeze_btn.setToolTip("Freeze the current frame for the selected duration")
        self._set_button_class(self.freeze_btn, 'primary')
        self.freeze_btn.clicked.connect(self.freeze_image)

        self.freeze_sec_spin = QDoubleSpinBox()
        self.freeze_sec_spin.setRange(FREEZE_MIN_SEC, FREEZE_MAX_SEC)
        self.freeze_sec_spin.setSuffix("s")
        self.freeze_sec_spin.setFixedSize(56, UI_LAYOUT.BUTTON_HEIGHT)
        self.freeze_sec_spin.setAlignment(Qt.AlignCenter)
        self.freeze_sec_spin.setStyleSheet(UIStyles.SPINBOX)
        self.freeze_sec_spin.setToolTip("Duration for the next freeze frame")

        fg_l.addWidget(self.freeze_btn)
        fg_l.addWidget(self.freeze_sec_spin)
        controls_row.addWidget(self.freeze_group)

        controls_row.addStretch(1)

        # Trim Group
        self.trim_group = QFrame()
        self.trim_group.setStyleSheet(f"QFrame {{ background: {UI_COLORS.BACKGROUND_DARK}; border: 1px solid {UI_COLORS.BORDER_MEDIUM}; border-radius: 6px; padding: 2px; }}")
        tg_l = QHBoxLayout(self.trim_group)
        tg_l.setContentsMargins(6, 2, 6, 2)
        tg_l.setSpacing(6)

        self.start_trim_button = QPushButton("MARK START")
        self.start_trim_button.setProperty('class', 'primary')
        self.start_trim_button.setFixedSize(90, UI_LAYOUT.BUTTON_HEIGHT)
        self.start_trim_button.setCursor(Qt.PointingHandCursor)
        self.start_trim_button.setFocusPolicy(Qt.NoFocus)
        self.start_trim_button.setToolTip("Mark the current playhead as the segment start ([)")
        self._set_button_class(self.start_trim_button, 'primary')
        self.start_trim_button.clicked.connect(self.set_start)

        self.play_btn = QPushButton("PLAY")
        self.play_btn.setProperty('class', 'success')
        self.play_btn.setFixedSize(80, UI_LAYOUT.BUTTON_HEIGHT)
        self.play_btn.setCursor(Qt.PointingHandCursor)
        self.play_btn.setFocusPolicy(Qt.NoFocus)
        self.play_btn.setIcon(self.style().standardIcon(QStyle.SP_MediaPlay))
        self.play_btn.setToolTip("Play or pause preview (Space)")
        self._set_button_class(self.play_btn, 'success')
        self.play_btn.clicked.connect(self.toggle_play)

        self.end_trim_button = QPushButton("MARK END")
        self.end_trim_button.setProperty('class', 'primary')
        self.end_trim_button.setFixedSize(90, UI_LAYOUT.BUTTON_HEIGHT)
        self.end_trim_button.setCursor(Qt.PointingHandCursor)
        self.end_trim_button.setFocusPolicy(Qt.NoFocus)
        self.end_trim_button.setToolTip("Mark the current playhead as the segment end (])")
        self._set_button_class(self.end_trim_button, 'primary')
        self.end_trim_button.clicked.connect(self.set_end)

        tg_l.addWidget(self.start_trim_button)
        tg_l.addWidget(self.play_btn)
        tg_l.addWidget(self.end_trim_button)
        controls_row.addWidget(self.trim_group)

        controls_row.addStretch(1)
        main_layout.addLayout(controls_row)

        # Action & Speed Row
        bottom_row = QHBoxLayout()
        bottom_row.setSpacing(16)

        # Left Actions
        left_actions = QHBoxLayout()
        left_actions.setSpacing(8)
        self.clear_all_btn = QPushButton("CLEAR ALL")
        self.clear_all_btn.setFixedSize(90, UI_LAYOUT.BUTTON_HEIGHT)
        self.clear_all_btn.setCursor(Qt.PointingHandCursor)
        self.clear_all_btn.setFocusPolicy(Qt.NoFocus)
        self.clear_all_btn.setToolTip("Remove every speed and freeze segment")
        self._set_button_class(self.clear_all_btn, 'primary')
        self.clear_all_btn.clicked.connect(self.clear_all_segments)

        self.delete_seg_btn = QPushButton("DELETE SEGMENT")
        self.delete_seg_btn.setFixedSize(130, UI_LAYOUT.BUTTON_HEIGHT)
        self.delete_seg_btn.setCursor(Qt.PointingHandCursor)
        self.delete_seg_btn.setFocusPolicy(Qt.NoFocus)
        self.delete_seg_btn.setToolTip("Delete the active segment (Delete)")
        self.delete_seg_btn.clicked.connect(self.delete_current_selected_segment)
        self.delete_seg_btn.setEnabled(False)
        self._set_button_class(self.delete_seg_btn, 'primary')

        left_actions.addWidget(self.clear_all_btn)
        left_actions.addWidget(self.delete_seg_btn)
        bottom_row.addLayout(left_actions)

        bottom_row.addStretch(1)

        # Center: Apply/Cancel
        center_actions = QHBoxLayout()
        center_actions.setSpacing(12)
        self.cancel_btn = QPushButton("CANCEL")
        self.cancel_btn.setProperty('class', 'warning')
        self.cancel_btn.setFixedSize(90, UI_LAYOUT.BUTTON_HEIGHT)
        self.cancel_btn.setCursor(Qt.PointingHandCursor)
        self.cancel_btn.setFocusPolicy(Qt.NoFocus)
        self.cancel_btn.setToolTip("Close without applying changes")
        self._set_button_class(self.cancel_btn, 'warning')
        self.cancel_btn.clicked.connect(self.reject)

        self.save_btn = QPushButton("APPLY")
        self.save_btn.setProperty('class', 'success')
        self.save_btn.setFixedSize(180, UI_LAYOUT.BUTTON_HEIGHT)
        self.save_btn.setCursor(Qt.PointingHandCursor)
        self.save_btn.setFocusPolicy(Qt.NoFocus)
        self.save_btn.setToolTip("Apply speed and freeze segments to the main timeline")
        self._set_button_class(self.save_btn, 'success')
        self.save_btn.clicked.connect(self.accept)

        center_actions.addWidget(self.cancel_btn)
        center_actions.addWidget(self.save_btn)
        bottom_row.addLayout(center_actions)

        bottom_row.addStretch(1)

        # Right: Speed Controls
        speed_panel = QFrame()
        speed_panel.setStyleSheet(f"QFrame {{ background: {UI_COLORS.BACKGROUND_DARK}; border: 1px solid {UI_COLORS.BORDER_MEDIUM}; border-radius: 6px; padding: 4px; }}")
        sp_l = QHBoxLayout(speed_panel)
        sp_l.setContentsMargins(8, 2, 8, 2)
        sp_l.setSpacing(12)

        # Presets
        preset_grid = QGridLayout()
        preset_grid.setSpacing(1)
        self.speed_preset_buttons = []
        presets = [("0.5x", 0.5), ("1.0x", 1.0), ("1.5x", 1.5), ("2.0x", 2.0), ("3.0x", 3.0), ("4.0x", 4.0)]
        for i, (label, val) in enumerate(presets):
            btn = QPushButton(label)
            btn.setProperty('class', 'primary')
            btn.setFixedSize(46, 22)
            btn.setToolTip(f"Set selected segment speed to {label}")
            self._set_button_class(btn, 'primary')
            btn.clicked.connect(lambda _=False, v=val: self._apply_speed_preset(v))
            preset_grid.addWidget(btn, i // 3, i % 3)
            self.speed_preset_buttons.append((btn, val))
        sp_l.addLayout(preset_grid)

        # Precise Speed
        prec_l = QVBoxLayout()
        prec_l.setSpacing(2)
        speed_lbl = QLabel("SEGMENT SPEED")
        speed_lbl.setStyleSheet(f"color: {UI_COLORS.TEXT_SECONDARY}; font-size: 9px; font-weight: bold;")
        speed_lbl.setAlignment(Qt.AlignCenter)
        self.speed_spin = QDoubleSpinBox()
        self.speed_spin.setRange(0.1, 4.0)
        self.speed_spin.setSingleStep(0.1)
        self.speed_spin.setDecimals(1)
        self.speed_spin.setFixedSize(64, UI_LAYOUT.BUTTON_HEIGHT)
        self.speed_spin.setAlignment(Qt.AlignCenter)
        self.speed_spin.setStyleSheet(UIStyles.SPINBOX)
        self.speed_spin.setToolTip("Precise speed for the active or pending segment")
        self.speed_spin.valueChanged.connect(self.on_speed_changed)
        prec_l.addWidget(speed_lbl)
        prec_l.addWidget(self.speed_spin)
        sp_l.addLayout(prec_l)

        bottom_row.addWidget(speed_panel)
        main_layout.addLayout(bottom_row)
        self._update_preset_button_styles()
        self._update_selection_readout()
        self._update_zoom_button_state()
        self._update_clear_all_btn_state()
        self._install_timeline_zoom_wheel_filters()
        QTimer.singleShot(0, self._update_video_overlays)

    def setup_player(self, attempt=0):
        if not self.input_file_path: return
        try:
            self.video_frame.setAttribute(Qt.WA_NativeWindow)
            wid = self._preview_wid()
            if wid <= 0 and attempt < 10:
                QTimer.singleShot(120, lambda: self.setup_player(attempt + 1))
                return
            self.player = MPVSafetyManager.create_safe_mpv(
                wid=wid if wid > 0 else None,
                osc=False, hr_seek='yes', hwdec='auto', keep_open='yes',
                ytdl=False,
                vo='gpu' if sys.platform == 'win32' else 'gpu',
                input_default_bindings=False, input_vo_keyboard=False,
                extra_mpv_flags=[('force-window', 'no')] if wid > 0 else []
            )
            if not self.player: return
            self._bind_player_to_preview(True)
            self._safe_mpv_set("mute", False); self._safe_mpv_set("volume", max(1, self.volume))
            start_sec = self.abs_trim_start / 1000.0
            end_sec = self.abs_trim_end / 1000.0
            if end_sec > start_sec:
                options = f"start={start_sec:.3f},end={end_sec:.3f}"
                if not self._safe_mpv_command("loadfile", self.input_file_path, "replace", 0, options): return
            else:
                if not self._safe_mpv_command("loadfile", self.input_file_path, "replace"): return
        except Exception: return

        def _get_dur(bind_attempt=0):
            if not getattr(self, "player", None): return
            try:
                if bind_attempt == 0:
                    self._bind_player_to_preview(True)
                dur = self._safe_mpv_get('duration', 0)
                if dur and dur > 0:
                    self.timeline.setRange(0, int(self.duration))
                    self.timeline.set_duration_ms(int(self.duration))
                    self.timeline.set_segments(self.speed_segments)
                    if self.speed_segments:
                        self.on_selection_cleared()
                    else:
                        self._set_default_pending_range()
                    self.selection_modified = False
                    self._update_selection_readout()
                    self._update_apply_button_label()
                    QTimer.singleShot(250, self._finalize_startup)
                elif bind_attempt < 40:
                    QTimer.singleShot(100, lambda: _get_dur(bind_attempt + 1))
            except Exception:
                if bind_attempt < 40:
                    QTimer.singleShot(100, lambda: _get_dur(bind_attempt + 1))
        QTimer.singleShot(100, lambda: _get_dur(0))

    def _finalize_startup(self):
        if not self.player: return
        self._bind_player_to_preview(True)
        self._safe_mpv_set("pause", True)
        self.last_position_ms = self._normalize_seek_position(self.start_time_ms)
        self._pending_seek = None
        self._stop_seek_timer()
        self._issue_seek_now(self.last_position_ms, True)
        self.timeline.ensure_value_visible(int(self.start_time_ms))
        self.timeline.setValue(int(self.start_time_ms)); self.play_btn.setText("PLAY"); self.play_btn.setIcon(self.style().standardIcon(QStyle.SP_MediaPlay)); self.is_playing = False
        self._update_selection_readout()

    def _preview_wid(self):
        try:
            if not getattr(self, "video_frame", None): return 0
            self.video_frame.setAttribute(Qt.WA_NativeWindow)
            wid = int(self.video_frame.winId())
            return wid if wid > 0 else 0
        except Exception:
            return 0

    def _bind_player_to_preview(self, force=False):
        if not getattr(self, "player", None): return False
        wid = self._preview_wid()
        if wid <= 0:
            return False
        last_wid = getattr(self, "_last_bound_wid", 0)
        if not force and last_wid == wid:
            return True
        now = import_time.time()
        if not force and now - getattr(self, "_last_preview_bind_ts", 0) < 0.75:
            return True
        self._last_preview_bind_ts = now
        wid_changed = (last_wid != wid)
        self._last_bound_wid = wid
        self._safe_mpv_set("wid", wid)
        self._safe_mpv_set("force_window", "no")
        if wid_changed:
            try:
                if self._safe_mpv_get("path", ""):
                    self._safe_mpv_command("video-reload")
            except Exception:
                pass
        return True

    def _is_editing_widget_focused(self) -> bool:
        fw = QApplication.focusWidget()
        if fw is None: return False
        if isinstance(fw, (QLineEdit, QTextEdit, QPlainTextEdit, QAbstractSpinBox)): return True
        if isinstance(fw, QComboBox) and (fw.isEditable() or fw.hasFocus()): return True
        return False

    def keyPressEvent(self, event):
        if self._is_editing_widget_focused():
            super().keyPressEvent(event); return
        key = event.key(); mods = event.modifiers()
        if key == Qt.Key_Space: self.toggle_play(); event.accept()
        elif key == Qt.Key_Left:
            ms = -100 if mods == Qt.ControlModifier else (-3000 if mods == Qt.ShiftModifier else -500)
            self.seek_relative(ms); event.accept()
        elif key == Qt.Key_Right:
            ms = 100 if mods == Qt.ControlModifier else (3000 if mods == Qt.ShiftModifier else 500)
            self.seek_relative(ms); event.accept()
        elif key == Qt.Key_BracketLeft: self.set_start(); event.accept()
        elif key == Qt.Key_BracketRight: self.set_end(); event.accept()
        elif key == Qt.Key_Delete:
            if self.timeline.active_segment_index != -1: self.delete_segment(self.timeline.active_segment_index)
            else: self.delete_current_selected_segment()
            event.accept()
        else: super().keyPressEvent(event)

    def eventFilter(self, obj, event):
        if event.type() == QEvent.Wheel and self._handle_timeline_zoom_wheel(event):
            return True
        return super().eventFilter(obj, event)

    def wheelEvent(self, event):
        if self._handle_timeline_zoom_wheel(event):
            return
        super().wheelEvent(event)

    def seek_relative(self, ms):
        if not self.player: return
        curr_rel = (self._safe_mpv_get('time-pos', 0) or 0) * 1000 - self.abs_trim_start
        new_rel = max(0, min(self.duration, curr_rel + ms))
        self.seek_video(new_rel, exact=True); self.timeline.setValue(int(new_rel)); self.timeline.update()

    def _on_timeline_moved(self, value):
        self.seek_video(value, exact=False)

    def _on_timeline_released(self):
        self.seek_video(self.timeline.value(), exact=True)

    def _check_collision(self, start, end, exclude_idx=-1):
        for i, seg in enumerate(self.speed_segments):
            if i == exclude_idx: continue
            if start < seg['end'] + SEGMENT_GAP_MS and end > seg['start'] - SEGMENT_GAP_MS: return True
        return False

    def _commit_pending_segment(self, require_difference=False):
        if self.timeline.active_segment_index != -1:
            return True
        if not (getattr(self, "_pending_segment_active", False) or getattr(self, "_start_manually_set", False)):
            return True
        s = int(self.timeline.trimmed_start_ms)
        e = int(self.timeline.trimmed_end_ms)
        if e <= s:
            e = self._clamp_selection_end(s + PENDING_PREVIEW_MS, s)
        else:
            e = self._clamp_selection_end(e, s)
        if e - s < MIN_SEGMENT_MS:
            self.timeline.flash_blocked_edge(e)
            self._emit_error("Segment too short or blocked by a neighboring segment.")
            return False
        speed = float(self.speed_spin.value())
        if require_difference and abs(speed - self.base_speed) <= 0.01:
            self._pending_segment_active = False
            self._start_manually_set = False
            self.timeline.stop_ants()
            return True
        if self._check_collision(s, e):
            self.timeline.flash_blocked_edge(s)
            self._emit_error("Collision: segment overlaps another segment.")
            return False
        new_seg = {'start': s, 'end': e, 'speed': speed}
        self.speed_segments.append(new_seg)
        self.speed_segments.sort(key=lambda x: x['start'])
        self.timeline.set_segments(self.speed_segments)
        self._pending_segment_active = False
        self._start_manually_set = False
        self.list_modified = True
        self.selection_modified = False
        self._update_clear_all_btn_state()
        for i, seg in enumerate(self.speed_segments):
            if seg == new_seg:
                self.edit_segment(i)
                break
        return True

    def _emit_error(self, message: str):
        clean = str(message or "").strip()
        if hasattr(self.parent_app, 'statusBar'):
            try: self.parent_app.statusBar().showMessage(clean, 4000)
            except Exception: pass
        self.status_update_signal.emit(clean)

    def _emit_info(self, message: str):
        clean = str(message or "").strip()
        if hasattr(self.parent_app, 'statusBar'):
            try: self.parent_app.statusBar().showMessage(clean, 3000)
            except Exception: pass
        self.status_update_signal.emit(clean)

    def freeze_image(self):
        if not self.player: return
        try:
            was_playing = not bool(self._safe_mpv_get("pause", True))
            if was_playing: self.pause_video()
        except Exception:
            was_playing = False
        curr_rel = (self._safe_mpv_get('time-pos', 0) or 0) * 1000 - self.abs_trim_start
        requested_dur_sec = float(self.freeze_sec_spin.value())
        freeze_dur_ms = int(round(requested_dur_sec * 1000))
        duration_limit = int(self.duration)
        if duration_limit <= MIN_SEGMENT_MS:
            self._emit_error("Video clip is too short to freeze.")
            return
        start = max(0, min(int(curr_rel), duration_limit - MIN_SEGMENT_MS))
        max_possible_ms = duration_limit - start
        end = min(duration_limit, start + freeze_dur_ms)
        if end - start < MIN_SEGMENT_MS:
            self._emit_error("Freeze needs more remaining video time after the playhead.")
            return
        shrunk = False
        if freeze_dur_ms > max_possible_ms:
            shrunk = True
        if self._check_collision(start, end):
            for i, seg in enumerate(self.speed_segments):
                if seg['start'] > start:
                    end = min(end, seg['start'])
                    break
            if end - start < MIN_SEGMENT_MS:
                self.timeline.flash_blocked_edge(start)
                self._emit_error("Collision: this freeze overlaps an existing speed segment.")
                return
            if self._check_collision(start, end):
                self.timeline.flash_blocked_edge(end)
                self._emit_error("Collision: this freeze overlaps an existing speed segment.")
                return
            shrunk = True
        new_seg = {'start': start, 'end': end, 'speed': 0.0}
        self.speed_segments.append(new_seg); self.speed_segments.sort(key=lambda x: x['start'])
        self.timeline.set_segments(self.speed_segments); self.on_selection_cleared()
        for i, seg in enumerate(self.speed_segments):
            if seg == new_seg: self.edit_segment(i); break
        self.timeline.setValue(start)
        self.list_modified = True; self._update_clear_all_btn_state()
        actual_dur_sec = (end - start) / 1000.0
        if shrunk:
            self._emit_info(f"Frame frozen for {actual_dur_sec:.1f}s at {self._fmt(start)} (shrunk to fit; no frames skipped).")
        else:
            self._emit_info(f"Frame frozen for {actual_dur_sec:.1f}s at {self._fmt(start)} (no frames skipped).")

    def _fmt(self, ms: int) -> str:
        s = max(0, int(ms) // 1000); h, s = divmod(s, 3600); m, s = divmod(s, 60)
        return f"{h:d}:{m:02d}:{s:02d}" if h else f"{m:d}:{s:02d}"

    def toggle_play(self):
        if not self.player: return
        if not self._safe_mpv_get("pause", True): self.pause_video()
        else:
            curr_rel = (self._safe_mpv_get('time-pos', 0) or 0) * 1000 - self.abs_trim_start
            if curr_rel >= self.duration - 100: self.seek_video(0, exact=True); self.timeline.setValue(0); curr_rel = 0
            self._safe_mpv_set("pause", False); self.update_playback_speed(int(curr_rel)); self.timer.start(); self.play_btn.setText("PAUSE"); self.play_btn.setIcon(self.style().standardIcon(QStyle.SP_MediaPause))

    def pause_video(self):
        if self.player: self._safe_mpv_set("pause", True)
        self.timer.stop(); self.play_btn.setText("PLAY"); self.play_btn.setIcon(self.style().standardIcon(QStyle.SP_MediaPlay))

    def _normalize_seek_position(self, pos):
        try:
            rel_pos = int(round(float(pos)))
        except (TypeError, ValueError):
            rel_pos = int(getattr(self, "last_position_ms", 0) or 0)
        return max(0, min(int(self.duration), rel_pos))

    def _stop_seek_timer(self):
        timer = getattr(self, "_seek_timer", None)
        if timer is not None:
            try: timer.stop()
            except Exception: pass

    def _start_seek_timer(self, delay_ms):
        timer = getattr(self, "_seek_timer", None)
        if timer is None:
            return
        try:
            timer.start(max(1, int(delay_ms)))
        except Exception:
            pass

    def _last_seek_timestamp(self):
        try:
            return float(getattr(self, "_last_seek_command_ts", 0.0) or 0.0)
        except (TypeError, ValueError):
            return 0.0

    def _is_duplicate_seek(self, abs_pos_ms, exact, now):
        last_target = getattr(self, "_last_seek_target_ms", None)
        if last_target is None:
            return False
        try:
            last_target_ms = int(last_target)
        except (TypeError, ValueError):
            return False
        if last_target_ms != int(abs_pos_ms):
            return False
        elapsed_ms = (now - self._last_seek_timestamp()) * 1000.0
        if elapsed_ms > SEEK_DUPLICATE_WINDOW_MS:
            return False
        if not exact:
            return True
        return bool(getattr(self, "_last_seek_exact", False))

    def _issue_seek_now(self, rel_pos, exact, now=None):
        if not self.player:
            return False
        rel_pos = self._normalize_seek_position(rel_pos)
        abs_pos_ms = int(self.abs_trim_start) + rel_pos
        now = import_time.monotonic() if now is None else now
        if self._is_duplicate_seek(abs_pos_ms, bool(exact), now):
            return True
        mode = "exact" if exact else "keyframes"
        ok = self._safe_mpv_command("seek", abs_pos_ms / 1000.0, "absolute", mode)
        if ok:
            self._last_seek_command_ts = now
            self._last_seek_target_ms = abs_pos_ms
            self._last_seek_exact = bool(exact)
        return ok

    def _flush_pending_seek(self):
        pending = getattr(self, "_pending_seek", None)
        if pending is None:
            return
        rel_pos, exact = pending
        now = import_time.monotonic()
        elapsed_ms = (now - self._last_seek_timestamp()) * 1000.0
        if not exact and elapsed_ms < SEEK_THROTTLE_MS:
            self._start_seek_timer(SEEK_THROTTLE_MS - elapsed_ms)
            return
        self._pending_seek = None
        self._issue_seek_now(rel_pos, exact, now=now)

    def seek_video(self, pos, exact=False):
        self._in_freeze_segment = False
        self._freeze_seg_idx = -1
        if not self.player: return
        rel_pos = self._normalize_seek_position(pos)
        self.last_position_ms = rel_pos
        try:
            self.timeline.ensure_value_visible(rel_pos)
            self._update_zoom_button_state()
        except Exception:
            pass
        self.update_playback_speed(rel_pos)
        if exact:
            self._pending_seek = None
            self._stop_seek_timer()
            self._issue_seek_now(rel_pos, True)
            return
        now = import_time.monotonic()
        abs_pos_ms = int(self.abs_trim_start) + rel_pos
        if self._is_duplicate_seek(abs_pos_ms, False, now):
            return
        elapsed_ms = (now - self._last_seek_timestamp()) * 1000.0
        if elapsed_ms >= SEEK_THROTTLE_MS:
            self._pending_seek = None
            self._issue_seek_now(rel_pos, False, now=now)
        else:
            self._pending_seek = (rel_pos, False)
            self._start_seek_timer(SEEK_THROTTLE_MS - elapsed_ms)

    def update_ui(self):
        if not self.player or getattr(self, "_updating_ui", False): return
        self._updating_ui = True
        try:
            if getattr(self, "_in_freeze_segment", False):
                now = import_time.time(); elapsed = (now - self._freeze_start_ts) * 1000
                if self._freeze_seg_idx < 0 or self._freeze_seg_idx >= len(self.speed_segments):
                    self._in_freeze_segment = False; self._freeze_seg_idx = -1
                    return
                seg = self.speed_segments[self._freeze_seg_idx]; seg_dur = seg['end'] - seg['start']
                if elapsed >= seg_dur:
                    resume_rel = int(seg['start'])
                    self._in_freeze_segment = False; self._freeze_seg_idx = -1
                    self._safe_mpv_command("seek", (self.abs_trim_start + resume_rel) / 1000.0, "absolute", "exact")
                    self.update_playback_speed(resume_rel)
                    self._safe_mpv_set("pause", False)
                    self.timeline.ensure_value_visible(resume_rel)
                    self.timeline.blockSignals(True); self.timeline.setValue(resume_rel); self.timeline.blockSignals(False); self.timeline.update()
                    self.last_position_ms = resume_rel
                return
            curr_rel = (self._safe_mpv_get('time-pos', 0) or 0) * 1000 - self.abs_trim_start
            if getattr(self, "_start_manually_set", False):
                start = int(self.timeline.trimmed_start_ms)
                end = self._clamp_selection_end(int(curr_rel), start)
                self.timeline.set_trim_times(start, end)
                self.update_pending_visualization()
            if not self.timeline.isSliderDown():
                if curr_rel >= self.duration:
                    self.pause_video()
                    curr_rel = self.duration
                    self.seek_video(self.duration, exact=True)
                elif curr_rel < 0: curr_rel = 0
                t_i = int(round(curr_rel)); self.timeline.ensure_value_visible(t_i); self.timeline.blockSignals(True); self.timeline.setValue(t_i); self.timeline.blockSignals(False); self.timeline.update()
                self.update_playback_speed(t_i); self.last_position_ms = t_i
        finally: self._updating_ui = False

    def update_playback_speed(self, rel_time):
        if not self.player: return
        target_speed = self.base_speed; seg_idx = -1
        for i, seg in enumerate(self.speed_segments):
            if abs(seg['speed']) < 0.001 and seg['start'] <= rel_time < seg['end']:
                target_speed = 0.0; seg_idx = i; break
        if abs(target_speed) > 0.001:
            for i, seg in enumerate(self.speed_segments):
                if abs(seg['speed']) >= 0.001 and seg['start'] <= rel_time < seg['end']:
                    target_speed = seg['speed']; seg_idx = i; break
        if abs(target_speed) < 0.001:
            if not getattr(self, "_in_freeze_segment", False):
                self._in_freeze_segment = True; self._freeze_start_ts = import_time.time(); self._freeze_seg_idx = seg_idx; self._safe_mpv_set("pause", True)
            return
        if getattr(self, "_in_freeze_segment", False):
            self._in_freeze_segment = False; self._freeze_seg_idx = -1
        now = import_time.time()
        if now - getattr(self, "_last_rate_update", 0) < 0.05: return
        current_rate = self._safe_mpv_get("speed", 1.0)
        if abs(current_rate - target_speed) > 0.005:
            result = self._safe_mpv_set("speed", target_speed)
            if result is not False:
                self._last_rate_update = now

    def on_speed_changed(self, val):
        if getattr(self, "_suppress_speed_change_flag", False):
            self.update_pending_visualization()
            self._update_preset_button_styles()
            self.update_playback_speed(self.last_position_ms)
            return
        idx = self.timeline.active_segment_index
        if idx != -1 and 0 <= idx < len(self.speed_segments):
            if abs(self.speed_segments[idx]['speed'] - val) > 1e-9:
                self.speed_segments[idx]['speed'] = val
                self.timeline.set_segments(self.speed_segments)
                self.selection_modified = True
                self.list_modified = True
        elif self.timeline.trimmed_start_ms >= 0 and self.timeline.trimmed_end_ms > self.timeline.trimmed_start_ms:
            self.selection_modified = True
        self.update_pending_visualization()
        self._update_preset_button_styles()
        self.update_playback_speed(self.last_position_ms)

    def on_trim_changed(self, s, e):
        idx = self.timeline.active_segment_index
        if idx != -1 and 0 <= idx < len(self.speed_segments):
            seg = self.speed_segments[idx]
            new_start = int(s)
            new_end = int(e)
            if new_end - new_start < MIN_SEGMENT_MS:
                self.timeline.set_trim_times(seg['start'], seg['end'])
                self.timeline.flash_blocked_edge(new_end)
                self.update_pending_visualization()
                return
            if self._check_collision(new_start, new_end, exclude_idx=idx):
                self.timeline.set_trim_times(seg['start'], seg['end'])
                self.timeline.flash_blocked_edge(new_start if abs(new_start - seg['start']) > abs(new_end - seg['end']) else new_end)
                self._emit_error("Collision: segment overlaps another segment.")
                self.update_pending_visualization()
                return
            seg['start'] = new_start
            seg['end'] = new_end
            self.speed_segments.sort(key=lambda x: x['start'])
            self.timeline.set_segments(self.speed_segments)
            self.timeline.active_segment_index = self.speed_segments.index(seg)
            self.selection_modified = True
            self.list_modified = True
            self._update_clear_all_btn_state()
        self.update_pending_visualization()

    def update_pending_visualization(self):
        s, e = self.timeline.trimmed_start_ms, self.timeline.trimmed_end_ms
        if self.timeline.active_segment_index != -1:
            self.timeline.set_pending_segment(-1, -1, self.speed_spin.value())
            self._update_selection_readout()
            self._update_apply_button_label()
            return
        if s < 0 or e <= s:
            self.timeline.set_pending_segment(-1, -1, self.speed_spin.value())
            self._update_selection_readout()
            self._update_apply_button_label()
            return
        if not (getattr(self, "_pending_segment_active", False) or getattr(self, "_start_manually_set", False)):
            self.timeline.set_pending_segment(-1, -1, self.speed_spin.value())
            self._update_selection_readout()
            self._update_apply_button_label()
            return
        self.timeline.set_pending_segment(s, e, self.speed_spin.value())
        self._update_selection_readout()
        self._update_apply_button_label()

    def _update_delete_button_label(self, idx):
        if not hasattr(self, "delete_seg_btn"): return
        if 0 <= idx < len(self.speed_segments) and abs(self.speed_segments[idx].get('speed', 1.0)) < 0.001:
            self.delete_seg_btn.setText("DELETE FREEZE IMAGE")
        else:
            self.delete_seg_btn.setText("DELETE SPEED SEGMENT")

    def on_selection_cleared(self):
        self.timeline.active_segment_index = -1
        self._pending_segment_active = False
        self._start_manually_set = False
        self.delete_seg_btn.setEnabled(False)
        self._set_button_class(self.delete_seg_btn, 'primary')
        self._update_delete_button_label(-1)
        self._suppress_speed_change_flag = True
        try:
            self.speed_spin.setValue(self.base_speed)
        finally:
            self._suppress_speed_change_flag = False
        self.timeline.set_trim_times(-1, -1)
        self.update_pending_visualization()
        self.timeline.stop_ants()
        self._update_preset_button_styles()

    def edit_segment(self, idx):
        if 0 <= idx < len(self.speed_segments):
            seg = self.speed_segments[idx]
            self._pending_segment_active = False
            self._start_manually_set = False
            self.timeline.active_segment_index = idx
            self.timeline.set_trim_times(seg['start'], seg['end'])
            self.timeline.ensure_range_visible(seg['start'], seg['end'])
            self._suppress_speed_change_flag = True
            try:
                self.speed_spin.setValue(seg['speed'])
            finally:
                self._suppress_speed_change_flag = False
            self.delete_seg_btn.setEnabled(True)
            self._set_button_class(self.delete_seg_btn, 'danger')
            self._update_delete_button_label(idx)
            self.update_pending_visualization()
            self.timeline.stop_ants()
            self._update_preset_button_styles()

    def add_segment(self):
        self._commit_pending_segment()

    def delete_segment(self, idx):
        if 0 <= idx < len(self.speed_segments):
            self.speed_segments.pop(idx); self.timeline.set_segments(self.speed_segments); self.on_selection_cleared(); self.list_modified = True; self._update_clear_all_btn_state()

    def delete_current_selected_segment(self):
        if self.timeline.active_segment_index != -1: self.delete_segment(self.timeline.active_segment_index)

    def clear_all_segments(self):
        if not self.speed_segments: return
        msg = QMessageBox(self); msg.setIcon(QMessageBox.Question); msg.setWindowTitle("Clear All"); msg.setText("Are you sure you want to delete all custom speed segments?"); msg.setStandardButtons(QMessageBox.Yes | QMessageBox.No)
        if msg.exec_() == QMessageBox.Yes:
            self.speed_segments = []
            self.timeline.set_segments([])
            self.list_modified = True
            self._update_clear_all_btn_state()
            self._set_default_pending_range()

    def set_start(self):
        current_speed = float(self.speed_spin.value())
        self.on_selection_cleared()
        self._suppress_speed_change_flag = True
        try:
            self.speed_spin.setValue(current_speed)
        finally:
            self._suppress_speed_change_flag = False
        curr_rel = (self._safe_mpv_get('time-pos', 0) or 0) * 1000 - self.abs_trim_start
        anchor = max(0, int(curr_rel))
        gap = self._gap_for_point(anchor)
        if gap is None:
            self._emit_error("Move the playhead outside an existing segment before marking a new start.")
            return
        _low, high = gap
        if high - anchor < MIN_SEGMENT_MS:
            self._emit_error("No free room after this start point.")
            return
        preview_end = min(high, anchor + PENDING_PREVIEW_MS)
        preview_end = max(preview_end, anchor + MIN_SEGMENT_MS)
        preview_end = min(preview_end, high)
        self.timeline.set_trim_times(anchor, preview_end)
        self.timeline.ensure_range_visible(anchor, preview_end)
        self._start_manually_set = True
        self._pending_segment_active = True
        self.timeline.start_ants()
        self.update_pending_visualization()

    def set_end(self):
        curr_rel = (self._safe_mpv_get('time-pos', 0) or 0) * 1000 - self.abs_trim_start
        e = int(curr_rel)
        if getattr(self, "_start_manually_set", False):
            s = self.timeline.trimmed_start_ms
            e = self._clamp_selection_end(e, s)
        else:
            s, e = self._auto_range_ending_at(e)
        self.timeline.active_segment_index = -1
        self.timeline.set_trim_times(s, e)
        self.timeline.ensure_range_visible(s, e)
        self._pending_segment_active = True
        created = False
        if e - s >= MIN_SEGMENT_MS:
            created = self._commit_pending_segment()
        else:
            self._emit_error("Segment too short (minimum 10 ms).")
        self._start_manually_set = False
        if not created:
            self._pending_segment_active = False
            self.timeline.stop_ants()
        self.update_pending_visualization()
        self.pause_video()

    def accept(self):
        if self.timeline.active_segment_index == -1:
            speed_changed = abs(float(self.speed_spin.value()) - self.base_speed) > 0.01
            should_commit = bool(getattr(self, "_start_manually_set", False) or getattr(self, "_pending_segment_active", False) and (self.selection_modified or speed_changed))
            if should_commit and not self._commit_pending_segment(require_difference=not getattr(self, "_start_manually_set", False)):
                return
        self.cleanup(); self.save_geometry(); super().accept()

    def reject(self):
        if self.list_modified or self.selection_modified:
            msg = QMessageBox(self); msg.setIcon(QMessageBox.Warning); msg.setWindowTitle("Unsaved Changes"); msg.setText("You have unsaved speed segments. Discard and close?"); msg.setStandardButtons(QMessageBox.Yes | QMessageBox.Cancel)
            if msg.exec_() == QMessageBox.Cancel: return
        self.cleanup(); self.save_geometry(); super().reject()

    def cleanup(self):
        self.timer.stop()
        self._pending_seek = None
        self._stop_seek_timer()
        try: self.timeline.stop_ants()
        except Exception: pass
        if self.player: MPVSafetyManager.safe_mpv_shutdown(self.player); self.player = None

    def showEvent(self, event):
        super().showEvent(event)
        QTimer.singleShot(0, lambda: self._bind_player_to_preview(True))
        QTimer.singleShot(0, self._update_video_overlays)
        if getattr(self, "_pending_segment_active", False) or getattr(self, "_start_manually_set", False):
            self.timeline.start_ants()

    def hideEvent(self, event):
        super().hideEvent(event)
        try: self.timeline.stop_ants()
        except Exception: pass

    def resizeEvent(self, event):
        super().resizeEvent(event)
        QTimer.singleShot(0, lambda: self._bind_player_to_preview(True))
        QTimer.singleShot(0, self._update_video_overlays)

    def closeEvent(self, event):
        self.reject()
        if self.isVisible(): event.ignore()
        else: event.accept()
