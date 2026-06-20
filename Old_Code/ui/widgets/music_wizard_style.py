class MergerUIStyle:
    BUTTON_COMMON = """
        QPushButton {
            color: #ffffff;
            border-style: solid;
            border-radius: 8px;
            font-weight: bold;
            font-size: 10px;
            padding: 8px 14px;
        }
        QPushButton:hover:!disabled {
            border: 1px solid #7DD3FC;
        }
        QPushButton:disabled {
            background-color: #4a5a63;
            color: #95a5a6;
            border: 1px solid #34495e;
        }
    """
    BUTTON_STANDARD = BUTTON_COMMON + """
        QPushButton {
            background: qlineargradient(x1:0, y1:0, x2:0, y2:1, stop:0 #3a8db0, stop:0.1 #2d7da1, stop:1 #1a5276);
            border-top: 1px solid rgba(255, 255, 255, 0.2);
            border-left: 1px solid rgba(255, 255, 255, 0.2);
            border-bottom: 1px solid rgba(0, 0, 0, 0.6);
            border-right: 1px solid rgba(0, 0, 0, 0.6);
            padding: 8px 2px;
        }
        QPushButton:pressed:!disabled {
            background: qlineargradient(x1:0, y1:0, x2:0, y2:1, stop:0 #0d2c3d, stop:1 #1a5276);
            border-top: 1px solid rgba(0, 0, 0, 0.7);
            border-left: 1px solid rgba(0, 0, 0, 0.7);
            border-bottom: 1px solid rgba(255, 255, 255, 0.1);
            border-right: 1px solid rgba(255, 255, 255, 0.1);
            padding-top: 9px;
            padding-left: 3px;
            padding-bottom: 7px;
            padding-right: 1px;
        }
    """
    BUTTON_CANCEL = BUTTON_COMMON + """
        QPushButton {
            background: qlineargradient(x1:0, y1:0, x2:0, y2:1, stop:0 #e67e22, stop:0.1 #d35400, stop:1 #a04000);
            border-top: 1px solid rgba(255, 255, 255, 0.2);
            border-left: 1px solid rgba(255, 255, 255, 0.2);
            border-bottom: 1px solid rgba(0, 0, 0, 0.6);
            border-right: 1px solid rgba(0, 0, 0, 0.6);
        }
        QPushButton:pressed:!disabled {
            background: qlineargradient(x1:0, y1:0, x2:0, y2:1, stop:0 #5e2600, stop:1 #a04000);
            border-top: 1px solid rgba(0, 0, 0, 0.7);
            border-left: 1px solid rgba(0, 0, 0, 0.7);
            border-bottom: 1px solid rgba(255, 255, 255, 0.1);
            border-right: 1px solid rgba(255, 255, 255, 0.1);
            padding-top: 9px;
            padding-left: 15px;
            padding-bottom: 7px;
            padding-right: 13px;
        }
    """
    BUTTON_ARROW = BUTTON_COMMON + """
        QPushButton {
            background: qlineargradient(x1:0, y1:0, x2:0, y2:1, stop:0 #3a8db0, stop:0.1 #2d7da1, stop:1 #1a5276);
            font-size: 20px;
            padding: 2px;
            border-top: 1px solid rgba(255, 255, 255, 0.2);
            border-left: 1px solid rgba(255, 255, 255, 0.2);
            border-bottom: 1px solid rgba(0, 0, 0, 0.6);
            border-right: 1px solid rgba(0, 0, 0, 0.6);
        }
        QPushButton:pressed:!disabled {
            background: qlineargradient(x1:0, y1:0, x2:0, y2:1, stop:0 #0d2c3d, stop:1 #1a5276);
            border-top: 1px solid rgba(0, 0, 0, 0.7);
            border-left: 1px solid rgba(0, 0, 0, 0.7);
            border-bottom: 1px solid rgba(255, 255, 255, 0.1);
            border-right: 1px solid rgba(255, 255, 255, 0.1);
            padding-top: 3px;
            padding-left: 3px;
            padding-bottom: 1px;
            padding-right: 1px;
        }
    """
    BUTTON_MERGE = BUTTON_COMMON + """
        QPushButton {
            background: qlineargradient(x1:0, y1:0, x2:0, y2:1, stop:0 #2ecc71, stop:0.1 #27ae60, stop:1 #1b6d26);
            border-radius: 10px;
            border-top: 1px solid rgba(255, 255, 255, 0.2);
            border-left: 1px solid rgba(255, 255, 255, 0.2);
            border-bottom: 1px solid rgba(0, 0, 0, 0.6);
            border-right: 1px solid rgba(0, 0, 0, 0.6);
            font-size: 10px;
        }
        QPushButton:pressed:!disabled {
            background: qlineargradient(x1:0, y1:0, x2:0, y2:1, stop:0 #0e3514, stop:1 #1b6d26);
            border-top: 1px solid rgba(0, 0, 0, 0.7);
            border-left: 1px solid rgba(0, 0, 0, 0.7);
            border-bottom: 1px solid rgba(255, 255, 255, 0.1);
            border-right: 1px solid rgba(255, 255, 255, 0.1);
            padding-top: 9px;
            padding-left: 15px;
            padding-bottom: 7px;
            padding-right: 13px;
        }
    """
    BUTTON_DANGER = BUTTON_COMMON + """
        QPushButton {
            background: qlineargradient(x1:0, y1:0, x2:0, y2:1, stop:0 #e74c3c, stop:0.1 #c0392b, stop:1 #8e2317);
            border-top: 1px solid rgba(255, 255, 255, 0.2);
            border-left: 1px solid rgba(255, 255, 255, 0.2);
            border-bottom: 1px solid rgba(0, 0, 0, 0.6);
            border-right: 1px solid rgba(0, 0, 0, 0.6);
            padding: 8px 4px;
        }
        QPushButton:pressed:!disabled {
            background: qlineargradient(x1:0, y1:0, x2:0, y2:1, stop:0 #4d1009, stop:1 #8e2317);
            border-top: 1px solid rgba(0, 0, 0, 0.7);
            border-left: 1px solid rgba(0, 0, 0, 0.7);
            border-bottom: 1px solid rgba(255, 255, 255, 0.1);
            border-right: 1px solid rgba(255, 255, 255, 0.1);
            padding-top: 9px;
            padding-left: 5px;
            padding-bottom: 7px;
            padding-right: 3px;
        }
    """
    BUTTON_TOOL = BUTTON_COMMON + """
        QPushButton {
            background: qlineargradient(x1:0, y1:0, x2:0, y2:1, stop:0 #4a90e2, stop:1 #318181);
            padding: 5px 2px;
            border-radius: 6px;
            border-top: 1px solid rgba(255, 255, 255, 0.1);
            border-left: 1px solid rgba(255, 255, 255, 0.1);
            border-bottom: 1px solid rgba(0, 0, 0, 0.5);
            border-right: 1px solid rgba(0, 0, 0, 0.5);
            font-size: 10px;
        }
        QPushButton:pressed:!disabled {
            background: qlineargradient(x1:0, y1:0, x2:0, y2:1, stop:0 #1a3a3a, stop:1 #318181);
            border-top: 1px solid rgba(0, 0, 0, 0.6);
            border-left: 1px solid rgba(0, 0, 0, 0.6);
            border-bottom: 1px solid rgba(255, 255, 255, 0.1);
            border-right: 1px solid rgba(255, 255, 255, 0.1);
            padding-top: 6px;
            padding-left: 3px;
            padding-bottom: 4px;
            padding-right: 1px;
        }
    """
    PROGRESS_BAR = """
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
    """
    STATUS_BAR = """
        QStatusBar {
            background: #2c3e50;
            color: #bdc3c7;
            border-top: 1px solid #34495e;
        }
    """
    SLIDER_VOLUME_VERTICAL_METALLIC = """
        QSlider::groove:vertical {
            background: #0f172a;
            width: 6px;
            border-radius: 3px;
        }
        QSlider::sub-page:vertical {
            background: #0f172a;
            border-radius: 3px;
        }
        QSlider::add-page:vertical {
            background: qlineargradient(x1:0, y1:0, x2:0, y2:1, stop:0 #3b82f6, stop:1 #06b6d4);
            border-radius: 3px;
        }
        QSlider::handle:vertical {
            background: qlineargradient(x1:0, y1:0, x2:0, y2:1, stop:0 #1e293b, stop:0.45 #1e293b, stop:0.5 #7dd3fc, stop:0.55 #1e293b, stop:1 #1e293b);
            border: 1px solid #3b82f6;
            height: 18px;
            margin: 0 -5px;
            border-radius: 5px;
        }
        QSlider::handle:vertical:hover {
            background: qlineargradient(x1:0, y1:0, x2:0, y2:1, stop:0 #334155, stop:0.45 #334155, stop:0.5 #a5f3fc, stop:0.55 #334155, stop:1 #334155);
            border: 1.5px solid #7dd3fc;
        }
    """
    SLIDER_MUSIC_VERTICAL_METALLIC = """
        QSlider::groove:vertical {
            background: #1a1a1a;
            width: 6px;
            border-radius: 3px;
        }
        QSlider::handle:vertical {
            background: qlineargradient(x1:0, y1:0, x2:1, y2:0, 
                stop:0 #5a5a5a, 
                stop:0.35 #9a9a9a, 
                stop:0.38 #000, stop:0.42 #000,
                stop:0.45 #9a9a9a,
                stop:0.48 #000, stop:0.52 #000,
                stop:0.55 #9a9a9a,
                stop:0.58 #000, stop:0.62 #000,
            stop:0.65 #9a9a9a,
            stop:1 #5a5a5a);
            border: 1px solid #111;
            width: 40px;
            height: 15px;
            margin: 0 -17px;
            border-radius: 2px;
        }
        QSlider::handle:vertical:hover {
            border: 2px solid #7DD3FC;
        }
        QSlider::add-page:vertical { background: #3498db; border-radius: 3px; }
        QSlider::sub-page:vertical { background: #333; border-radius: 3px; }
    """

