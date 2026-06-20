import shutil
import subprocess
import sys
import time
from collections import deque
from PyQt5.QtCore import Qt, QTimer, QThread, pyqtSignal
from PyQt5.QtGui import QPainter, QColor
from PyQt5.QtWidgets import QWidget, QPlainTextEdit, QProgressBar
_ENABLE_SAFE_GPU_STATS = shutil.which("nvidia-smi") is not None

class MergerGpuWorker(QThread):
    """Background worker to poll GPU stats without freezing the Main GUI."""
    stats_updated = pyqtSignal(int)

    def __init__(self):
        super().__init__()
        self._running = True

    def stop(self):
        self._running = False
        self.wait()

    def run(self):
        while self._running:
            gpu = 0
            try:
                if _ENABLE_SAFE_GPU_STATS:
                    flags = subprocess.CREATE_NO_WINDOW if sys.platform == "win32" else 0
                    r = subprocess.run(
                        ["nvidia-smi", "--query-gpu=utilization.gpu,utilization.encoder", "--format=csv,noheader,nounits", "-i", "0"],
                        capture_output=True, text=True, timeout=1.0, creationflags=flags
                    )
                    if r.returncode == 0:
                        row = (r.stdout or "0,0").strip().splitlines()[0].split(",")
                        gpu_core = int(row[0].strip() or 0)
                        gpu_enc  = int(row[1].strip() or 0)
                        gpu = max(0, min(100, max(gpu_core, gpu_enc)))
            except Exception:
                pass
            self.stats_updated.emit(gpu)
            for _ in range(10):
                if not self._running: break
                time.sleep(0.1)

class MergerPhaseOverlayMixin:
    def _append_live_log(self, line: str) -> None:
        """Buffers log lines and flushes them in batches to save CPU."""
        if not getattr(self, "live_log", None):
            return
        if not hasattr(self, "_log_buffer"):
            self._log_buffer = []
        self._log_buffer.append(line)

    def _flush_logs(self):
        """Called by timer to dump buffered logs to UI."""
        if hasattr(self, "_log_buffer") and self._log_buffer and getattr(self, "live_log", None):
            chunk = "\n".join(self._log_buffer)
            self._log_buffer.clear()
            self.live_log.appendPlainText(chunk)
            self.live_log.verticalScrollBar().setValue(
                self.live_log.verticalScrollBar().maximum()
            )

    def _ensure_overlay_widgets(self) -> None:
        """Creates the (hidden) overlay, graph, and log widgets."""
        if getattr(self, "_overlay", None):
            return
        ov_parent = self.centralWidget() if hasattr(self, "centralWidget") and self.centralWidget() else self
        self._overlay = QWidget(ov_parent)
        self._overlay.setWindowFlags(Qt.Widget)
        self._overlay.setAttribute(Qt.WA_NoSystemBackground, True)
        self._overlay.hide()
        self._graph = QWidget(self._overlay)
        self._graph.setAttribute(Qt.WA_NoSystemBackground, True)
        self._graph.setAttribute(Qt.WA_TransparentForMouseEvents, True)
        self._graph.paintEvent = self._draw_performance_graphs
        
        def _paint_overlay(event):
            painter = QPainter(self._overlay)
            painter.fillRect(self._overlay.rect(), QColor(11, 20, 29, 250))
            painter.end()
        self._overlay.paintEvent = _paint_overlay

        self.live_log = QPlainTextEdit(self._overlay)
        self.live_log.setReadOnly(True)
        self.live_log.setMaximumBlockCount(5000)
        self.live_log.setStyleSheet("""
            QPlainTextEdit {
                color:#25e825; background:#071018; border:1px solid #1f3545; border-radius:10px;
                font-family: Consolas, monospace; font-size: 12px;
            }
        """)
        self._overlay_progress_bar = QProgressBar(self._overlay)
        self._overlay_progress_bar.setRange(0, 100)
        self._overlay_progress_bar.setValue(0)
        self._overlay_progress_bar.setTextVisible(True)
        self._overlay_progress_bar.setFormat("%p%")
        self._overlay_progress_bar.setStyleSheet("""
            QProgressBar {
                border: 1px solid #266b89;
                border-radius: 5px;
                text-align: center;
                height: 18px;
                background-color: #34495e;
                color: white;
            }
            QProgressBar::chunk {
                background-color: #2ecc71;
                border-radius: 4px;
            }
        """)
        for nm in ("_cpu_hist", "_gpu_hist", "_mem_hist", "_iops_hist"):
            setattr(self, nm, deque(maxlen=200))
        self._log_buffer = []
        self._log_flush_timer = QTimer(self)
        self._log_flush_timer.setInterval(250)
        self._log_flush_timer.timeout.connect(self._flush_logs)
        self._log_flush_timer.start()
        self._last_gpu_val = 0
        self._gpu_worker = MergerGpuWorker()
        self._gpu_worker.stats_updated.connect(lambda v: setattr(self, "_last_gpu_val", v))
        self._stats_timer = QTimer(self)
        self._stats_timer.setInterval(1000)
        self._stats_timer.timeout.connect(self._sample_perf_counters_safe)
        self._overlay.installEventFilter(self)
