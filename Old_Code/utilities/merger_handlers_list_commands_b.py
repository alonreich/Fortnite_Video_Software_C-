import os
from PyQt5.QtWidgets import QUndoCommand
from PyQt5.QtCore import Qt

class AddCommand(QUndoCommand):
    def __init__(self, parent, items_data, list_widget):
        super().__init__(f"Add {len(items_data)} videos")
        self.parent = parent
        self.items_data = items_data
        self.list_widget = list_widget

    def redo(self):
        for entry in self.items_data:
            path = entry["path"]
            self.parent.event_handler._add_single_item_internal(
                path,
                row=entry.get("row"),
                probe_data=entry.get("probe_data"),
                f_hash=entry.get("f_hash"),
                clip_id=entry.get("clip_id"),
                refresh=False
            )
            self.parent.logger.info(f"LIST: Added file '{os.path.basename(path)}' at row {entry.get('row')}")
        self.parent.event_handler.refresh_ranks()

    def undo(self):
        for entry in self.items_data:
            clip_id = entry.get("clip_id")
            for i in range(self.list_widget.count()):
                it = self.list_widget.item(i)
                if it.data(Qt.UserRole + 3) == clip_id:
                    self.list_widget.takeItem(i)
                    self.parent.logger.info(f"UNDO: Removed file '{os.path.basename(entry.get('path'))}'")
                    break
        self.parent.event_handler.refresh_ranks()

class RemoveCommand(QUndoCommand):
    def __init__(self, parent, items, list_widget):
        super().__init__(f"Remove {len(items)} videos")
        self.parent = parent
        self.items_data = []
        self.list_widget = list_widget
        self.items_data = sorted(
            [(list_widget.row(it), it.data(Qt.UserRole), it.data(Qt.UserRole + 1), it.data(Qt.UserRole + 2), it.data(Qt.UserRole + 3)) for it in items], 
            key=lambda x: x[0], reverse=True
        )

    def redo(self):
        for row, path, _, _, clip_id in self.items_data:
            for i in range(self.list_widget.count()):
                it = self.list_widget.item(i)
                if it.data(Qt.UserRole + 3) == clip_id:
                    self.list_widget.takeItem(i)
                    self.parent.logger.info(f"LIST: Removed file '{os.path.basename(path)}' from row {row}")
                    break
        self.parent.event_handler.refresh_ranks()

    def undo(self):
        for row, path, probe_data, f_hash, clip_id in reversed(self.items_data):
            self.parent.event_handler._add_single_item_internal(path, row, probe_data, f_hash, clip_id=clip_id, refresh=False)
            self.parent.logger.info(f"UNDO: Restored file '{os.path.basename(path)}' to row {row}")
        self.parent.event_handler.refresh_ranks()

class ClearCommand(QUndoCommand):
    def __init__(self, parent, items_data, list_widget):
        super().__init__(f"Clear {len(items_data)} videos")
        self.parent = parent
        self.items_data = items_data
        self.list_widget = list_widget

    def redo(self):
        self.list_widget.clear()
        self.parent.logger.info(f"LIST: Cleared all {len(self.items_data)} items from list")

    def undo(self):
        for entry in self.items_data:
            path = entry["path"]
            self.parent.event_handler._add_single_item_internal(
                path,
                row=entry.get("row"),
                probe_data=entry.get("probe_data"),
                f_hash=entry.get("f_hash"),
                clip_id=entry.get("clip_id"),
                refresh=False
            )
        self.parent.logger.info(f"UNDO: Restored {len(self.items_data)} items to list")
        self.parent.event_handler.refresh_ranks()
