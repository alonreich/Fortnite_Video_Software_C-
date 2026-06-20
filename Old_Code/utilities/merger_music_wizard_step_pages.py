import os
from PyQt5.QtCore import Qt, QThread, pyqtSignal, QTimer
from PyQt5.QtWidgets import QWidget, QVBoxLayout, QLabel, QProgressBar, QHBoxLayout, QLineEdit, QListWidgetItem, QFrame, QPushButton, QStackedLayout, QSizePolicy, QStyle
from utilities.merger_trimmed_slider import TrimmedSlider
from utilities.merger_music_wizard_widgets import SearchableListWidget, MusicItemWidget

class TrackScannerWorker(QThread):
    """Background worker for scanning MP3 files without UI freeze."""
    scanning_started = pyqtSignal()
    scanning_finished = pyqtSignal(list)
    scanning_error = pyqtSignal(str)

    def __init__(self, folder_path):
        super().__init__()
        self.folder_path = folder_path
        self._running = True

    def stop(self):
        self._running = False
        self.quit()
        if not self.wait(300):
            self.terminate()

    def run(self):
        try:
            self.scanning_started.emit()
            if not os.path.isdir(self.folder_path):
                self.scanning_error.emit(f"Folder not found: {self.folder_path}")
                return
            files = []
            for f in os.listdir(self.folder_path):
                if not self._running:
                    return
                if f.lower().endswith(".mp3"):
                    full_path = os.path.join(self.folder_path, f)
                    files.append((f, full_path))
            if self._running:
                files.sort(key=lambda x: x[0].lower())
                self.scanning_finished.emit(files)
        except Exception as e:
            if self._running:
                self.scanning_error.emit(str(e))

