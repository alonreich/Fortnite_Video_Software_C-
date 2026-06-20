import math
import shutil
import os
import subprocess
import sys
import time
from collections import deque
import psutil
from PyQt5.QtCore import Qt, QTimer, QRect, QThread, pyqtSignal, QPoint
from PyQt5.QtGui import QRegion, QIcon, QColor, QPainter, QPen, QBrush, QFont
from PyQt5.QtWidgets import QWidget, QPlainTextEdit

def _find_nvidia_smi():
    found = shutil.which("nvidia-smi")
    if found:
        return found
    if sys.platform == "win32":
        candidates = [
            r"C:\Windows\System32\nvidia-smi.exe",
            r"C:\Program Files\NVIDIA Corporation\NVSMI\nvidia-smi.exe",
        ]
        for path in candidates:
            try:
                if os.path.exists(path):
                    return path
            except Exception:
                pass
    return None

class GpuWorker(QThread):
    stats_updated = pyqtSignal(int)

    def __init__(self):
        super().__init__()
        self._running = True
        self._nvidia_smi = _find_nvidia_smi()

    def stop(self):
        self._running = False

    def start_polling(self):
        self._running = True
        if self.isRunning():
            return
        self.start()

    def run(self):
        while self._running:
            gpu = 0
            try:
                if self._nvidia_smi and self._running:
                    flags = subprocess.CREATE_NO_WINDOW if sys.platform == "win32" else 0
                    r = subprocess.run(
                        [self._nvidia_smi, "--query-gpu=utilization.gpu,utilization.encoder", "--format=csv,noheader,nounits", "-i", "0"],
                        capture_output=True, text=True, timeout=1.5, creationflags=flags
                    )
                    if r.returncode == 0 and self._running:
                        output = (r.stdout or "0,0").strip().splitlines()
                        if output:
                            row = output[0].split(",")
                            gpu_core = int(row[0].strip() or 0)
                            gpu_enc  = int(row[1].strip() or 0)
                            gpu = max(0, min(100, max(gpu_core, gpu_enc)))
            except: pass
            if self._running:
                try: self.stats_updated.emit(gpu)
                except: pass
            for _ in range(15):
                if not self._running: break
                time.sleep(0.1)

