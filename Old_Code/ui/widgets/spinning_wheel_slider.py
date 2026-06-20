from PyQt5.QtCore import Qt, QRectF, pyqtSignal, QPropertyAnimation, pyqtProperty, QEasingCurve
from PyQt5.QtGui import QPainter, QColor, QFont, QFontMetrics, QPen, QLinearGradient, QRadialGradient, QPainterPath
from PyQt5.QtWidgets import QWidget
import math

class SpinningWheelSlider(QWidget):
    valueChanged = pyqtSignal(int)

    def __init__(self, parent=None):
        super().__init__(parent)
        self._value = 0
        self._range = (0, 4)
        self._labels = ["BAD", "OKAY", "STANDARD", "GOOD", "MAXIMUM"]
        self._rotation = 0.0 
        self._anim = QPropertyAnimation(self, b"rotation")
        self._anim.setDuration(150)
        self._anim.setEasingCurve(QEasingCurve.OutCubic)
        self.setFixedSize(180, 35)
        self.setCursor(Qt.OpenHandCursor)
        self.setEnabled(False)
        self._is_dragging = False
        self._last_mouse_x = 0
        self._drag_velocity = 0
        self._overscroll = 0.08

    def setLabels(self, labels):
        self._labels = labels
        self.update()

    def _clamp_rotation(self, val: float) -> float:
        lo = self._range[0] - self._overscroll
        hi = self._range[1] + self._overscroll
        return max(lo, min(hi, float(val)))
    @pyqtProperty(float)
    def rotation(self): return self._rotation
    @rotation.setter
    def rotation(self, val):
        self._rotation = self._clamp_rotation(val)
        new_val = int(round(max(self._range[0], min(self._range[1], self._rotation))))
        if new_val != self._value:
            self._value = new_val
            self.valueChanged.emit(new_val)
        self.update()

    def setValue(self, val, animated=True):
        val = max(self._range[0], min(self._range[1], int(val)))
        if val != self._value or abs(self._rotation - val) > 0.01:
            self._value = val
            if animated:
                self._anim.stop()
                self._anim.setStartValue(self._rotation)
                self._anim.setEndValue(float(val))
                self._anim.start()
            else:
                self.rotation = float(val)
            self.valueChanged.emit(val)

    def value(self): return self._value

    def setRange(self, min_val, max_val):
        self._range = (min_val, max_val)
        self.update()

    def mousePressEvent(self, event):
        if not self.isEnabled(): return
        self._is_dragging = True
        self._last_mouse_x = event.x()
        self._anim.stop()
        self.setCursor(Qt.ClosedHandCursor)

    def mouseMoveEvent(self, event):
        if not self._is_dragging: return
        dx = event.x() - self._last_mouse_x
        self._last_mouse_x = event.x()
        sensitivity = 0.011
        new_rot = self._rotation - (dx * sensitivity)
        self.rotation = new_rot

    def mouseReleaseEvent(self, event):
        if not self._is_dragging: return
        self._is_dragging = False
        self.setCursor(Qt.OpenHandCursor)
        target = int(round(max(self._range[0], min(self._range[1], self._rotation))))
        self.setValue(target)

    def paintEvent(self, event):
        p = QPainter(self)
        p.setRenderHint(QPainter.Antialiasing)
        w, h = self.width(), self.height()
        cx, cy = w / 2, h / 2
        rect = QRectF(0, 0, w, h)
        rim_grad = QLinearGradient(0, 0, 0, h)
        rim_grad.setColorAt(0.0, QColor("#15202b"))
        rim_grad.setColorAt(0.5, QColor("#3e5871"))
        rim_grad.setColorAt(1.0, QColor("#15202b"))
        p.setBrush(rim_grad)
        p.setPen(QPen(QColor("#0d1217"), 1))
        p.drawRoundedRect(rect, 6, 6)
        inner_rect = rect.adjusted(3, 3, -3, -3)
        face_grad = QRadialGradient(cx, cy, w * 0.7, cx, cy)
        face_grad.setColorAt(0.0, QColor("#3a6b6b"))
        face_grad.setColorAt(0.4, QColor("#1e313d"))
        face_grad.setColorAt(0.8, QColor("#0f1a0f"))
        face_grad.setColorAt(1.0, QColor("#080c08"))
        p.setBrush(face_grad)
        pen_color = QColor("#000000")
        pen_color.setAlpha(140)
        p.setPen(QPen(pen_color, 1))
        p.drawRoundedRect(inner_rect, 4, 4)
        p.setPen(Qt.NoPen)
        for i in range(self._range[0] - 5, self._range[1] + 6):
            rib_angle = (i - self._rotation) * (math.pi / 5)
            if abs(rib_angle) > math.pi / 1.8: continue
            rib_opacity = math.cos(rib_angle) ** 2.0
            rib_x = cx + math.sin(rib_angle) * (w * 0.85)
            rib_w = max(1.0, 5.0 * (rib_opacity ** 2.5))
            rib_grad = QLinearGradient(rib_x - rib_w/2, 0, rib_x + rib_w/2, 0)
            rib_grad.setColorAt(0.0, QColor(0, 10, 20, int(130 * rib_opacity)))
            rib_grad.setColorAt(0.5, QColor(210, 245, 255, int(40 * rib_opacity)))
            rib_grad.setColorAt(1.0, QColor(0, 20, 10, int(130 * rib_opacity)))
            p.setBrush(rib_grad)
            p.drawRect(QRectF(rib_x - rib_w/2, inner_rect.top(), rib_w, inner_rect.height()))
        shadow_rect = inner_rect.adjusted(1, 1, -1, -1)
        shadow_grad = QLinearGradient(0, shadow_rect.top(), 0, shadow_rect.bottom())
        shadow_grad.setColorAt(0.0, QColor(0, 0, 0, 210))
        shadow_grad.setColorAt(0.2, QColor(0, 0, 0, 0))
        shadow_grad.setColorAt(0.8, QColor(0, 0, 0, 0))
        shadow_grad.setColorAt(1.0, QColor(0, 0, 0, 210))
        p.setBrush(shadow_grad)
        p.setPen(Qt.NoPen)
        p.drawRoundedRect(shadow_rect, 4, 4)
        p.save()
        p.setClipRect(shadow_rect)
        for i in range(self._range[0], self._range[1] + 1):
            angle = (i - self._rotation) * (math.pi / 5)
            if abs(angle) > math.pi / 1.4: continue
            opacity = math.cos(angle)
            if opacity < 0: continue
            x_pos = cx + math.sin(angle) * (w * 0.82)
            scale = 0.50 + (0.60 * (opacity ** 0.6))
            y_bulge = (1.0 - (opacity ** 0.3)) * 12
            f = QFont("Segoe UI", int(9 * scale), QFont.Bold)
            p.setFont(f)
            fm = QFontMetrics(f)
            txt = self._labels[i] if i < len(self._labels) else str(i)
            tw, th = fm.horizontalAdvance(txt), fm.height()
            if i == self._value:
                color = QColor("#50ffef") if self.isEnabled() else QColor("#95a5a6")
            else:
                color = QColor("#c5dcf2") if self.isEnabled() else QColor("#7f8c8d")
            alpha = int(255 * (opacity ** 5.0))
            color.setAlpha(alpha)
            p.setPen(QColor(0,0,0, int(alpha * 0.8)))
            p.drawText(int(x_pos - tw/2 + 2), int(cy + th/3 + y_bulge + 2), txt)
            p.setPen(color)
            p.drawText(int(x_pos - tw/2), int(cy + th/3 + y_bulge), txt)
        p.restore()
        if self.isEnabled():
            p.setPen(QPen(QColor("#ff4d4d"), 2, Qt.SolidLine, Qt.RoundCap))
            p.drawLine(int(cx), 3, int(cx), 11)
            p.drawLine(int(cx), h-11, int(cx), h-3)
            center_glow = QRadialGradient(cx, cy, 50)
            center_glow.setColorAt(0, QColor(80, 255, 239, 45))
            center_glow.setColorAt(1, QColor(0, 0, 0, 0))
            p.setBrush(center_glow)
            p.setPen(Qt.NoPen)
            p.drawEllipse(QRectF(cx-50, cy-50, 100, 100))
