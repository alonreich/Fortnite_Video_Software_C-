from PyQt5.QtWidgets import QUndoStack
from PyQt5.QtCore import Qt

class MergerHandlersListCoreMixin:
    def __init__(self):
        self.undo_stack = QUndoStack(self.parent)
        self.undo_stack.setUndoLimit(200)
        self._loading_lock = False
        self._pending_smart_prepare = False
        self._order_before_drag = None
        self._is_replaying_undo = False
        self._loading_progress_total = 0

    def _safe_update_button_states(self):
        """Safely update button states, catching RuntimeError if Qt objects are deleted."""
        try:
            self.parent.event_handler.update_button_states()
        except RuntimeError:
            pass

    def _snapshot_order(self):
        """Create a complete snapshot of all item data for undo/redo operations."""
        out = []
        for i in range(self.parent.listw.count()):
            it = self.parent.listw.item(i)
            out.append({
                "clip_id": it.data(Qt.UserRole + 3),
                "path": it.data(Qt.UserRole),
                "probe_data": it.data(Qt.UserRole + 1),
                "f_hash": it.data(Qt.UserRole + 2),
            })
        return out

    def setup_list_connections(self):
        self.parent.listw.setContextMenuPolicy(Qt.CustomContextMenu)
        self.parent.listw.customContextMenuRequested.connect(self.show_context_menu)

        from PyQt5.QtWidgets import QShortcut
        from PyQt5.QtGui import QKeySequence
        self.undo_shortcut = QShortcut(QKeySequence("Ctrl+Z"), self.parent)
        self.undo_shortcut.activated.connect(self.undo_stack.undo)
        self.redo_shortcut = QShortcut(QKeySequence("Ctrl+Y"), self.parent)
        self.redo_shortcut.activated.connect(self.undo_stack.redo)
        if hasattr(self.parent, "btn_undo"):
            self.parent.btn_undo.clicked.connect(self.undo_stack.undo)
        if hasattr(self.parent, "btn_redo"):
            self.parent.btn_redo.clicked.connect(self.undo_stack.redo)
        self.undo_stack.indexChanged.connect(lambda *_: self._safe_update_button_states())
        self.del_shortcut = QShortcut(QKeySequence("Delete"), self.parent)
        self.del_shortcut.activated.connect(self.remove_selected)
        self.sel_all_shortcut = QShortcut(QKeySequence("Ctrl+A"), self.parent)
        self.sel_all_shortcut.activated.connect(self.parent.listw.selectAll)
        self.esc_shortcut = QShortcut(QKeySequence("Esc"), self.parent)
        self.esc_shortcut.activated.connect(self.parent.listw.clearSelection)

    def refresh_ranks(self):
        """Update all ranking labels (#1, #2, etc.) based on current list order."""
        for i in range(self.parent.listw.count()):
            item = self.parent.listw.item(i)
            w = self.parent.listw.itemWidget(item)
            if w and hasattr(w, 'rank_label'):
                w.rank_label.setText(f"#{i + 1}")

    def refresh_selection_highlights(self):
        """Sync the visual 'marked' state of custom widgets with their list selection state."""
        listw = self.parent.listw
        for i in range(listw.count()):
            item = listw.item(i)
            w = listw.itemWidget(item)
            if w and hasattr(w, 'set_marked'):
                w.set_marked(item.isSelected())