class PhaseOverlayMixin:
    def _append_live_log(self, line: str) -> None:
        if " | " in line:
            parts = line.split(" | ")
            line = parts[-1].strip()
        if not hasattr(self, "_log_buffer"):
            self._log_buffer = []
        line = str(line or "").strip()
        if line:
            self._log_buffer.append(line)

    def _flush_logs(self):
        if hasattr(self, "_log_buffer") and self._log_buffer and getattr(self, "live_log", None):
            chunk = "\n".join(self._log_buffer)
            self._log_buffer.clear()
            self.live_log.appendPlainText(chunk)
            self.live_log.verticalScrollBar().setValue(
                self.live_log.verticalScrollBar().maximum()
            )

    def _ensure_overlay_widgets(self) -> None:
        if getattr(self, "_overlay", None):
            return
        self._overlay = QWidget(self)
        self._overlay.setAttribute(Qt.WA_NoSystemBackground, True)
        self._overlay.setAttribute(Qt.WA_TransparentForMouseEvents, False)
        
        def _paint_overlay(event):
            painter = QPainter(self._overlay)
            painter.fillRect(self._overlay.rect(), QColor(11, 20, 29, 250))
            painter.end()
        self._overlay.paintEvent = _paint_overlay
        self._overlay.hide()
        self._graph = QWidget(self._overlay)
        self._graph.setAttribute(Qt.WA_NoSystemBackground, True)
        self._graph.setAttribute(Qt.WA_TransparentForMouseEvents, True)
        self._graph.paintEvent = self._draw_performance_graphs
        self.live_log = QPlainTextEdit(self._overlay)
        self.live_log.setReadOnly(True)
        self.live_log.setMaximumBlockCount(5000)
        self.live_log.setStyleSheet("""
            QPlainTextEdit {
                color: #00ff66; background: rgba(2, 10, 14, 230); border: 1px solid rgba(0, 255, 102, 90);
                font-family: Consolas, monospace; font-size: 8pt; padding: 4px;
            }
        """)
        for nm in ("_cpu_hist", "_gpu_hist", "_mem_hist", "_iops_hist"):
            if not hasattr(self, nm):
                setattr(self, nm, deque(maxlen=200))
        self._log_buffer = []
        self._log_flush_timer = QTimer(self)
        self._log_flush_timer.setInterval(250)
        self._log_flush_timer.timeout.connect(self._flush_logs)
        self._log_flush_timer.start()
        self._last_gpu_val = 0
        self._gpu_worker = GpuWorker()
        self._gpu_worker.stats_updated.connect(self._on_gpu_update)
        self._stats_timer = QTimer(self)
        self._stats_timer.setInterval(1000)
        self._stats_timer.timeout.connect(self._sample_perf_counters_safe)
        self._overlay.installEventFilter(self)

    def _on_gpu_update(self, val):
        try:
            val = int(val)
        except Exception:
            val = 0
        self._last_gpu_val = max(0, min(100, val))

    def _resize_overlay(self) -> None:
        try:
            if getattr(self, "_overlay", None):
                self._overlay.setGeometry(self.rect())
                if self._overlay.isVisible():
                    self._update_overlay_mask()
        except Exception:
            pass

    def _update_overlay_mask(self):
        try:
            if not getattr(self, "_overlay", None) or not self._overlay.isVisible():
                return
            main_rect = self.rect()
            self._overlay.setGeometry(main_rect)
            mask_region = QRegion(main_rect)
            widgets_to_show = ["process_button", "cancel_button", "progress_bar"]
            for w_name in widgets_to_show:
                w = getattr(self, w_name, None)
                if w and w.isVisible():
                    global_pos = w.mapToGlobal(QPoint(0,0))
                    local_pos = self._overlay.mapFromGlobal(global_pos)
                    w_rect = QRect(local_pos, w.size())
                    mask_region = mask_region.subtracted(QRegion(w_rect))
            if hasattr(self, "timeline_overlay") and self.timeline_overlay:
                 self.timeline_overlay.hide()
            self._overlay.setMask(mask_region)
            self._overlay.raise_()
            margin_x = 40
            margin_y = 40
            avail_w = main_rect.width() - (2 * margin_x)
            avail_h = main_rect.height() - (2 * margin_y)
            if avail_w < 100 or avail_h < 100: return
            graph_h = 240 
            if hasattr(self, "_graph"):
                self._graph.setGeometry(margin_x, margin_y, avail_w, graph_h)
            if hasattr(self, "live_log"):
                log_y = margin_y + graph_h + 30
                log_h = max(150, avail_h - graph_h - 100)
                self.live_log.setGeometry(margin_x, log_y, avail_w, log_h)
                self.live_log.show()
                self.live_log.raise_()
        except Exception:
            pass

    def _show_processing_overlay(self) -> None:
        if not getattr(self, "is_processing", False): return
        self._ensure_overlay_widgets()
        try:
            if hasattr(self, "process_button") and not hasattr(self, "_original_process_btn_style"):
                self._original_process_btn_style = self.process_button.styleSheet()
            if hasattr(self, "cancel_button") and not hasattr(self, "_original_cancel_btn_style"):
                self._original_cancel_btn_style = self.cancel_button.styleSheet()
            for nm in ("_cpu_hist", "_gpu_hist", "_mem_hist", "_iops_hist"):
                if hasattr(self, nm):
                    getattr(self, nm).clear()
            self._overlay.setGeometry(self.rect())
            if hasattr(self, "live_log"):
                self.live_log.clear()
            if hasattr(self, "_log_buffer"):
                self._log_buffer.clear()
            self._append_live_log("Backend log stream attached.")
            self._overlay.show()
            self._overlay.raise_()
            QTimer.singleShot(0, self._update_overlay_mask)
            QTimer.singleShot(100, self._update_overlay_mask)
            QTimer.singleShot(500, self._update_overlay_mask)
            self._sample_perf_counters_safe()
            self._stats_timer.start()
            if hasattr(self, "_gpu_worker"):
                self._gpu_worker.start_polling()
            if not getattr(self, "_color_pulse_timer", None):
                self._color_pulse_timer = QTimer(self)
                self._color_pulse_timer.setInterval(100)
                self._color_pulse_timer.timeout.connect(self._pulse_button_color)
            self._color_pulse_timer.start()
        except Exception:
            pass

    def _hide_processing_overlay(self) -> None:
        try:
            if getattr(self, "_overlay", None):
                self._overlay.hide()
                self._overlay.lower()
        except: pass
        try:
            if getattr(self, "_stats_timer", None):
                self._stats_timer.stop()
            if hasattr(self, "_gpu_worker") and self._gpu_worker.isRunning():
                self._gpu_worker.stop()
        except: pass
        try:
            if getattr(self, "_color_pulse_timer", None):
                self._color_pulse_timer.stop()
            if hasattr(self, "process_button") and hasattr(self, "_original_process_btn_style"):
                self.process_button.setStyleSheet(self._original_process_btn_style)
                self.process_button.setIcon(QIcon())
            if hasattr(self, "cancel_button") and hasattr(self, "_original_cancel_btn_style"):
                self.cancel_button.setStyleSheet(self._original_cancel_btn_style)
        except: pass

    def _pulse_button_color(self):
        try:
            if not getattr(self, "is_processing", False):
                if getattr(self, "_color_pulse_timer", None):
                    self._color_pulse_timer.stop()
                return
            self._pulse_phase = (getattr(self, "_pulse_phase", 0) + 1) % 20
            t = self._pulse_phase / 20.0
            k = (math.sin(4 * math.pi * t) + 1) / 2
            g1 = (72, 235, 90)
            g2 = (10,  80, 16)
            r = int(g1[0] * k + g2[0] * (1 - k))
            g = int(g1[1] * k + g2[1] * (1 - k))
            b = int(g1[2] * k + g2[2] * (1 - k))
            current_text = self.process_button.text()
            current_icon = self.process_button.icon()
            self.process_button.setStyleSheet(f"""
                QPushButton#processButton {{ 
                    background-color: rgb({r},{g},{b});
                    color: #ffffff;
                    font-weight: bold;
                    font-size: 12px;
                    border-radius: 10px;
                    padding: 10px 18px;
                    border-style: solid;
                    border-top: 1px solid rgba(255, 255, 255, 0.2);
                    border-left: 1px solid rgba(255, 255, 255, 0.2);
                    border-bottom: 1px solid rgba(0, 0, 0, 0.6);
                    border-right: 1px solid rgba(0, 0, 0, 0.6);
                    min-width: 125px;
                    max-width: 125px;
                    min-height: 65px;
                    max-height: 65px;
                }} 
                QPushButton#processButton:hover {{  background-color: #c8f7c5; }} 
            """)
            self.process_button.setText(current_text)
            self.process_button.setIcon(current_icon)
        except Exception:
            pass

    def _sample_perf_counters_safe(self):
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
            self._overlay.update()

    def _set_overlay_phase(self, phase: str) -> None:
        p = (phase or "").lower()
        if any(x in p for x in ("processing", "step", "encode", "intro", "core", "concat")):
            if not getattr(self, "is_processing", False): return
            if not getattr(self, "_overlay", None) or not self._overlay.isVisible():
                self._show_processing_overlay()
        elif any(x in p for x in ("done", "idle", "error", "failed")):
            if getattr(self, "_overlay", None) and self._overlay.isVisible():
                self._hide_processing_overlay()

    def _draw_performance_graphs(self, event):
        painter = QPainter(self._graph)
        painter.setRenderHint(QPainter.Antialiasing)
        w = self._graph.width()
        painter.fillRect(self._graph.rect(), QColor(11, 20, 29, 120))
        metrics = [
            (list(self._cpu_hist), "#3498db", "CPU"),
            (list(self._gpu_hist), "#e74c3c", "GPU"),
            (list(self._mem_hist), "#2ecc71", "MEM"),
            (list(self._iops_hist), "#f1c40f", "I/O")
        ]
        stick_w = 10
        stick_max_h = 45
        gap = 2
        row_spacing = 55
        start_x = 75 
        font = QFont("Segoe UI", 9, QFont.Bold)
        painter.setFont(font)
        for idx, (data, color, label) in enumerate(metrics):
            y_base = idx * row_spacing + 10
            cur_val = data[-1] if data else 0
            painter.setPen(QColor("white"))
            painter.drawText(5, y_base + 18, label)
            painter.setPen(QColor(color))
            painter.drawText(5, y_base + 38, f"{cur_val}%")
            if not data: continue
            for i, val in enumerate(data):
                x = start_x + i * (stick_w + gap)
                if x + stick_w > w:
                    offset = (i * (stick_w + gap)) - (w - start_x - stick_w)
                    x = start_x + i * (stick_w + gap) - offset
                    if x < start_x: continue
                painter.fillRect(x, y_base, stick_w, stick_max_h, QColor(31, 53, 69, 80))
                fill_h = max(1, int((val / 100.0) * stick_max_h))
                painter.fillRect(x, y_base + stick_max_h - fill_h, stick_w, fill_h, QBrush(QColor(color)))
                painter.setPen(QPen(QColor(color).lighter(130), 1))
                painter.drawLine(x, y_base + stick_max_h - fill_h, x + stick_w - 1, y_base + stick_max_h - fill_h)
            if idx < len(metrics) - 1:
                line_y = y_base + row_spacing - 5
                painter.setPen(QPen(QColor(16, 185, 129, 60), 2)) 
                painter.drawLine(0, line_y, w, line_y)
