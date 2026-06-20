from PyQt5.QtCore import QUrl, Qt
from PyQt5.QtGui import QDesktopServices

class MergerHandlersPreviewMixin:
    def preview_file(self, path: str):
        try:
            QDesktopServices.openUrl(QUrl.fromLocalFile(path))
        except Exception as e:
            self.logger.error("Preview failed: %s", e)

    def preview_clicked(self):
        try:
            btn = self.parent.sender()
            p = btn.property("path")
            if p:
                self.preview_file(str(p))
        except Exception as e:
            if hasattr(self.parent, "set_status_message"):
                self.parent.set_status_message("Preview failed", "color: #ff6b6b; font-weight: bold;", 2500)

    def make_item_widget(self, path: str, rank: int = 1):
        from PyQt5.QtWidgets import QWidget, QHBoxLayout, QLabel, QPushButton, QSizePolicy, QFrame
        from utilities.merger_ui_style import MergerUIStyle
        import os
        w = QWidget()
        w.setFixedHeight(50)
        w.setMinimumWidth(380)
        w.setStyleSheet("background: transparent;")
        h = QHBoxLayout(w)
        h.setContentsMargins(0, 0, 0, 0)
        h.setSpacing(10)
        h.addStretch(1)
        rank_lbl = QLabel(f"#{rank}")
        rank_lbl.setObjectName("rankLabel")
        rank_lbl.setFixedWidth(45)
        rank_lbl.setStyleSheet("font-size: 16px; font-weight: bold; color: #7DD3FC; background: transparent;")
        rank_lbl.setAlignment(Qt.AlignRight | Qt.AlignVCenter)
        h.addWidget(rank_lbl)
        frame = QFrame()
        frame.setObjectName("videoItemFrame")
        frame.setFixedSize(500, 50)
        frame.setSizePolicy(QSizePolicy.Fixed, QSizePolicy.Fixed)
        frame.setStyleSheet("""
            QFrame#videoItemFrame { 
                background-color: #2c3e50; 
                border: 2px solid #34495e; 
                border-radius: 8px; 
            }
            QFrame#videoItemFrame:hover {
                background-color: #34495e;
                border-color: #3498db;
            }
        """)
        frame_layout = QHBoxLayout(frame)
        frame_layout.setContentsMargins(15, 0, 10, 0)
        frame_layout.setSpacing(10)
        lbl = QLabel(os.path.basename(path))
        lbl.setObjectName("fileLabel")
        lbl.setStyleSheet("font-size:13px; font-weight: 600; color: #ecf0f1; background: transparent; border: none;")
        lbl.setToolTip(path)
        lbl.setWordWrap(False)
        lbl.setFixedHeight(24)
        lbl.setSizePolicy(QSizePolicy.Expanding, QSizePolicy.Fixed)
        btn = QPushButton("▶  PREVIEW  ▶")
        btn.setObjectName("playButton")
        btn.setFixedSize(110, 32)
        btn.setCursor(Qt.PointingHandCursor)
        btn.setStyleSheet(MergerUIStyle.BUTTON_STANDARD)
        btn.setProperty("path", path)
        btn.clicked.connect(self.preview_clicked)
        frame_layout.addWidget(lbl, 1)
        frame_layout.addWidget(btn, 0, Qt.AlignRight | Qt.AlignVCenter)
        h.addWidget(frame)
        h.addStretch(1)
        w.video_frame = frame
        w.rank_label = rank_lbl

        def set_marked(is_marked: bool):
            if is_marked:
                frame.setStyleSheet("""
                    QFrame#videoItemFrame { 
                        background-color: #34495e; 
                        border: 2px solid #3498db; 
                        border-radius: 8px; 
                    }
                """)
                lbl.setStyleSheet("font-size:13px; font-weight: bold; color: #ffffff; background: transparent; border: none;")
            else:
                frame.setStyleSheet("""
                    QFrame#videoItemFrame { 
                        background-color: #2c3e50; 
                        border: 2px solid #34495e; 
                        border-radius: 8px; 
                    }
                    QFrame#videoItemFrame:hover {
                        background-color: #34495e;
                        border-color: #3498db;
                    }
                """)
                lbl.setStyleSheet("font-size:13px; font-weight: 600; color: #ecf0f1; background: transparent; border: none;")
        w.set_marked = set_marked
        return w
