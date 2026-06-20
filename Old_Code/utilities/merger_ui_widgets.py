from PyQt5.QtWidgets import (
    QHBoxLayout, QVBoxLayout, QLabel, QPushButton, QSizePolicy, QWidget
)

from PyQt5.QtCore import Qt
from utilities.merger_unified_music_widget import UnifiedMusicWidget

class MergerUIWidgetsMixin:
    def create_center_buttons(self):
        row = QHBoxLayout()
        row.setContentsMargins(0, 0, 0, 0)
        row.setSpacing(10)
        self.parent.btn_undo = QPushButton("UNDO")
        self.parent.btn_undo.setObjectName("aux-btn")
        self.parent.btn_undo.setFixedSize(85, 32)
        self.parent.btn_undo.setCursor(Qt.PointingHandCursor)
        self.parent.btn_undo.setToolTip("Undo last action (Ctrl+Z)")
        self.parent.btn_redo = QPushButton("REDO")
        self.parent.btn_redo.setObjectName("aux-btn")
        self.parent.btn_redo.setFixedSize(85, 32)
        self.parent.btn_redo.setCursor(Qt.PointingHandCursor)
        self.parent.btn_redo.setToolTip("Redo last action (Ctrl+Y)")
        row.addWidget(self.parent.btn_undo)
        row.addWidget(self.parent.btn_redo)
        return row

    def create_quality_column(self):
        from ui.widgets.spinning_wheel_slider import SpinningWheelSlider
        self.parent.quality_slider = SpinningWheelSlider(self.parent)
        self.parent.quality_slider.setRange(0, 4)
        self.parent.quality_slider._labels = ["20%", "40%", "60%", "80%", "100%"]
        self.parent.quality_slider.setValue(4)
        self.parent.quality_slider.setEnabled(True)
        tooltips = {
            0: "20% Quality: Maximum compression. Results in very small file sizes, significantly smaller than all combined videos.",
            1: "40% Quality: High compression. Great for saving space while keeping the video watchable.",
            2: "60% Quality: Balanced. Good reduction in file size with decent visual clarity.",
            3: "80% Quality: High Quality. Slight reduction in quality, results in roughly 80% of original combined sizes.",
            4: "100% Quality: Original Quality. No extra compression, matches the combined original videos exactly."
        }
        
        def update_tooltip(val):
            self.parent.quality_slider.setToolTip(tooltips.get(val, ""))
        self.parent.quality_slider.valueChanged.connect(update_tooltip)
        update_tooltip(4)
        col = QVBoxLayout()
        col.setContentsMargins(0, 0, 0, 0)
        col.addWidget(self.parent.quality_slider, 0, Qt.AlignCenter)
        return col

    def create_merge_row(self):
        self.parent.btn_back = QPushButton("RETURN TO MAIN APP")
        self.parent.btn_back.setFixedSize(135, 42)
        self.parent.btn_back.setObjectName("returnButton")
        self.parent.btn_back.clicked.connect(self.parent.return_to_main_app)
        self.parent.btn_back.setCursor(Qt.PointingHandCursor)
        self.parent.btn_back.setToolTip("Exit merger and return to main application")
        self.parent.merge_row = QHBoxLayout()
        self.parent.merge_row.setContentsMargins(0, 0, 0, 0)
        self.parent.merge_row.setSpacing(20)
        
        merge_wrap = QWidget()
        merge_wrap.setLayout(self.parent.merge_row)
        merge_wrap.setSizePolicy(QSizePolicy.Preferred, QSizePolicy.Fixed)
        
        self.parent.btn_merge = QPushButton("MERGE VIDEOS")
        self.parent.btn_merge.setObjectName("mergeButton")
        self.parent.btn_merge.setFixedSize(210, 42)
        self.parent.btn_merge.setCursor(Qt.PointingHandCursor)
        self.parent.btn_merge.clicked.connect(self.parent.on_merge_clicked)
        self.parent.btn_merge.setToolTip("Start merging the video list (Ctrl+Enter)")
        
        # Dual-button cluster matching Main App
        from ui.styles import UIStyles
        self.parent.btn_cancel = QPushButton("CANCEL")
        self.parent.btn_cancel.setFixedSize(140, 42)
        self.parent.btn_cancel.setCursor(Qt.PointingHandCursor)
        self.parent.btn_cancel.setStyleSheet(UIStyles.BUTTON_CANCEL)
        self.parent.btn_cancel.clicked.connect(self.parent.cancel_processing)
        self.parent.btn_cancel.hide()
 
        self.parent.btn_processing = QPushButton("PROCESSING")
        self.parent.btn_processing.setFixedSize(140, 42)
        self.parent.btn_processing.setCursor(Qt.PointingHandCursor)
        self.parent.btn_processing.setStyleSheet(UIStyles.BUTTON_WIZARD_GREEN)
        self.parent.btn_processing.hide()
 
        self.parent.merge_row.addStretch(1)
        self.parent.merge_row.addWidget(self.parent.btn_merge)
        self.parent.merge_row.addWidget(self.parent.btn_cancel)
        self.parent.merge_row.addWidget(self.parent.btn_processing)
        self.parent.merge_row.addStretch(1)
        self.parent.merge_row.addWidget(self.parent.btn_back, 0, Qt.AlignCenter)
        self.parent.merge_row.addSpacing(60)
        return merge_wrap

    def create_music_layout(self):
        music_layout = QHBoxLayout()
        music_layout.setSpacing(15)
        self.parent.unified_music_widget = UnifiedMusicWidget(self.parent)
        music_layout.addWidget(self.parent.unified_music_widget)
        self.parent.add_music_checkbox = self.parent.unified_music_widget.toggle_button
        return music_layout
