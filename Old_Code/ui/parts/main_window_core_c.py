from PyQt5.QtCore import *
from PyQt5.QtGui import *
from PyQt5.QtWidgets import *

class MainWindowCoreCMixin:
    def _safe_handle_duration_changed(self, duration_ms: int):
        try:
            self.logger.info(f"UI: Video duration changed -> {duration_ms}ms")
            self.positionSlider.setRange(0, duration_ms); self.positionSlider.set_duration_ms(duration_ms)
            if hasattr(self, "_update_quality_label"): self._update_quality_label()
        except: pass

    def _save_app_state_and_config(self):
        cfg = self.config_manager.config
        try: cfg['mobile_checked'] = bool(self.mobile_checkbox.isChecked())
        except: pass
        try: cfg['teammates_checked'] = bool(self.teammates_checkbox.isChecked())
        except: pass
        try: cfg['last_directory'] = self.last_dir
        except: pass
        try:
            cfg['geometry'] = {'x': self.x(), 'y': self.y(), 'w': self.width(), 'h': self.height()}
            cfg['qt_geometry'] = bytes(self.saveGeometry().toBase64()).decode('utf-8')
        except: pass
        self.config_manager.save_config(cfg); self.logger.info("CONFIG: Application state and geometry saved.")

    def save_geometry(self): self._save_app_state_and_config()

    def restore_geometry(self):
        try:
            cfg = self.config_manager.config; qt_geom = cfg.get('qt_geometry')
            if qt_geom:
                if self.restoreGeometry(QByteArray.fromBase64(qt_geom.encode('utf-8'))): self.logger.info("UI: Restored geometry from Qt layout."); return
            geom = cfg.get('geometry')
            if geom and isinstance(geom, dict):
                x, y, w, h = geom.get('x'), geom.get('y'), geom.get('w'), geom.get('h')
                if x is not None and y is not None:
                    screen = QApplication.screenAt(QPoint(x, y)) or QApplication.primaryScreen(); avail = screen.availableGeometry()
                    self.move(max(avail.x(), min(x, avail.right() - 100)), max(avail.y(), min(y, avail.bottom() - 100)))
                if w is not None and h is not None: self.resize(w, h)
                self.logger.info(f"UI: Restored absolute geometry -> {w}x{h} at ({x},{y})")
        except: pass

    def cleanup_and_exit(self):
        try:
            if hasattr(self, "logger"): self.logger.info("SYSTEM: Shutdown sequence initiated.")
            self.blockSignals(True)
            if hasattr(self, "timer") and self.timer.isActive(): self.timer.stop()
            if hasattr(self, "_hw_worker") and self._hw_worker:
                try: self._hw_worker.abort()
                except: pass
            if getattr(self, "is_processing", False) and hasattr(self, "process_thread"):
                try:
                    self.process_thread.cancel()
                    if self.process_thread.isRunning(): self.process_thread.wait(1000)
                except: pass

            from system.utils import MPVSafetyManager
            if getattr(self, "player", None): 
                MPVSafetyManager.safe_mpv_shutdown(self.player, timeout=0.3, lock=getattr(self, "_mpv_lock", None))
            if getattr(self, "_music_preview_player", None): 
                MPVSafetyManager.safe_mpv_shutdown(self._music_preview_player, timeout=0.3, lock=getattr(self, "_mpv_lock", None))

            from system.utils import ProcessManager
            try: ProcessManager.cleanup_temp_files()
            except: pass
            if not getattr(self, "_preserve_staged_input_on_close", False) and hasattr(self, "_cleanup_staged_input_workspace"):
                try: self._cleanup_staged_input_workspace(clear_input=False)
                except: pass
            try: self._save_app_state_and_config()
            except: pass
            if hasattr(self, "logger"): self.logger.info("SYSTEM: Cleanup complete.")
            QTimer.singleShot(100, lambda: QCoreApplication.instance().quit())
        except Exception as e:
            if hasattr(self, "logger"): self.logger.error(f"SYSTEM: Error during cleanup: {e}")
            QCoreApplication.instance().quit()

    def _on_slider_trim_changed(self, start_ms, end_ms):
        self.logger.info(f"TRIM: Slider range updated -> START: {start_ms}ms, END: {end_ms}ms")
        self.trim_start_ms = start_ms; self.trim_end_ms = end_ms
        if hasattr(self, "_wizard_tracks") and self._wizard_tracks:
            m_s = getattr(self, "music_timeline_start_ms", 0); m_e = getattr(self, "music_timeline_end_ms", 0)
            self.music_timeline_start_ms = max(start_ms, min(m_s, end_ms)); self.music_timeline_end_ms = max(self.music_timeline_start_ms, min(m_e, end_ms))
            if hasattr(self, "positionSlider"): self.positionSlider.set_music_times(self.music_timeline_start_ms, self.music_timeline_end_ms)
            if len(self._wizard_tracks) == 1:
                p, o, _ = self._wizard_tracks[0]; self._wizard_tracks[0] = (p, o, (self.music_timeline_end_ms - self.music_timeline_start_ms) / 1000.0)
        else:
            self.music_timeline_start_ms = 0; self.music_timeline_end_ms = 0
            if hasattr(self, "positionSlider"): self.positionSlider.reset_music_times()
        self._update_trim_widgets_from_trim_times(); self._update_quality_label()
        if hasattr(self, "_trigger_recovery_save"): self._trigger_recovery_save()
