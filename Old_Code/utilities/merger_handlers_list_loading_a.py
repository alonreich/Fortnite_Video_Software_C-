from pathlib import Path
from PyQt5.QtWidgets import QFileDialog, QMessageBox
from utilities.workers import FolderScanWorker
from utilities.merger_handlers_list_helpers import _natural_key

class MergerHandlersListLoadingSourceMixin:
    def add_videos(self):
        if self.parent.is_processing:
            return
        if self._loading_lock:
            self.parent.set_status_message("Still loading previous files... Please wait.", "color: #ffa500;", 1800, force=True)
            return
        self.parent.logger.info("USER: Clicked ADD VIDEOS")
        start_dir = self.parent.logic_handler.get_last_dir()
        files, _ = QFileDialog.getOpenFileNames(
            self.parent, "Select videos to merge", start_dir,
            "Videos (*.mp4 *.mov *.mkv *.m4v *.ts *.avi *.webm);;All Files (*)"
        )
        if not files:
            self.parent.logger.info("USER: Cancelled file selection")
            return
        self.parent.logger.info(f"USER: Selected {len(files)} files to add")
        self._start_file_loader(files)

    def add_folder(self):
        if self.parent.is_processing:
            return
        if self._loading_lock:
            self.parent.set_status_message("Still loading previous files... Please wait.", "color: #ffa500;", 1800, force=True)
            return
        self.parent.logger.info("USER: Clicked ADD FOLDER")
        start_dir = self.parent.logic_handler.get_last_dir()
        folder = QFileDialog.getExistingDirectory(self.parent, "Select Folder of Videos", start_dir)
        if not folder:
            self.parent.logger.info("USER: Cancelled folder selection")
            return
        self.parent.logger.info(f"USER: Selected folder '{folder}'")
        exts = {'.mp4', '.mov', '.mkv', '.m4v', '.ts', '.avi', '.webm'}
        self.parent.set_status_message("Scanning folder for videos...", "color: #7289da;", force=True)
        self._folder_scan_worker = FolderScanWorker(folder, exts)
        self._folder_scan_worker.finished.connect(self._on_folder_scan_finished)
        self._folder_scan_worker.start()

    def _on_folder_scan_finished(self, files, err):
        if err:
            self.parent.logger.error(f"ERROR: Failed to read folder: {err}")
            QMessageBox.warning(self.parent, "Scan Error", "Could not scan folder contents.")
            self.parent.set_status_message("Folder scan failed.", "color: #ff6b6b;", 2500, force=True)
            return
        if not files:
            self.parent.logger.info("USER: No valid videos found in folder")
            QMessageBox.information(self.parent, "No Videos", "No video files found in that folder.")
            self.parent.set_status_message("No videos found in selected folder.", "color: #ffa500;", 1800, force=True)
            return
        files = sorted(files, key=_natural_key)
        self.parent.logger.info(f"USER: Found {len(files)} videos in folder (recursive smart add)")
        self._start_file_loader(files)

    def add_videos_from_list(self, files):
        if self.parent.is_processing:
            return
        if self._loading_lock:
            self.parent.set_status_message("Still loading previous files... Please wait.", "color: #ffa500;", 1800, force=True)
            return
        if not files: return
        self._start_file_loader(files)
