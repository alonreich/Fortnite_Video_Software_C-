from PyQt5.QtGui import QPainter, QColor, QPen, QBrush, QFont

class MergerPhaseOverlayDraw:
    def _draw_performance_graphs(self, event):
        """Draw responsive 4-lane timeline monitor (left->right scrolling samples)."""
        if not hasattr(self, "_graph"):
            return
        painter = QPainter(self._graph)
        try:
            painter.setRenderHint(QPainter.Antialiasing)
            rect = self._graph.rect()
            w = max(1, rect.width())
            h = max(1, rect.height())
            painter.fillRect(rect, QColor(8, 17, 25, 230))
            cpu_data = list(getattr(self, "_cpu_hist", []))
            gpu_data = list(getattr(self, "_gpu_hist", []))
            mem_data = list(getattr(self, "_mem_hist", []))
            io_data = list(getattr(self, "_iops_hist", []))
            metrics = [
                (cpu_data, QColor("#45aaf2"), "CPU"),
                (gpu_data, QColor("#ff6b6b"), "GPU"),
                (io_data, QColor("#f7b731"), "I/O"),
                (mem_data, QColor("#2ed573"), "MEM"),
            ]
            left_pad = 66
            right_pad = 12
            top_pad = 8
            lane_gap = 8
            lanes = 4
            lane_h = max(22, int((h - top_pad * 2 - lane_gap * (lanes - 1)) / lanes))
            chart_w = max(20, w - left_pad - right_pad)
            painter.setFont(QFont("Segoe UI", 9, QFont.Bold))
            for lane_idx, (data, color, label) in enumerate(metrics):
                lane_y = top_pad + lane_idx * (lane_h + lane_gap)
                painter.fillRect(left_pad, lane_y, chart_w, lane_h, QColor(20, 35, 48, 170))
                mid_y = lane_y + lane_h // 2
                painter.setPen(QPen(QColor(120, 145, 165, 80), 1))
                painter.drawLine(left_pad, mid_y, left_pad + chart_w, mid_y)
                latest = int(data[-1]) if data else 0
                painter.setPen(QColor("#e6edf3"))
                painter.drawText(6, lane_y + 14, label)
                painter.setPen(color)
                painter.drawText(6, lane_y + lane_h - 5, f"{latest}%")
                if not data:
                    continue
                samples = data[-chart_w:]
                for x_off, val in enumerate(samples):
                    x = left_pad + x_off
                    bar_h = max(1, int((max(0, min(100, int(val))) / 100.0) * (lane_h - 2)))
                    y = lane_y + lane_h - bar_h - 1
                    painter.setPen(QPen(color, 1))
                    painter.drawLine(x, lane_y + lane_h - 1, x, y)
                painter.setPen(QPen(QColor(90, 110, 130, 90), 1))
                painter.drawRect(left_pad, lane_y, chart_w, lane_h)
        finally:
            painter.end()