class MergerMusicWizardStepPagesMixin:
    def load_tracks(self, folder_path):
        """Scans the folder for MP3 files in background without UI freeze."""
        if not os.path.isdir(folder_path):
            self.logger.warning(f"WIZARD: MP3 folder not found: {folder_path}")
            return
        self._stop_track_scanner()
        self.track_list.clear()
        self.coverage_progress.setRange(0, 0)
        self.coverage_progress.setFormat("Scanning folder...")
        self._track_scanner = TrackScannerWorker(folder_path)
        self._track_scanner.scanning_started.connect(self._on_scanning_started)
        self._track_scanner.scanning_finished.connect(self._on_scanning_finished)
        self._track_scanner.scanning_error.connect(self._on_scanning_error)
        self._track_scanner.start()

    def _stop_track_scanner(self):
        """Safely stop and disconnect the track scanner worker."""
        if not hasattr(self, '_track_scanner') or not self._track_scanner:
            return
        try:
            self._track_scanner.scanning_started.disconnect()
            self._track_scanner.scanning_finished.disconnect()
            self._track_scanner.scanning_error.disconnect()
        except Exception:
            pass
        try:
            if self._track_scanner.isRunning():
                self._track_scanner.stop()
                self._track_scanner.wait(500)
        except Exception:
            pass
        self._track_scanner = None

    def _on_scanning_started(self):
        self.logger.info("WIZARD: Scanning MP3 folder in background...")

    def _on_scanning_finished(self, files):
        """files: list of (filename, full_path)"""
        self.coverage_progress.setRange(0, 100)
        self.coverage_progress.setFormat("%p%")
        self.track_list.clear()
        for filename, full_path in files:
            item = QListWidgetItem(self.track_list)
            custom_widget = MusicItemWidget(filename)
            item.setSizeHint(custom_widget.sizeHint())
            item.setData(Qt.UserRole, full_path)
            self.track_list.addItem(item)
            self.track_list.setItemWidget(item, custom_widget)
        self.logger.info(f"WIZARD: Loaded {len(files)} tracks from folder")
        self._report_non_mp3_files()

    def _on_scanning_error(self, error_msg):
        self.coverage_progress.setRange(0, 100)
        self.coverage_progress.setFormat("%p%")
        self.logger.error(f"WIZARD: Scanning error: {error_msg}")

    def _report_non_mp3_files(self):
        """Count and log non-MP3 audio files in the folder."""
        if not hasattr(self, '_track_scanner') or not self._track_scanner:
            return
        folder = self._track_scanner.folder_path
        try:
            import glob
            all_files = glob.glob(os.path.join(folder, "*"))
            audio_exts = {'.wav', '.flac', '.aac', '.m4a', '.ogg', '.wma'}
            non_mp3 = [f for f in all_files if os.path.splitext(f)[1].lower() in audio_exts]
            if non_mp3:
                self.logger.info(f"WIZARD: {len(non_mp3)} non‑MP3 audio files ignored")
                if hasattr(self, 'search_hint_lbl'):
                    self.search_hint_lbl.setText(f"{len(non_mp3)} non‑MP3 files ignored")
                    QTimer.singleShot(3000, lambda: self.search_hint_lbl.setText(""))
        except Exception:
            pass

    def _on_search_buffer_changed(self, buffer_text):
        """Update search hint label with auto‑hide timer."""
        if not hasattr(self, 'search_hint_lbl'):
            return
        if buffer_text:
            self.search_hint_lbl.setText(f"Searching: '{buffer_text}'")
            if hasattr(self, '_search_hint_timer'):
                self._search_hint_timer.stop()
            self._search_hint_timer = QTimer()
            self._search_hint_timer.setSingleShot(True)
            self._search_hint_timer.timeout.connect(lambda: self.search_hint_lbl.setText(""))
            self._search_hint_timer.start(3000)
        else:
            self.search_hint_lbl.setText("")

    def setup_step1_select(self):
        from ui.styles import UIStyles
        page = QWidget()
        layout = QVBoxLayout(page)
        layout.setSpacing(0)
        layout.setContentsMargins(0, 0, 0, 0)
        header_layout = QHBoxLayout()
        header_layout.setContentsMargins(0, 0, 0, 0)
        left_balance_spacer = QWidget()
        left_balance_spacer.setObjectName("headerLeftSpacer")
        left_balance_spacer.setFixedWidth(185) 
        left_balance_spacer.setSizePolicy(QSizePolicy.Fixed, QSizePolicy.Fixed)
        header_layout.addWidget(left_balance_spacer)
        header_layout.addStretch(1)
        step1_container = QVBoxLayout()
        step1_container.addSpacing(10)
        self.lbl_step1 = QLabel("STEP 1: Pick a song from your folder")
        self.lbl_step1.setStyleSheet("font-size: 20px; font-weight: bold; color: #7DD3FC; padding: 0px; margin: 0px;")
        self.lbl_step1.setAlignment(Qt.AlignCenter)
        self.lbl_step1.setFixedHeight(40)
        self.lbl_step1.setSizePolicy(QSizePolicy.Expanding, QSizePolicy.Fixed)
        step1_container.addWidget(self.lbl_step1)
        header_layout.addLayout(step1_container)
        header_layout.addStretch(1)
        self.select_folder_btn = QPushButton()
        self.select_folder_btn.setObjectName("selectFolderBtn")
        self.select_folder_btn.setFixedSize(100, 42)
        self.select_folder_btn.setSizePolicy(QSizePolicy.Fixed, QSizePolicy.Fixed)
        self.select_folder_btn.setCursor(Qt.PointingHandCursor)
        self.select_folder_btn.setStyleSheet(UIStyles.BUTTON_WIZARD_BLUE + " QPushButton#selectFolderBtn { min-width: 185px; }")
        self.select_folder_btn.clicked.connect(lambda: self.parent_window._on_select_music_folder(self))
        btn_layout = QHBoxLayout(self.select_folder_btn)
        btn_layout.setContentsMargins(0, 0, 0, 0)
        btn_layout.setSpacing(2)
        gear_l = QLabel("⚙️")
        gear_l.setStyleSheet("font-size: 13px; color: white; background: transparent; border: none; padding: 0;")
        gear_l.setAttribute(Qt.WA_TransparentForMouseEvents)
        txt_lbl = QLabel(" SELECT MUSIC FOLDER ")
        txt_lbl.setStyleSheet("font-size: 11px; color: white; font-weight: bold; background: transparent; border: none; padding: 0;")
        txt_lbl.setAttribute(Qt.WA_TransparentForMouseEvents)
        gear_r = QLabel("⚙️")
        gear_r.setStyleSheet("font-size: 13px; color: white; background: transparent; border: none; padding: 0;")
        gear_r.setAttribute(Qt.WA_TransparentForMouseEvents)
        btn_layout.addStretch()
        btn_layout.addWidget(gear_l)
        btn_layout.addWidget(txt_lbl)
        btn_layout.addWidget(gear_r)
        btn_layout.addStretch()
        header_layout.addWidget(self.select_folder_btn)
        layout.addLayout(header_layout)
        layout.addSpacing(10)
        self.coverage_progress = QProgressBar()
        self.coverage_progress.setFixedHeight(25)
        self.coverage_progress.setStyleSheet("""
            QProgressBar {
                background: #1f3545;
                border: 1px solid #34495e;
                border-radius: 6px;
                text-align: center;
                color: white;
                font-weight: bold;
            }
            QProgressBar::chunk {
                background: qlineargradient(x1:0, y1:0, x2:1, y2:0, stop:0 #3498db, stop:1 #2ecc71);
                border-radius: 5px;
            }
        """)
        layout.addWidget(self.coverage_progress)
        layout.addSpacing(15)
        search_layout = QHBoxLayout()
        search_layout.setSpacing(10)
        search_icon = QLabel("🔍")
        search_icon.setStyleSheet("font-size: 18px; background: transparent;")
        self.search_input = QLineEdit()
        self.search_input.setPlaceholderText("Search songs by name...")
        self.search_input.textChanged.connect(self._on_search_changed)
        search_layout.addWidget(search_icon)
        search_layout.addWidget(self.search_input)
        self.search_hint_lbl = QLabel("")
        self.search_hint_lbl.setStyleSheet("color: #7DD3FC; font-weight: bold; font-style: italic; font-size: 11px;")
        search_layout.addWidget(self.search_hint_lbl)
        layout.addLayout(search_layout)
        layout.addSpacing(10)
        self.track_list = SearchableListWidget()
        self.track_list.buffer_changed.connect(self._on_search_buffer_changed)
        self.track_list.setStyleSheet("""
            QListWidget {
                background: #142d37;
                border: 2px solid #1f3545;
                border-radius: 12px;
                outline: none;
            }
            QListWidget::item {
                background: transparent;
                border: none;
                padding: 0px;
                margin: 0px;
            }
            QListWidget::item:selected {
                background: #1a5276;
                border-radius: 4px;
            }
            QScrollBar:vertical {
                width: 22px;
                background: #142d37;
                border: 1px solid #1f3545;
                border-radius: 10px;
                margin: 2px;
            }
            QScrollBar::handle:vertical {
                min-height: 34px;
                border-radius: 9px;
                border: 1px solid #b8c0c8;
                background: qlineargradient(
                    x1:0, y1:0, x2:1, y2:0,
                    stop:0 #c9d0d6,
                    stop:0.5 #e1e6eb,
                    stop:1 #b6bec6
                );
            }
            QScrollBar::handle:vertical:hover {
                border: 1px solid #d9e0e6;
                background: qlineargradient(
                    x1:0, y1:0, x2:1, y2:0,
                    stop:0 #d7dde2,
                    stop:0.5 #edf1f5,
                    stop:1 #c6cdd4
                );
            }
            QScrollBar::add-line:vertical, QScrollBar::sub-line:vertical {
                height: 0px;
            }
            QScrollBar::add-page:vertical, QScrollBar::sub-page:vertical {
                background: transparent;
            }
        """)
        self.track_list.itemDoubleClicked.connect(self.go_to_offset_step)
        layout.addWidget(self.track_list)
        layout.addSpacing(10)
        self.stack.addWidget(page)

    def setup_step2_offset(self):
        page = QWidget()
        layout = QVBoxLayout(page)
        layout.setSpacing(0)
        layout.setContentsMargins(0, 0, 0, 0)
        layout.addSpacing(10)
        self.lbl_step2 = QLabel("STEP 2: Choose music custom starting point in seconds")
        self.lbl_step2.setStyleSheet("font-size: 20px; font-weight: bold; color: #7DD3FC; margin: 0px; padding: 0px;")
        self.lbl_step2.setAlignment(Qt.AlignCenter)
        self.lbl_step2.setFixedHeight(30)
        layout.addWidget(self.lbl_step2)
        layout.addSpacing(10)
        self.wave_container = QFrame()
        self.wave_container.setObjectName("waveContainer")
        self.wave_container.setFixedHeight(270)
        self.wave_container.setStyleSheet("#waveContainer { background: #000; border: 3px solid #266b89; border-radius: 12px; }")
        container_layout = QVBoxLayout(self.wave_container)
        container_layout.setContentsMargins(0, 0, 0, 0)
        container_layout.setSpacing(0)
        self.wave_preview = QLabel("Visualizing audio...")
        self.wave_preview.setStyleSheet("background: transparent; border: none;")
        self.wave_preview.setAlignment(Qt.AlignCenter)
        self.wave_preview.installEventFilter(self)
        container_layout.addWidget(self.wave_preview)
        layout.addWidget(self.wave_container)
        self.slider_unified_container = QWidget()
        self.slider_unified_container.setFixedHeight(100)
        self.slider_unified_layout = QVBoxLayout(self.slider_unified_container)
        self.slider_unified_layout.setContentsMargins(0, 0, 0, 0)
        self.slider_unified_layout.setSpacing(0)
        self.offset_slider = TrimmedSlider()
        self.offset_slider.is_wizard = True
        self.offset_slider.setFixedHeight(100)
        self.offset_slider.setProperty("is_wizard_slider", True)
        self.offset_slider.setFixedHeight(100)
        self.offset_slider.valueChanged.connect(self._on_slider_seek)
        self.offset_slider.sliderMoved.connect(self._on_slider_seek)
        try:
            self.offset_slider.sliderPressed.connect(self._on_drag_start)
            self.offset_slider.sliderReleased.connect(self._on_drag_end)
        except Exception as ex:
            self.logger.debug("WIZARD: slider drag signal hookup skipped: %s", ex)
        self.slider_unified_layout.addWidget(self.offset_slider)
        layout.addWidget(self.slider_unified_container)
        layout.addStretch(1)
        self._wave_caret = QLabel(self)
        self._wave_caret.setStyleSheet("background: rgba(46, 204, 113, 210); border: none;")
        self._wave_caret.setFixedWidth(1)
        self._wave_caret.setAttribute(Qt.WA_TransparentForMouseEvents, True)
        self._wave_caret.hide()
        badge_style = "background: rgba(52, 152, 219, 220); color: white; border-radius: 4px; padding: 2px 6px; font-weight: bold; font-size: 11px; border: none;"
        self._wave_time_badge = QLabel(self)
        self._wave_time_badge.setStyleSheet(badge_style)
        self._wave_time_badge.setAlignment(Qt.AlignCenter)
        self._wave_time_badge.setAttribute(Qt.WA_TransparentForMouseEvents, True)
        self._wave_time_badge.hide()
        self._wave_time_badge_bottom = QLabel(self)
        self._wave_time_badge_bottom.setStyleSheet(badge_style)
        self._wave_time_badge_bottom.setAlignment(Qt.AlignCenter)
        self._wave_time_badge_bottom.setAttribute(Qt.WA_TransparentForMouseEvents, True)
        self._wave_time_badge_bottom.hide()
        self.stack.addWidget(page)
