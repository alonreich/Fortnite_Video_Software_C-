import os
from PyQt5.QtCore import Qt
from utilities.merger_handlers_list_commands_a import ReorderCommand, BatchMoveCommand
from utilities.merger_handlers_list_commands_b import RemoveCommand, ClearCommand

class MergerHandlersListEditMixin:
    def remove_selected(self):
        if self.parent.is_processing:
            return
        selected = self.parent.listw.selectedItems()
        if not selected: return
        self.parent.logger.info(f"USER: Clicked REMOVE SELECTED ({len(selected)} items selected)")
        cmd = RemoveCommand(self.parent, selected, self.parent.listw)
        self.undo_stack.push(cmd)
        self.parent.event_handler.update_button_states()
        self.parent.set_status_message(f"Removed {len(selected)}", "color: #e74c3c;", 2000, force=True)
        self.parent.logic_handler.request_save_config()

    def clear_all(self):
        if self.parent.is_processing:
            return
        if self.parent.listw.count() == 0: return
        self.parent.logger.info("USER: Clicked CLEAR ALL")
        items_data = []
        for i in range(self.parent.listw.count()):
            it = self.parent.listw.item(i)
            items_data.append({
                "row": i,
                "path": it.data(Qt.UserRole),
                "probe_data": it.data(Qt.UserRole + 1),
                "f_hash": it.data(Qt.UserRole + 2),
                "clip_id": it.data(Qt.UserRole + 3),
            })
        self.undo_stack.push(ClearCommand(self.parent, items_data, self.parent.listw))
        self.parent.set_status_message("List cleared", "color: #e74c3c;", 2000, force=True)
        self.parent.logic_handler.request_save_config()

    def move_item(self, direction: int):
        if self.parent.is_processing:
            return
        sel = self.parent.listw.selectedItems()
        if not sel: return
        dir_name = "UP" if direction < 0 else "DOWN"
        self.parent.logger.info(f"USER: Clicked MOVE {dir_name} ({len(sel)} items selected)")
        rows = sorted([self.parent.listw.row(i) for i in sel])
        if not rows: return
        before = self._snapshot_order()
        if direction < 0 and rows[0] == 0:
            return
        if direction > 0 and rows[-1] == self.parent.listw.count() - 1:
            return
        selected_set = set(rows)
        order = list(range(self.parent.listw.count()))
        if direction < 0:
            for r in rows:
                order[r - 1], order[r] = order[r], order[r - 1]
        else:
            for r in reversed(rows):
                order[r + 1], order[r] = order[r], order[r + 1]
        by_index = {i: before[i] for i in range(len(before))}
        after = [by_index[idx] for idx in order]
        self.undo_stack.push(BatchMoveCommand(self.parent, before, after))
        self.parent.set_status_message(f"Moved {len(selected_set)} item(s)", "color: #7289da;", 1200, force=True)

    def on_selection_changed(self):
        self.parent.event_handler.update_button_states()

    def on_drag_completed(self, start_row, end_row, path, tag):
        if start_row == end_row: return
        self.parent.logger.info(f"USER: Drag reordered '{os.path.basename(path)}' from row {start_row} to {end_row}")
        if hasattr(self.parent.logic_handler, "ensure_item_widgets_consistent"):
            self.parent.logic_handler.ensure_item_widgets_consistent()
        self.parent.logic_handler.request_save_config()
        self.parent.event_handler.update_button_states()
        self.parent.set_status_message("Order updated", "color: #7289da;", 1200, force=True)

    def on_drag_started(self, *_):
        self._order_before_drag = self._snapshot_order()

    def on_rows_moved(self, sourceParent, sourceStart, sourceEnd, destinationParent, destinationRow):
        """Capture drag-reorder as a single undoable transaction."""
        if self._is_replaying_undo:
            return
        if hasattr(self.parent.logic_handler, "ensure_item_widgets_consistent"):
            self.parent.logic_handler.ensure_item_widgets_consistent()
        before = self._order_before_drag or []
        after = self._snapshot_order()
        if before and before != after:
            self.undo_stack.push(ReorderCommand(self.parent, before, after))
        self._order_before_drag = None
        self.parent.logic_handler.request_save_config()
        self.parent.event_handler.update_button_states()