class MergerUIStyleMixin:
    def set_style(self):
        self.parent.setStyleSheet('''
            QWidget {
                background-color: #2c3e50;
                color: #ecf0f1;
                font-family: "Helvetica Neue", Arial, sans-serif;
            }
            QLabel { font-size: 12px; padding: 5px; background: transparent; }
            QListWidget {
                background-color: #34495e;
                border: 2px solid #266b89;
                border-radius: 10px;
                padding: 2px;
                color: white;
                outline: none;
            }
            QListWidget::item {
                background: transparent;
            }
            QListWidget::item:selected {
                background: transparent;
            }
            QRubberBand {
                background-color: rgba(125, 211, 252, 55);
                border: 2px solid #7DD3FC;
            }
            QToolTip {
                font-family: Arial;
                font-size: 13pt;
                font-weight: normal;
                border: 1px solid #ecf0f1;
                background-color: #34495e;
                color: #ecf0f1;
                padding: 5px;
            }
        ''')
        self.parent.btn_merge.setStyleSheet(MergerUIStyle.BUTTON_MERGE)
        self.parent.btn_back.setStyleSheet(MergerUIStyle.BUTTON_TOOL)
        self.parent.progress_bar.setStyleSheet(MergerUIStyle.PROGRESS_BAR)
        if hasattr(self.parent, "btn_cancel_merge"):
            self.parent.btn_cancel_merge.setStyleSheet(MergerUIStyle.BUTTON_CANCEL)
        for b in [self.parent.btn_add, self.parent.btn_add_folder, self.parent.btn_undo, self.parent.btn_redo]:
            b.setStyleSheet(MergerUIStyle.BUTTON_STANDARD)
            b.setCursor(Qt.PointingHandCursor)
        for b in [self.parent.btn_remove, self.parent.btn_clear]:
            b.setStyleSheet(MergerUIStyle.BUTTON_DANGER)
            b.setCursor(Qt.PointingHandCursor)
        self.parent.btn_up.setStyleSheet(MergerUIStyle.BUTTON_ARROW)
        self.parent.btn_up.setCursor(Qt.PointingHandCursor)
        self.parent.btn_down.setStyleSheet(MergerUIStyle.BUTTON_ARROW)
        self.parent.btn_down.setCursor(Qt.PointingHandCursor)
        self.parent.btn_merge.setCursor(Qt.PointingHandCursor)
        self.parent.btn_back.setCursor(Qt.PointingHandCursor)
        if hasattr(self.parent, "btn_cancel_merge"):
            self.parent.btn_cancel_merge.setCursor(Qt.PointingHandCursor)
