import os
import uuid
from PyQt5.QtWidgets import QMenu, QAction
from PyQt5.QtCore import Qt, QPoint, QUrl
from PyQt5.QtGui import QDesktopServices
from utilities.merger_handlers_list_commands_b import AddCommand

class MergerHandlersListMenuMixin:
    def show_context_menu(self, pos: QPoint):
        item = self.parent.listw.itemAt(pos)
        menu = QMenu(self.parent)
        selected = self.parent.listw.selectedItems()
        add_action = QAction("Add Videos...", self.parent)
        add_action.triggered.connect(self.add_videos)
        menu.addAction(add_action)
        add_folder_action = QAction("Smart Add Folder (Recursive)", self.parent)
        add_folder_action.triggered.connect(self.add_folder)
        menu.addAction(add_folder_action)
        quick_smart_add = QAction("Smart Add & Auto-Merge Prep", self.parent)
        quick_smart_add.triggered.connect(self.smart_add_and_prepare)
        menu.addAction(quick_smart_add)
        if item:
            if not item.isSelected():
                self.parent.listw.setCurrentItem(item)
            path = item.data(Qt.UserRole)
            open_action = QAction("Open in Default Player", self.parent)
            open_action.triggered.connect(lambda: QDesktopServices.openUrl(QUrl.fromLocalFile(path)))
            menu.addAction(open_action)
            dup_action = QAction("Duplicate Item", self.parent)
            dup_action.triggered.connect(lambda: self.duplicate_item(item))
            menu.addAction(dup_action)
            remove_action = QAction("Remove Selected", self.parent)
            remove_action.triggered.connect(self.remove_selected)
            menu.addAction(remove_action)
            move_up_action = QAction("Move Up", self.parent)
            move_up_action.triggered.connect(lambda: self.move_item(-1))
            menu.addAction(move_up_action)
            move_down_action = QAction("Move Down", self.parent)
            move_down_action.triggered.connect(lambda: self.move_item(1))
            menu.addAction(move_down_action)
        menu.addSeparator()
        merge_now_action = QAction("Merge Now", self.parent)
        merge_now_action.setEnabled(self.parent.listw.count() >= 1 and not self.parent.is_processing)
        merge_now_action.triggered.connect(self.parent.on_merge_clicked)
        menu.addAction(merge_now_action)
        if selected:
            remove_quick = QAction(f"Quick Remove ({len(selected)})", self.parent)
            remove_quick.triggered.connect(self.remove_selected)
            menu.addAction(remove_quick)
        undo_action = self.undo_stack.createUndoAction(self.parent)
        redo_action = self.undo_stack.createRedoAction(self.parent)
        menu.addAction(undo_action)
        menu.addAction(redo_action)
        menu.exec_(self.parent.listw.mapToGlobal(pos))

    def duplicate_item(self, item):
        path = item.data(Qt.UserRole)
        probe_data = item.data(Qt.UserRole + 1)
        f_hash = item.data(Qt.UserRole + 2)
        row = self.parent.listw.row(item) + 1
        entry = {
            "path": path,
            "row": row,
            "probe_data": probe_data,
            "f_hash": f_hash,
            "clip_id": uuid.uuid4().hex,
        }
        self.undo_stack.push(AddCommand(self.parent, [entry], self.parent.listw))

    def smart_add_and_prepare(self):
        """One-shot action: add videos and proactively prepare merge prerequisites."""
        if self._loading_lock:
            self.parent.set_status_message("Still loading previous files... Please wait.", "color: #ffa500;", 1800, force=True)
            return
        self._pending_smart_prepare = True
        self.add_videos()

    def on_merge_clicked(self):
        self.parent.logger.info("USER: Clicked MERGE VIDEOS")
        self.parent.start_merge_processing()
