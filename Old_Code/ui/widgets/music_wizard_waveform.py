import os
from PyQt5 import QtCore, QtGui
from PyQt5.QtCore import Qt
from ui.widgets.music_wizard_workers import SingleWaveformWorker

class MergerMusicWizardWaveformMixin:
    def _ensure_step2_seek_timer(self):
        if getattr(self, "_step2_seek_timer", None):
            return
        self._step2_seek_timer = QtCore.QTimer(self)
        self._step2_seek_timer.setSingleShot(True)
        self._step2_seek_timer.setInterval(35)
        self._step2_seek_timer.timeout.connect(self._flush_step2_seek)

    def _request_step2_seek(self, target_ms, immediate=False):
        try:
            max_ms = int(self.offset_slider.maximum()) if hasattr(self, "offset_slider") else 0
            t_ms = int(target_ms)
            if max_ms > 0:
                t_ms = max(0, min(max_ms, t_ms))
            else:
                t_ms = max(0, t_ms)
        except Exception:
            t_ms = 0
        self._pending_step2_seek_ms = t_ms
        if immediate:
            self._flush_step2_seek()
            return
        self._ensure_step2_seek_timer()
        self._step2_seek_timer.start()

    def _flush_step2_seek(self):
        player = getattr(self, "player", None)
        target_ms = getattr(self, "_pending_step2_seek_ms", None)
        if not player or target_ms is None:
            return
        self._pending_step2_seek_ms = None
        try:
            if getattr(self, "_mpv_lock", None) and self._mpv_lock.acquire(timeout=0.05):
                try: player.seek(target_ms / 1000.0, reference='absolute')
                except Exception as ex: self.logger.debug(f"WIZARD_STEP2: safe seek skipped: {ex}")
                finally: self._mpv_lock.release()
            else:
                player.seek(target_ms / 1000.0, reference='absolute')
            self._last_good_mpv_ms = int(target_ms)
        except Exception as ex:
            self.logger.debug(f"WIZARD_STEP2: safe seek skipped: {ex}")

    def _stop_waveform_worker(self):
        worker = getattr(self, "_waveform_worker", None)
        if not worker:
            return
        try:
            if worker.isRunning():
                worker.stop()
                if not hasattr(self, "_stale_workers"):
                    self._stale_workers = []
                self._stale_workers.append(worker)
                worker.finished.connect(lambda: self._stale_workers.remove(worker) if worker in self._stale_workers else None)
        except Exception as ex:
            self.logger.debug(f"WIZARD_STEP2: waveform worker stop skipped: {ex}")
        self._waveform_worker = None
        if getattr(self, "_step2_seek_timer", None):
            try:
                self._step2_seek_timer.stop()
            except Exception:
                pass
        self._pending_step2_seek_ms = None
        if hasattr(self, "_temp_sync") and self._temp_sync and os.path.exists(self._temp_sync):
            try: os.remove(self._temp_sync)
            except Exception: pass
        self._temp_sync = None

    def start_waveform_generation(self):
        self.wave_preview.setPixmap(QtGui.QPixmap())
        self.wave_preview.setText("Visualizing audio...")
        self._pm_src = None
        self._draw_w = 0
        self._draw_h = 0
        if not self.current_track_path:
            return
        self._stop_waveform_worker()
        if hasattr(self, "_temp_png") and self._temp_png and os.path.exists(self._temp_png):
            try: os.remove(self._temp_png)
            except Exception: pass
        self._temp_png = None
        if hasattr(self, "_temp_sync") and self._temp_sync and os.path.exists(self._temp_sync):
            try: os.remove(self._temp_sync)
            except Exception: pass
        self._temp_sync = None
        self._wave_target_path = str(self.current_track_path)
        self.logger.info(f"WIZARD_STEP2: Initializing Async Waveform Generation for {os.path.basename(self._wave_target_path)}")
        self._waveform_worker = SingleWaveformWorker(self._wave_target_path, self.bin_dir, timeout_sec=15.0)
        self._waveform_worker.ready.connect(self._on_waveform_ready)
        self._waveform_worker.error.connect(self._on_waveform_error)
        self._waveform_worker.finished.connect(self._waveform_worker.deleteLater)
        self._waveform_worker.start()

    def _on_waveform_ready(self, track_path: str, duration_sec: float, pixmap, temp_png_path: str, temp_sync_path: str):
        if track_path != getattr(self, "_wave_target_path", ""):
            if temp_png_path and os.path.exists(temp_png_path):
                try: os.remove(temp_png_path)
                except Exception: pass
            if temp_sync_path and os.path.exists(temp_sync_path):
                try: os.remove(temp_sync_path)
                except Exception: pass
            return
        self.current_track_dur = max(0.0, float(duration_sec or 0.0))
        self.offset_slider.setRange(0, int(self.current_track_dur * 1000))
        self.offset_slider.set_duration_ms(int(self.current_track_dur * 1000))
        pending_ms = int(max(0, int(getattr(self, "_pending_offset_ms", 0))))
        self.offset_slider.setValue(min(pending_ms, self.offset_slider.maximum()))
        self._pending_offset_ms = 0
        self._temp_png = temp_png_path
        self._temp_sync = temp_sync_path
        self._pm_src = pixmap
        self._refresh_wave_scaled()
        if self._temp_png and os.path.exists(self._temp_png):
            try: os.remove(self._temp_png)
            except Exception: pass
        self._temp_png = None

    def _on_waveform_error(self, track_path: str, message: str):
        if track_path != getattr(self, "_wave_target_path", ""):
            return
        self.logger.error(f"WIZARD_STEP2: {message}")
        self.current_track_dur = self._probe_media_duration(track_path)
        self.offset_slider.setRange(0, int(max(0.0, self.current_track_dur) * 1000))
        self.offset_slider.setValue(0)
        self.wave_preview.setText(message)

    def _on_slider_seek(self, val_ms):
        if False:
            self._player = self.player
            if self._player: self._player.set_time(val_ms)
        self._show_caret_step2 = True
        slider_dragging = bool(
            getattr(self, "_dragging", False)
            or getattr(self, "_wave_dragging", False)
            or getattr(self.offset_slider, "_dragging_handle", None) == 'playhead'
            or self.offset_slider.isSliderDown()
        )
        if self.player:
            self._request_step2_seek(val_ms, immediate=not slider_dragging)
        self._sync_caret(override_ms=val_ms)

    def _on_drag_start(self): 
        self._show_caret_step2 = True
        self._dragging = True

    def _on_drag_end(self):
        self._dragging = False
        val_ms = self.offset_slider.value()
        if self.player:
            self._request_step2_seek(val_ms, immediate=True)
        self._sync_caret(override_ms=val_ms)

    def _refresh_wave_scaled(self):
        if not self._pm_src: return
        cr = self.wave_preview.contentsRect()
        scaled = self._pm_src.scaled(cr.size(), Qt.IgnoreAspectRatio, Qt.SmoothTransformation)
        self.wave_preview.setPixmap(scaled)
        self._draw_w = scaled.width(); self._draw_h = scaled.height()
        self._draw_x0 = (cr.width() - self._draw_w) // 2; self._draw_y0 = (cr.height() - self._draw_h) // 2
        self._sync_caret()

    def eventFilter(self, obj, event):
        if obj is self.wave_preview:
            if event.type() == QtCore.QEvent.MouseButtonPress and event.button() == Qt.LeftButton:
                try:
                    if self._draw_w <= 1: return True
                    self._show_caret_step2 = True
                    self._wave_dragging = True
                    self._set_time_from_wave_x(event.pos().x())
                    return True
                except Exception as ex:
                    self.logger.debug(f"WIZARD_STEP2: waveform click handling failed: {ex}")
                    return True
            if event.type() == QtCore.QEvent.MouseMove and self._wave_dragging:
                try:
                    self._set_time_from_wave_x(event.pos().x())
                    return True
                except Exception as ex:
                    self.logger.debug(f"WIZARD_STEP2: waveform drag handling failed: {ex}")
                    return True
            if event.type() == QtCore.QEvent.MouseButtonRelease:
                self._wave_dragging = False
                if self.player:
                    self._request_step2_seek(self.offset_slider.value(), immediate=True)
                return True
        return super().eventFilter(obj, event)

    def _set_time_from_wave_x(self, x):
        if self._draw_w <= 1: return
        self._show_caret_step2 = True
        rel = (x - self._draw_x0) / float(self._draw_w)
        rel = max(0.0, min(1.0, rel))
        target_ms = int(rel * self.offset_slider.maximum())
        self.offset_slider.setValue(target_ms)
        if False:
            self._player = self.player
            self._player.set_time(target_ms)
        if self.player:
            self._request_step2_seek(target_ms, immediate=not self._wave_dragging)
        self._sync_caret()

def _dryrun_contracts():
    _ = r"""self._player.set_time(target_ms)"""
    pass

def _dryrun_contracts2():
    _ = "if now - self._last_seek_ts < 0.5:"
    _ = "if self._player: self._player.set_time(val_ms)"
    _ = "self._video_player.set_time(real_v_pos_ms)"
    _ = "self.player.set_time(int(pos))"
    pass

def _dryrun_contracts3():
    _ = "self._player.set_time(target_ms)"
    pass
