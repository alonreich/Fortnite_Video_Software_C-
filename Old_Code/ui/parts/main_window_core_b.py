import os, sys, time
from PyQt5.QtCore import *
from PyQt5.QtGui import *
from PyQt5.QtWidgets import *
from system.utils import MPVSafetyManager

class MainWindowCoreBMixin:
    def on_phase_update(self, phase: str) -> None:
        try:
            if getattr(self, "_in_transition", False): return
            if hasattr(self, "_set_overlay_phase"): self._set_overlay_phase(phase)
            if phase and hasattr(self, "_append_live_log"): self._append_live_log(f"STATUS: {phase}")
            p = (phase or "").lower()
            if any(x in p for x in ("processing", "step", "encode", "intro", "core", "concat")):
                self.is_processing = True
                if hasattr(self, "progress_bar"): self.progress_bar.show()
                if hasattr(self, "_pulse_timer"): self._pulse_timer.start(250)
            elif any(x in p for x in ("done", "idle", "error", "failed")):
                self.is_processing = False
                if hasattr(self, "progress_bar"): self.progress_bar.hide()
                if hasattr(self, "_pulse_timer"): self._pulse_timer.start(750)
            if hasattr(self, "_update_process_button_text"): self._update_process_button_text()
        except Exception as err:
            try:
                self.logger.debug("on_phase_update failed: %s", err)
            except Exception:
                pass

    def _handle_video_end(self):
        try:
            if getattr(self, "_in_transition", False): return
            if bool(getattr(self, "_opening_granular_dialog", False)): return
            if time.time() < float(getattr(self, "_ignore_mpv_end_until", 0.0)): return
            if bool(getattr(self, "_handling_video_end", False)): return
            self._handling_video_end = True
            if getattr(self, "player", None): self._safe_mpv_set("pause", True)
            self.positionSlider.blockSignals(True); self.positionSlider.setValue(self.positionSlider.maximum()); self.positionSlider.blockSignals(False)
            if getattr(self, "playPauseButton", None):
                self.playPauseButton.setText("PLAY"); self.playPauseButton.setIcon(self.style().standardIcon(QStyle.SP_MediaPlay))
            self.is_playing = False; self.wants_to_play = False; self.timer.stop()
        except Exception: pass
        finally: self._handling_video_end = False

    def log_overlay_sink(self, line: str):
        try: self._append_live_log(line)
        except Exception: pass

    def _on_speed_changed(self, value):
        self.playback_rate = value
        if self.player:
            if not self._safe_mpv_get("pause", True):
                self._safe_mpv_set("pause", True)
                self.playPauseButton.setText("PLAY"); self.playPauseButton.setIcon(self.style().standardIcon(QStyle.SP_MediaPlay)); self.is_playing = False
                if self.timer.isActive(): self.timer.stop()
        self.logger.info(f"UI: Playback speed changed to {value}x. Recalculating quality..."); self._update_quality_label()
        if hasattr(self, "_save_recovery_state"): self._save_recovery_state()
    @property
    def original_duration(self): return self.original_duration_ms / 1000.0 if self.original_duration_ms else 0.0

    def _setup_mpv(self):
        os.makedirs(os.path.join(self.base_dir, "logs"), exist_ok=True); self.player = None
        if not getattr(self, "_mpv_ready", True):
            self.mpv_instance = None
            return
        try:
            self.video_surface.setAttribute(Qt.WA_DontCreateNativeAncestors)
            self.video_surface.setAttribute(Qt.WA_NativeWindow)
            self.video_surface.setAttribute(Qt.WA_OpaquePaintEvent)
            self.video_surface.setAttribute(Qt.WA_NoSystemBackground)
            self.video_surface.setAutoFillBackground(False)
            self.video_surface.show()
            wid = int(self.video_surface.winId())
            hw_strat = str(getattr(self, "hardware_strategy", "CPU")).upper()
            target_hwdec = "auto"
            if "NVIDIA" in hw_strat: target_hwdec = "nvdec"
            elif "AMD" in hw_strat: target_hwdec = "d3d11va"
            elif "INTEL" in hw_strat: target_hwdec = "d3d11va"
            elif hw_strat == "CPU": target_hwdec = "no"
            self.player = MPVSafetyManager.create_safe_mpv(
                wid=wid, 
                osc=False, 
                hr_seek='yes', 
                hwdec=target_hwdec,
                keep_open='yes',
                ytdl=False,
                demuxer_max_bytes='500M',
                demuxer_max_back_bytes='100M',
                vo='gpu' if sys.platform == 'win32' else 'gpu',
                extra_mpv_flags=[('force-window', 'no')],
                input_vo_keyboard=False,
                input_default_bindings=False
            )
            self.mpv_instance = self.player
            if self.player:
                self.player.volume = 100
                try: self.player.speed = float(getattr(self, "playback_rate", 1.1) or 1.1)
                except Exception: pass
                self._bind_main_player_output()
                @self.player.event_callback('end-file')
                def h_ef(event):
                    try: MPVSafetyManager.run_on_qt_thread(self._on_mpv_end_reached)
                    except: pass
                self._mpv_end_file_cb = h_ef
        except Exception as e: self.logger.error(f"CRITICAL: MPV Error: {e}"); self.player = None
        if self.player: self._suspend_volume_sync = True
