from PyQt5.QtWidgets import QUndoCommand
from PyQt5.QtCore import Qt

class ReorderCommand(QUndoCommand):
    def __init__(self, parent, before_entries, after_entries):
        super().__init__("Reorder videos")
        self.parent = parent
        self.before_entries = list(before_entries)
        self.after_entries = list(after_entries)

    def _apply_order(self, entries):
        listw = self.parent.listw
        selected_ids = {
            listw.item(i).data(Qt.UserRole + 3)
            for i in range(listw.count())
            if listw.item(i).isSelected()
        }
        data_by_id = {}
        for i in range(listw.count()):
            it = listw.item(i)
            clip_id = it.data(Qt.UserRole + 3)
            p = it.data(Qt.UserRole)
            data_by_id[clip_id] = {
                "clip_id": clip_id,
                "path": p,
                "probe_data": it.data(Qt.UserRole + 1),
                "f_hash": it.data(Qt.UserRole + 2),
            }
        self.parent.event_handler._is_replaying_undo = True
        model = listw.model()
        model.blockSignals(True)
        try:
            listw.clear()
            for row, snap in enumerate(entries):
                entry = data_by_id.get(snap.get("clip_id"))
                if not entry:
                    continue
                self.parent.event_handler._add_single_item_internal(
                    entry.get("path"),
                    row=row,
                    probe_data=entry.get("probe_data"),
                    f_hash=entry.get("f_hash"),
                    clip_id=entry.get("clip_id"),
                    refresh=False
                )
            self.parent.event_handler.refresh_ranks()
            if selected_ids:
                listw.clearSelection()
                first_selected = None
                for i in range(listw.count()):
                    it = listw.item(i)
                    if it.data(Qt.UserRole + 3) in selected_ids:
                        it.setSelected(True)
                        if first_selected is None:
                            first_selected = it
                if first_selected is not None:
                    listw.setCurrentItem(first_selected)
        finally:
            model.blockSignals(False)
            self.parent.event_handler._is_replaying_undo = False
            try:
                self.parent.event_handler.update_button_states()
            except RuntimeError:
                pass

    def redo(self):
        self._apply_order(self.after_entries)

    def undo(self):
        self._apply_order(self.before_entries)

class BatchMoveCommand(QUndoCommand):
    def __init__(self, parent, before_entries, after_entries):
        super().__init__("Move selected videos")
        self.parent = parent
        self.before_entries = list(before_entries)
        self.after_entries = list(after_entries)

    def _apply(self, entries):
        listw = self.parent.listw
        selected_ids = {
            listw.item(i).data(Qt.UserRole + 3)
            for i in range(listw.count())
            if listw.item(i).isSelected()
        }
        model = listw.model()
        model.blockSignals(True)
        self.parent.event_handler._is_replaying_undo = True
        try:
            by_id = {}
            for i in range(listw.count()):
                it = listw.item(i)
                cid = it.data(Qt.UserRole + 3)
                by_id[cid] = {
                    "clip_id": cid,
                    "path": it.data(Qt.UserRole),
                    "probe_data": it.data(Qt.UserRole + 1),
                    "f_hash": it.data(Qt.UserRole + 2),
                }
            listw.clear()
            for row, snap in enumerate(entries):
                entry = by_id.get(snap.get("clip_id"))
                if not entry:
                    continue
                self.parent.event_handler._add_single_item_internal(
                    entry["path"],
                    row=row,
                    probe_data=entry.get("probe_data"),
                    f_hash=entry.get("f_hash"),
                    clip_id=entry.get("clip_id"),
                    refresh=False
                )
            self.parent.event_handler.refresh_ranks()
            if selected_ids:
                listw.clearSelection()
                first_selected = None
                for i in range(listw.count()):
                    it = listw.item(i)
                    if it.data(Qt.UserRole + 3) in selected_ids:
                        it.setSelected(True)
                        if first_selected is None:
                            first_selected = it
                if first_selected is not None:
                    listw.setCurrentItem(first_selected)
        finally:
            model.blockSignals(False)
            self.parent.event_handler._is_replaying_undo = False
            self.parent.event_handler.update_button_states()

    def redo(self):
        self._apply(self.after_entries)

    def undo(self):
        self._apply(self.before_entries)
