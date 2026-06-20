class MergerHandlersButtonsMixin:
    def update_button_states(self):
        n = self.parent.listw.count()
        is_processing = self.parent.is_processing
        selected_items = self.parent.listw.selectedItems()
        is_single_selection = len(selected_items) == 1
        self.parent.add_music_checkbox.setEnabled(n >= 1 and not is_processing)
        self.parent.btn_merge.setEnabled(n >= 1 and not is_processing)
        self.parent.btn_remove.setEnabled(bool(selected_items) and not is_processing)
        self.parent.btn_clear.setEnabled(n > 0 and not is_processing)
        self.parent.btn_add.setEnabled(not is_processing and n < self.parent.MAX_FILES)
        self.parent.btn_add_folder.setEnabled(not is_processing and n < self.parent.MAX_FILES)
        self.parent.btn_back.setEnabled(not is_processing)
        self.parent.listw.setEnabled(not is_processing)
        undo_enabled = False
        redo_enabled = False
        if hasattr(self, 'undo_stack') and self.undo_stack is not None:
            try:
                undo_enabled = (not is_processing) and self.undo_stack.canUndo()
                redo_enabled = (not is_processing) and self.undo_stack.canRedo()
            except RuntimeError:
                pass
        if hasattr(self.parent, "btn_undo"):
            self.parent.btn_undo.setEnabled(undo_enabled)
        if hasattr(self.parent, "btn_redo"):
            self.parent.btn_redo.setEnabled(redo_enabled)
        if is_single_selection and not is_processing:
            current_row = self.parent.listw.row(selected_items[0])
            self.parent.btn_up.setEnabled(current_row > 0)
            self.parent.btn_down.setEnabled(current_row < n - 1)
        else:
            self.parent.btn_up.setEnabled(False)
            self.parent.btn_down.setEnabled(False)
        if hasattr(self.parent, "is_status_locked") and self.parent.is_status_locked():
            return
        if is_processing:
            self.parent.set_status_message("Processing merge... Please wait.")
        elif n == 0:
            self.parent.set_status_message("Ready. Add 1 to 100 videos to begin.")
        else:
            total_hint = ""
            if hasattr(self.parent, "estimate_total_duration_text"):
                try:
                    t = self.parent.estimate_total_duration_text()
                    if t:
                        total_hint = f" Estimated length: {t}."
                except Exception:
                    total_hint = ""
            self.parent.set_status_message(f"Ready to merge {n} video{'s' if n != 1 else ''}.{total_hint}")
