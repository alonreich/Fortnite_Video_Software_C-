from pathlib import Path
import uuid
from PyQt5.QtWidgets import QMessageBox
from PyQt5.QtCore import Qt
from utilities.workers import FastFileLoaderWorker
from utilities.merger_handlers_list_commands_b import AddCommand

class MergerHandlersListLoadingWorkerMixin:
    def _start_file_loader(self, files):
        current_count = self.parent.listw.count()
        current_files = set()
        if current_count > 0:
             for i in range(current_count):
                it = self.parent.listw.item(i)
                current_files.add(it.data(Qt.UserRole))
        room = max(0, int(self.parent.MAX_FILES - current_count))
        unique_candidates = []
        seen = set(current_files)
        for f in files:
            if f in seen:
                continue
            seen.add(f)
            unique_candidates.append(f)
        if not unique_candidates:
            self.parent.set_status_message("All selected files are already in the list.", "color: #ffa500;", 2200, force=True)
            return
        if len(unique_candidates) > room:
             kept = unique_candidates[:room]
             skipped = len(unique_candidates) - len(kept)
             QMessageBox.information(
                 self.parent,
                 "List limit reached",
                 f"Only {len(kept)} more file(s) can be added (limit {self.parent.MAX_FILES}).\n"
                 f"Skipped {skipped} extra file(s).",
             )
             files = kept
        else:
             files = unique_candidates
        self._pending_undo_items = []
        self._loading_progress_total = max(1, len(files))
        self._loading_lock = True
        self.parent.set_ui_busy(True)
        self.parent.set_status_message("Loading files...", "color: #ffa500;", force=True)
        existing_hashes = set()
        if current_count > 0:
             for i in range(current_count):
                it = self.parent.listw.item(i)
                h = it.data(Qt.UserRole + 2)
                if h: existing_hashes.add(h)
        self._loader = FastFileLoaderWorker(files, current_files, existing_hashes, self.parent.MAX_FILES, self.parent.ffmpeg)
        self._loader.file_loaded.connect(self._on_file_loaded)
        self._loader.progress.connect(self._on_loader_progress)
        self._loader.finished.connect(self._on_loading_finished)
        self._loader.start()
        if files:
            self.parent.logic_handler.set_last_dir(str(Path(files[0]).parent))
            self.parent.logic_handler.request_save_config()

    def _on_loader_progress(self, current, total):
        self._loading_progress_total = max(1, int(total or 1))
        pct = int(round((float(current) / float(self._loading_progress_total)) * 100.0))
        pct = max(0, min(100, pct))
        self.parent.set_status_message(f"Loading files... {current}/{self._loading_progress_total} ({pct}%)", "color: #ffa500;", force=True)

    def _on_file_loaded(self, path, size, probe_data, f_hash):
        current_idx = self.parent.listw.count() + len(self._pending_undo_items)
        self._pending_undo_items.append({
            "path": path,
            "row": current_idx,
            "probe_data": probe_data,
            "f_hash": f_hash,
            "clip_id": uuid.uuid4().hex,
        })

    def _on_loading_finished(self, added, duplicates):
        self._loading_lock = False
        self.parent.set_ui_busy(False)
        msg = []
        if added: msg.append(f"Added {added}")
        if duplicates: msg.append(f"Skipped {duplicates} dupe(s)")
        self.parent.set_status_message(" | ".join(msg) if msg else "Done", "color: #ffa500;", 3000, force=True)
        if getattr(self, "_pending_undo_items", None):
            self.undo_stack.push(AddCommand(self.parent, list(self._pending_undo_items), self.parent.listw))
            self._pending_undo_items = []
        self._loading_progress_total = 0
        self.parent.event_handler.update_button_states()
        self.parent.logic_handler.request_save_config()
        if getattr(self, "_pending_smart_prepare", False):
            self._pending_smart_prepare = False
            if self.parent.listw.count() > 0:
                self.parent.estimate_total_duration_seconds()
                self.parent.set_status_message(
                    "Smart action complete: files queued and merge prerequisites precomputed.",
                    "color: #43b581;",
                    2000,
                    force=True,
                )
        if added:
            self.parent.set_status_message(f"Loaded {added} videos. Ready for merge.", "color: #43b581;", 2500, force=True)
