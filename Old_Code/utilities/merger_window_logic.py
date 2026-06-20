import os
from pathlib import Path
from PyQt5.QtCore import QByteArray
from PyQt5.QtCore import QPropertyAnimation, QEasingCurve, Qt, QTimer, QPoint, QRect
from PyQt5.QtWidgets import QLabel
from utilities.merger_utils import _load_conf, _save_conf

class MergerWindowLogic:
    def __init__(self, window):
        self.window = window
        self._save_timer = QTimer(self.window)
        self._save_timer.setSingleShot(True)
        self._save_timer.timeout.connect(self.save_config)

    def _is_widget_alive(self, widget):
        if not widget:
            return False
        try:
            _ = widget.isVisible()
            return True
        except RuntimeError:
            return False

    def load_config(self):
        self.window._cfg = _load_conf()
        self.window._last_dir = self.window._cfg.get("last_dir", str(Path.home() / "Downloads"))
        self.window._last_out_dir = self.window._cfg.get("last_out_dir", str(Path.home() / "Downloads"))
        self.window.logger.info(f"Loaded last_dir: {self.window._last_dir}")
        self.window.logger.info(f"Loaded last_out_dir: {self.window._last_out_dir}")
        try:
            qt_geom = self.window._cfg.get("qt_geometry")
            if isinstance(qt_geom, str) and qt_geom:
                try:
                    if self.window.restoreGeometry(QByteArray.fromBase64(qt_geom.encode("utf-8"))):
                        self.window.logger.info("Restored window geometry from qt_geometry")
                    else:
                        self.window.logger.debug("qt_geometry restore returned False; falling back")
                except Exception as ex:
                    self.window.logger.debug(f"qt_geometry restore failed: {ex}")
            g = self.window._cfg.get("geometry", {})

            from PyQt5.QtWidgets import QApplication
            screen_geo = QApplication.primaryScreen().availableGeometry()
            if (not qt_geom) and g and 'x' in g and 'y' in g:
                x = int(g.get("x", self.window.x()))
                y = int(g.get("y", self.window.y()))
                w = int(g.get("w", self.window.width()))
                h = int(g.get("h", self.window.height()))
                if not screen_geo.intersects(QRect(x, y, w, h)):
                    self._apply_default_center(screen_geo)
                    return
                self.window.move(x, y)
                self.window.resize(w, h)
                self.window.logger.info(f"Restored window geometry: {x},{y} {w}x{h}")
            elif not qt_geom:
                self._apply_default_center(screen_geo)
        except Exception as ex:
            self.window.logger.debug(f"Failed to restore window geometry: {ex}")
        try:
            music_state = self.window._cfg.get("music_widget", {})
            if hasattr(self.window, "unified_music_widget") and isinstance(music_state, dict):
                self.window.unified_music_widget.apply_state(music_state)
        except Exception as ex:
            self.window.logger.debug(f"Failed to restore music state: {ex}")

    def _apply_default_center(self, screen_geo):
        w, h = 1000, 700
        x = screen_geo.x() + (screen_geo.width() - w) // 2
        y = max(screen_geo.top(), screen_geo.y() + (screen_geo.height() - h) // 2 - 25)
        self.window.resize(w, h)
        self.window.move(x, y)
        self.window.logger.info(f"Centered window on first monitor: {x},{y}")

    def save_config(self):
        """Thread-safe configuration saving with atomic write."""
        try:
            config_copy = self.window._cfg.copy()
            try:
                config_copy["qt_geometry"] = bytes(self.window.saveGeometry().toBase64()).decode("utf-8")
            except Exception:
                pass
            config_copy["geometry"] = {
                "x": self.window.x(),
                "y": self.window.y(),
                "w": self.window.width(),
                "h": self.window.height()
            }
            config_copy["last_dir"] = self.window._last_dir
            config_copy["last_out_dir"] = self.window._last_out_dir
            if hasattr(self.window, "unified_music_widget"):
                config_copy["music_widget"] = self.window.unified_music_widget.export_state()
            _save_conf(config_copy)
            self.window._cfg = config_copy
        except Exception as err:
            self.window.logger.error("Error saving config in merger closeEvent: %s", err)

    def request_save_config(self, delay_ms: int = 300):
        """Debounced config save to avoid disk-write storms during rapid UI actions."""
        if delay_ms <= 0:
            self.save_config()
            return
        self._save_timer.start(int(delay_ms))

    def get_last_dir(self):
        return getattr(self.window, "_last_dir", str(Path.home()))

    def set_last_dir(self, path):
        self.window._last_dir = path

    def get_last_out_dir(self):
        return getattr(self.window, "_last_out_dir", str(Path.home()))

    def set_last_out_dir(self, path):
        self.window._last_out_dir = path

    def can_anim(self, row, new_row):
        if row == new_row or not (0 <= row < self.window.listw.count()) or not (0 <= new_row < self.window.listw.count()):
            return False
        if getattr(self.window, "_animating", False):
            return False
        w_row = self.window.listw.itemWidget(self.window.listw.item(row))
        w_new = self.window.listw.itemWidget(self.window.listw.item(new_row))
        if not self._is_widget_alive(w_row) or not self._is_widget_alive(w_new):
            return False
        return True

    def start_swap_animation(self, row, new_row):
        ghost1 = None
        ghost2 = None
        a1 = None
        a2 = None
        try:
            v = self.window.listw.viewport()
            it1, it2 = self.window.listw.item(row), self.window.listw.item(new_row)
            w1, w2 = self.window.listw.itemWidget(it1), self.window.listw.itemWidget(it2)
            if not self._is_widget_alive(w1) or not self._is_widget_alive(w2):
                return False
            if not w1.isVisible() or not w2.isVisible():
                self.window.listw.scrollToItem(it1)
                if not w1.isVisible() or not w2.isVisible():
                    self.perform_swap(row, new_row)
                    return True
            r1 = self.window.listw.visualItemRect(it1)
            r2 = self.window.listw.visualItemRect(it2)
            if r1.isNull() or r2.isNull():
                return False
            try:
                pm1 = w1.grab()
                pm2 = w2.grab()
            except RuntimeError:
                return False
            ghost1 = QLabel(v); ghost1.setPixmap(pm1); ghost1.setAttribute(Qt.WA_TransparentForMouseEvents, True)
            ghost1.setStyleSheet("background: transparent;")
            ghost2 = QLabel(v); ghost2.setPixmap(pm2); ghost2.setAttribute(Qt.WA_TransparentForMouseEvents, True)
            ghost2.setStyleSheet("background: transparent;")
            ghost1.move(r1.topLeft()); ghost1.show()
            ghost2.move(r2.topLeft()); ghost2.show()
            w1.setVisible(False); w2.setVisible(False)
            a1 = QPropertyAnimation(ghost1, b"pos", self.window); a1.setDuration(150)
            a2 = QPropertyAnimation(ghost2, b"pos", self.window); a2.setDuration(150)
            a1.setStartValue(r1.topLeft()); a1.setEndValue(r2.topLeft()); a1.setEasingCurve(QEasingCurve.InOutQuad)
            a2.setStartValue(r2.topLeft()); a2.setEndValue(r1.topLeft()); a2.setEasingCurve(QEasingCurve.InOutQuad)
            self.window._animating = True

            def _cleanup_animation():
                """Safe cleanup of animation resources."""
                try:
                    if w1: w1.setVisible(True)
                    if w2: w2.setVisible(True)
                    if ghost1: ghost1.deleteLater()
                    if ghost2: ghost2.deleteLater()
                    if a1: a1.deleteLater()
                    if a2: a2.deleteLater()
                except Exception as ex:
                    self.window.logger.debug(f"Error cleaning up animation: {ex}")
                finally:
                    self.window._animating = False

            def _finish():
                try:
                    self.perform_swap(row, new_row)
                except Exception as ex:
                    self.window.logger.debug(f"Error during swap animation finish: {ex}")
                finally:
                    _cleanup_animation()
            animations_completed = [False, False]
            
            def _on_animation_finished(anim_index):
                animations_completed[anim_index] = True
                if all(animations_completed):
                    _finish()
            a1.finished.connect(lambda: _on_animation_finished(0))
            a2.finished.connect(lambda: _on_animation_finished(1))
            try:
                a1.start()
                a2.start()
                return True
            except Exception as ex:
                self.window.logger.debug(f"Animation start failed: {ex}")
                _cleanup_animation()
                return False
        except Exception as ex:
            self.window.logger.debug(f"Animation setup failed: {ex}")
            if hasattr(self.window, '_animating'):
                self.window._animating = False
            return False

    def perform_swap(self, row, new_row):
        """Redirects swap to move, as swapping neighbors is effectively a move."""
        self.perform_move(row, new_row)
        
    def perform_move(self, from_row, to_row, rebuild_widget: bool = False):
        """Robustly moves an item from from_row to to_row (insertion)."""
        listw = self.window.listw
        if from_row == to_row or from_row < 0 or to_row < 0 or from_row >= listw.count() or to_row >= listw.count():
            return
        updates_prev = listw.updatesEnabled()
        listw.setUpdatesEnabled(False)
        blocker = None
        try:
            from PyQt5.QtCore import QSignalBlocker
            blocker = QSignalBlocker(listw)
        except Exception:
            blocker = None
        try:
            item = listw.item(from_row)
            if not item:
                return
            path = item.data(Qt.UserRole)
            probe_data = item.data(Qt.UserRole + 1)
            f_hash = item.data(Qt.UserRole + 2)
            clip_id = item.data(Qt.UserRole + 3)
            self.window.logger.info(f"LOGIC: Moving item '{os.path.basename(str(path))}' from index {from_row} to {to_row}.")
            listw.takeItem(from_row)
            self.window.event_handler._add_single_item_internal(
                path,
                row=to_row,
                probe_data=probe_data,
                f_hash=f_hash,
                clip_id=clip_id,
            )
            item = listw.item(to_row)
            listw.clearSelection()
            item.setSelected(True)
            listw.setCurrentItem(item)
            self.ensure_item_widgets_consistent()
            if hasattr(self.window.event_handler, 'update_button_states'):
                self.window.event_handler.update_button_states()
        finally:
            if blocker is not None:
                try:
                    blocker.unblock()
                except Exception:
                    pass
            listw.setUpdatesEnabled(updates_prev)

    def ensure_item_widgets_consistent(self):
        """Self-heal: ensure every row has a bound widget to prevent visual ghost blanks."""
        listw = self.window.listw
        for i in range(listw.count()):
            try:
                item = listw.item(i)
                if not item:
                    continue
                if listw.itemWidget(item) is not None:
                    continue
                path = item.data(Qt.UserRole)
                probe_data = item.data(Qt.UserRole + 1)
                w = self.window.make_item_widget(path)
                item.setSizeHint(w.sizeHint())
                listw.setItemWidget(item, w)
            except Exception:
                continue
