import math
import psutil
import time
from PyQt5.QtCore import Qt, QRect, QTimer, QPoint
from PyQt5.QtGui import QRegion, QIcon, QColor, QPainter, QPen, QBrush, QFont
from PyQt5.QtWidgets import QWidget

class MergerPhaseOverlayLogic:
    def _layout_overlay_panels(self, main_rect):
        """Keep overlay panels large and readable across all window sizes."""
        margin_x = 24
        margin_y = 20
        avail_w = max(100, main_rect.width() - (2 * margin_x))
        avail_h = max(120, main_rect.height() - (2 * margin_y))
        pb_h = 18
        pb_gap = 10
        graph_h = max(180, int((avail_h - pb_h - pb_gap) * 0.42))
        log_y = margin_y + graph_h + 14
        log_h = max(100, (margin_y + avail_h - pb_h - pb_gap) - log_y)
        pb_y = margin_y + avail_h - pb_h
        if hasattr(self, "_graph"):
            self._graph.setGeometry(margin_x, margin_y, avail_w, graph_h)
            self._graph.raise_()
        if hasattr(self, "live_log"):
            self.live_log.setGeometry(margin_x, log_y, avail_w, log_h)
            self.live_log.raise_()
        if hasattr(self, "_overlay_progress_bar"):
            self._overlay_progress_bar.setGeometry(margin_x, pb_y, avail_w, pb_h)
            self._overlay_progress_bar.raise_()

    def _resize_overlay(self) -> None:
        """Called by the main resizeEvent to resize/mask the overlay."""
        try:
            if getattr(self, "_overlay", None) and self._overlay.isVisible():
                parent = self._overlay.parentWidget()
                if parent:
                    self._overlay.setGeometry(parent.rect())
                else:
                    self._overlay.setGeometry(self.rect())
                self._update_overlay_mask()
        except Exception:
            pass

    def _update_overlay_mask(self):
        """Positions graph/log full-screen and keeps controls visible above overlay using a punch-hole mask."""
        try:
            if not getattr(self, "_overlay", None) or not self._overlay.isVisible():
                return
            parent = self._overlay.parentWidget()
            main_rect = parent.rect() if parent else self.rect()
            self._overlay.setGeometry(main_rect)
            self._overlay.raise_()
            
            full_region = QRegion(self._overlay.rect())
            button_holes = QRegion()
            
            # Identify buttons that need to be "punched through" the overlay
            widgets_to_show = ["btn_merge", "btn_cancel", "btn_processing", "btn_back"]
            for w_name in widgets_to_show:
                w = getattr(self, w_name, None)
                if w and not w.isHidden():
                    try:
                        # Direct global mapping is the most robust way to handle nested layouts
                        g_pos = w.mapToGlobal(QPoint(0, 0))
                        o_pos = self._overlay.mapFromGlobal(g_pos)
                        
                        # Use a generous 12px padding to ensure the full button + glow/border is visible
                        # This resolves the clipping issue where edges were being cut off
                        mapped_rect = QRect(o_pos.x() - 12, o_pos.y() - 12, w.width() + 24, w.height() + 24)
                        button_holes |= QRegion(mapped_rect)
                        w.raise_()
                    except: pass
            
            # Apply the mask: only show the overlay where the buttons AREN'T
            self._overlay.setMask(full_region - button_holes)
            self._layout_overlay_panels(main_rect)
        except Exception:
            try:
                if getattr(self, "_overlay", None):
                    self._overlay.clearMask()
            except: pass

    def _show_processing_overlay(self) -> None:
        """Shows the overlay and starts stats/pulse timers."""
        self._ensure_overlay_widgets()
        try:
            for nm in ("_cpu_hist", "_gpu_hist", "_mem_hist", "_iops_hist"):
                if hasattr(self, nm):
                    getattr(self, nm).clear()
            parent = self._overlay.parentWidget()
            self._overlay.setGeometry(parent.rect() if parent else self.rect())
            self._overlay.show()
            self._overlay.raise_()
            if hasattr(self, "_overlay_progress_bar"):
                self._overlay_progress_bar.setValue(0)
            self._layout_overlay_panels(self._overlay.rect())
            self._update_overlay_mask()
            self._sample_perf_counters_safe()
            self._stats_timer.start()
            if hasattr(self, "_gpu_worker") and not self._gpu_worker.isRunning():
                self._gpu_worker.start()
            if not getattr(self, "_color_pulse_timer", None):
                self._color_pulse_timer = QTimer(self)
                self._color_pulse_timer.setInterval(100)
                self._color_pulse_timer.timeout.connect(self._pulse_button_color)
            self._color_pulse_timer.start()
        except Exception:
            pass

    def _hide_processing_overlay(self) -> None:
        """Hides overlay, stops timers, and restores button style."""
        try:
            if getattr(self, "_stats_timer", None):
                self._stats_timer.stop()
            if hasattr(self, "_gpu_worker") and self._gpu_worker.isRunning():
                self._gpu_worker.stop()
        except Exception:
            pass
        try:
            if getattr(self, "_color_pulse_timer", None):
                self._color_pulse_timer.stop()
            if hasattr(self, "btn_merge") and hasattr(self, "_original_merge_btn_style"):
                self.btn_merge.setStyleSheet(self._original_merge_btn_style)
            if hasattr(self, "_overlay_progress_bar"):
                self._overlay_progress_bar.setValue(0)
        except Exception:
            pass
        try:
            if getattr(self, "_overlay", None):
                self._overlay.hide()
        except Exception:
            pass

    def _pulse_button_color(self):
        try:
            if not getattr(self, "is_processing", False):
                if getattr(self, "_color_pulse_timer", None):
                    self._color_pulse_timer.stop()
                return
            self._pulse_phase = (getattr(self, "_pulse_phase", 0) + 1) % 40
            k = (math.sin(self._pulse_phase * 0.4) + 1) / 2.0
            r1, g1, b1 = 30, 200, 100  # Green
            r2, g2, b2 = 180, 255, 200 # Light Green
            r = int(r1 * k + r2 * (1 - k))
            g = int(g1 * k + g2 * (1 - k))
            b = int(b1 * k + b2 * (1 - k))
            
            dots = "." * (self._pulse_phase % 4)
            current_text = f"PROCESSING{dots}"
            
            self.btn_processing.setStyleSheet(f"""
                QPushButton {{
                    background-color: rgb({r},{g},{b});
                    color: black;
                    font-weight: bold;
                    font-size: 14px;
                    border-radius: 10px;
                    padding: 10px 18px;
                    border-style: solid;
                    border-top: 1px solid rgba(255, 255, 255, 0.2);
                    border-left: 1px solid rgba(255, 255, 255, 0.2);
                    border-bottom: 1px solid rgba(0, 0, 0, 0.6);
                    border-right: 1px solid rgba(0, 0, 0, 0.6);
                }}
            """)
            self.btn_processing.setText(current_text)
        except Exception:
            pass

    def _sample_perf_counters_safe(self):
        """Gathers CPU/GPU/etc stats and updates the graph data."""
        try:
            cpu = int(psutil.cpu_percent(interval=None))
        except Exception:
            cpu = 0
        gpu = getattr(self, "_last_gpu_val", 0)
        try:
            mem = int(psutil.virtual_memory().percent)
        except Exception:
            mem = 0
        try:
            now = time.time()
            cur = psutil.disk_io_counters()
            cur_ops = int(getattr(cur, "read_count", 0)) + int(getattr(cur, "write_count", 0))
            prev = getattr(self, "_iops_prev", None)
            if prev is None:
                iops = 0.0
            else:
                dt = max(1e-3, now - prev["ts"])
                iops = max(0.0, (cur_ops - prev["ops"]) / dt)
            self._iops_prev = {"ts": now, "ops": cur_ops}
            dyn = max(1.0, float(getattr(self, "_iops_dyn_max", 1.0)))
            if iops > dyn * 0.98:
                dyn = iops * 1.25
            self._iops_dyn_max = dyn
            iops_pct = int(max(0, min(100, round(100.0 * iops / dyn))))
        except Exception:
            iops_pct = 0
        self._cpu_hist.append(cpu)
        self._gpu_hist.append(gpu)
        self._iops_hist.append(iops_pct)
        self._mem_hist.append(mem)
        if getattr(self, "_overlay", None) and self._overlay.isVisible():
            if hasattr(self, "_graph"):
                self._graph.update()
