import uuid
from PyQt5.QtCore import Qt
from PyQt5.QtWidgets import QListWidgetItem
from utilities.merger_handlers_list_helpers import _human_time

class MergerHandlersListItemBuildMixin:
    def _add_single_item_internal(self, path, row=None, probe_data=None, f_hash=None, clip_id=None, refresh=True):
        item = QListWidgetItem()
        item.setToolTip(path)
        item.setData(Qt.UserRole, path)
        item.setData(Qt.UserRole + 3, clip_id or uuid.uuid4().hex)
        if probe_data:
            item.setData(Qt.UserRole + 1, probe_data)
        if f_hash:
            item.setData(Qt.UserRole + 2, f_hash)
        current_count = self.parent.listw.count()
        rank = (row + 1) if row is not None else (current_count + 1)
        w = self.make_item_widget(path, rank=rank)
        if probe_data:
            try:
                dur = float(probe_data.get('format', {}).get('duration', 0))
                if dur > 0:
                    w.set_duration_label(_human_time(dur))
                streams = probe_data.get('streams', [])
                vid = next((s for s in streams if s.get('width')), None)
                if vid:
                    w.set_resolution_label(f"{vid['width']}x{vid['height']}")
            except Exception as ex:
                self.parent.logger.debug(f"Item metadata paint skipped: {ex}")

        from PyQt5.QtCore import QSize
        item.setSizeHint(QSize(w.width(), 50))
        if row is not None:
            self.parent.listw.insertItem(row, item)
            inserted_row = row
        else:
            self.parent.listw.addItem(item)
            inserted_row = self.parent.listw.count() - 1
        self.parent.listw.setItemWidget(item, w)
        if refresh:
            self.refresh_ranks()
        self.refresh_selection_highlights()
        return inserted_row
