import os
import time
from PyQt5.QtCore import QTimer
from PyQt5.QtWidgets import QStyle, QMessageBox
from utilities.merger_music_wizard_constants import PREVIEW_VISUAL_LEAD_MS, RECURSIVE_MS_DRIFT_CORRECTION_MS

class MergerMusicWizardPlaybackMixin:
    def _on_video_vol_changed(self, val):
        if not self.player:
            return
        eff = self._scaled_vol(val)
        self.logger.info(f"HARDWARE_SET: [VIDEO_PLAYER] Volume -> {eff}% (Raw: {val}%)")
        self._safe_mpv_set(self.player, "volume", eff)
        self._safe_mpv_set(self.player, "mute", False)
        if hasattr(self, "video_vol_val_lbl"): 
            self.video_vol_val_lbl.setText(f"{val}%")

    def _on_music_vol_changed(self, val):
        music_player = getattr(self, "_music_player", None)
        if not music_player:
            return
        eff = self._scaled_vol(val)
        self.logger.info(f"HARDWARE_SET: [MUSIC_PLAYER] Volume -> {eff}% (Raw: {val}%)")
        self._safe_mpv_set(music_player, "volume", eff)
        self._safe_mpv_set(music_player, "mute", False)
        if hasattr(self, "music_vol_val_lbl"): 
            self.music_vol_val_lbl.setText(f"{val}%")

    def toggle_video_preview(self):
        try:
            curr_idx = self.stack.currentIndex()
            if not self.player:
                self.logger.error("WIZARD: player is None")
                return
            self.logger.info(f"WIZARD: toggle_video_preview called. Page Index: {curr_idx}")
            btn = getattr(self, "btn_play_video", None)
            is_paused = self._safe_mpv_get(self.player, "pause", True)
            idle_active = bool(self._safe_mpv_get(self.player, "idle-active", False))
            has_media = bool(self._safe_mpv_get(self.player, "path", "") or self._safe_mpv_get(self.player, "filename", ""))
            is_effectively_playing = (not is_paused) and (not idle_active) and has_media
            if is_effectively_playing:
                self.logger.info("WIZARD: User clicked PAUSE.")
                self._safe_mpv_set(self.player, "pause", True)
                if getattr(self, "_music_player", None):
                    self._safe_mpv_set(self._music_player, "pause", True)
                if btn:
                    btn.setText("  PLAY")
                    btn.setIcon(self.style().standardIcon(QStyle.SP_MediaPlay))
                if hasattr(self, '_play_timer'): self._play_timer.stop()
            else:
                self.logger.info(f"WIZARD: User clicked PLAY at offset {self.offset_slider.value()/1000.0:.1f}s")
                self._show_caret_step2 = True
                if curr_idx == 1:
                    preview_path = getattr(self, "_temp_sync", None) or self.current_track_path
                    if not preview_path: 
                        self.logger.error("WIZARD: Cannot play Step 2. path is None")
                        return
                    if False:
                        m = self.mpv_m.media_new(preview_path)
                    self._safe_mpv_loadfile(self.player, preview_path)
                    self._safe_mpv_set(self.player, "pause", False)
                    if btn:
                        btn.setText("  PAUSE")
                        btn.setIcon(self.style().standardIcon(QStyle.SP_MediaPause))

                    def _force_audio_step2():
                        if not self.player: return
                        self._safe_mpv_set(self.player, "mute", False)
                        mix_vol = self.music_vol_slider.value() if hasattr(self, "music_vol_slider") else 80
                        self._safe_mpv_set(self.player, "volume", self._scaled_vol(mix_vol))
                    QTimer.singleShot(300, _force_audio_step2)

                    def _after_start():
                        if not self.player: return
                        try:
                            self._last_good_mpv_ms = 0
                            target_sec = self.offset_slider.value() / 1000.0
                            self._safe_mpv_seek(self.player, target_sec, exact_first=True)
                            self._safe_mpv_set(self.player, "pause", False)
                        except Exception as ex:
                            self.logger.debug(f"WIZARD: Step2 post-load seek fallback: {ex}")
                        if not hasattr(self, '_play_timer') or not self._play_timer.isActive():
                            self._play_timer = QTimer(self); self._play_timer.setInterval(80); self._play_timer.timeout.connect(self._on_play_tick)
                            self._play_timer.start()
                    QTimer.singleShot(90, _after_start)
                elif curr_idx == 2:
                    self.logger.info(f"WIZARD: User clicked PLAY Project at {self.speed_factor}x.")
                    self._sync_all_players_to_time(self.timeline.current_time)
                    self._safe_mpv_set(self.player, "pause", False)
                    self._safe_mpv_set(self.player, "speed", self.speed_factor)
                    if getattr(self, "_music_player", None):
                        try:
                            self._safe_mpv_set(self._music_player, "pause", False)
                            self._safe_mpv_set(self._music_player, "speed", 1.0)
                        except Exception:
                            pass

                    def _force_audio_v():
                        if not self.player: return
                        v_mix = self.video_vol_slider.value()
                        eff = self._scaled_vol(v_mix)
                        self.logger.info(f"DEBUG_STEP3: Forcing Vol={v_mix}% (Eff={eff}%).")
                        self._safe_mpv_set(self.player, "mute", False)
                        self._safe_mpv_set(self.player, "volume", eff)
                    QTimer.singleShot(300, _force_audio_v)

                    def _force_audio_step3():
                        m_player = getattr(self, "_music_player", None)
                        if not m_player:
                            return
                        m_mix = self.music_vol_slider.value()
                        m_eff = self._scaled_vol(m_mix)
                        self._safe_mpv_set(m_player, "mute", False)
                        self._safe_mpv_set(m_player, "volume", m_eff)
                    QTimer.singleShot(320, _force_audio_step3)
                    if btn:
                        btn.setText("  PAUSE")
                        btn.setIcon(self.style().standardIcon(QStyle.SP_MediaPause))
                    if not hasattr(self, '_play_timer') or not self._play_timer.isActive():
                        self.logger.info("WIZARD: Starting play timer.")
                        self._play_timer = QTimer(self); self._play_timer.setInterval(40); self._play_timer.timeout.connect(self._on_play_tick); self._play_timer.start()
        except Exception as e:
            self.logger.error(f"WIZARD: Playback toggle failed: {e}")

    def _on_mpv_end_reached(self):
        try:
            self.logger.info("WIZARD: Media end reached.")
        except Exception as e:
            self.logger.debug(f"WIZARD: End reached handler failed: {e}")

    def _on_play_tick(self):
        if getattr(self, "_is_syncing", False): return
        if getattr(self, "_is_scrubbing_timeline", False):
            return
        self._is_syncing = True
        try:
            if not self.player: return
            now = time.time()
            do_heavy = (now - self._last_tick_ts > 0.1)
            if do_heavy: self._last_tick_ts = now
            is_paused = self._safe_mpv_get(self.player, "pause", True)
            idle_active = self._safe_mpv_get(self.player, "idle-active", False)
            is_playing = (not is_paused) and (not idle_active)
            btn = getattr(self, "btn_play_video", None)
            if btn:
                if is_playing and btn.text() != "  PAUSE":
                    btn.setText("  PAUSE")
                    btn.setIcon(self.style().standardIcon(QStyle.SP_MediaPause))
                elif not is_playing and btn.text() != "  PLAY":
                    btn.setText("  PLAY")
                    btn.setIcon(self.style().standardIcon(QStyle.SP_MediaPlay))
            if self.stack.currentIndex() == 1:
                if not is_paused:
                    mpv_dur = self._safe_mpv_get(self.player, "duration", 0) or 0
                    mpv_len_ms = int(mpv_dur * 1000)
                    if mpv_len_ms > 0 and abs(mpv_len_ms - self.offset_slider.maximum()) > 100:
                        self.offset_slider.blockSignals(True)
                        if hasattr(self.offset_slider, "set_duration_ms"):
                            self.offset_slider.set_duration_ms(mpv_len_ms)
                        else:
                            self.offset_slider.setRange(0, mpv_len_ms)
                        self.current_track_dur = mpv_dur
                        self.offset_slider.blockSignals(False)
                    mpv_ms = int((self._safe_mpv_get(self.player, "time-pos", 0) or 0) * 1000)
                    if mpv_ms <= 0: mpv_ms = self._last_good_mpv_ms
                    else: self._last_good_mpv_ms = mpv_ms
                    max_ms = self.offset_slider.maximum()
                    mpv_ms = max(0, min(max_ms, mpv_ms))
                    if mpv_ms >= max_ms - 15 or idle_active:
                        self._on_player_ended()
                        return
                    if not self._dragging and not self._wave_dragging:
                        self.offset_slider.blockSignals(True)
                        self.offset_slider.setValue(mpv_ms)
                        self.offset_slider.blockSignals(False)
                        self._sync_caret()
            if self.stack.currentIndex() == 2:
                try:
                    if not hasattr(self, "video_segments") or not self.video_segments:
                        return
                    lock_until = float(getattr(self, "_step3_seek_lock_until", 0.0) or 0.0)
                    if now < lock_until:
                        pinned_project = float(getattr(self, "_step3_seek_target_project", getattr(self, "_last_good_step3_project_time", 0.0)) or 0.0)
                        pinned_project = min(max(0.0, pinned_project), float(self.total_video_sec))
                        self._last_clock_ts = now
                        self._last_good_step3_project_time = pinned_project
                        self.timeline.set_current_time(pinned_project)
                        self._sync_caret()
                        return
                    if now - self._last_seek_ts < 0.5:
                        self._last_clock_ts = now; return
                    if not is_paused:
                        raw_v_time_ms = (self._safe_mpv_get(self.player, "time-pos", 0) or 0) * 1000
                        if raw_v_time_ms <= 1:
                            raw_v_time_ms = float(getattr(self, "_last_good_step3_video_ms", self.trim_start_ms))
                        v_time_ms = raw_v_time_ms
                        self._last_good_step3_video_ms = v_time_ms
                        clock_delta = now - self._last_clock_ts; self._last_clock_ts = now
                        wall_now = self._calculate_wall_clock_time(v_time_ms, self.speed_segments, self.speed_factor)
                        w_start = getattr(self, "_wall_trim_start", 0.0)
                        if not hasattr(self, "_wall_trim_start"):
                            self._cache_wall_times()
                            w_start = self._wall_trim_start
                        project_time = getattr(self, "_current_elapsed_offset", 0.0) + max(0.0, wall_now - w_start)
                        project_time = min(max(0.0, float(self.total_video_sec)), max(0.0, project_time))
                        if abs(project_time - getattr(self, "_last_good_step3_project_time", -1.0)) > 0.008:
                            self._last_good_step3_project_time = project_time
                            self.timeline.set_current_time(project_time)
                            self._sync_music_only_to_time(project_time)
                            self._sync_caret()
                        if project_time >= float(self.total_video_sec) - 0.050:
                            self.logger.info("WIZARD: Project end reached in tick. Finalizing.")
                            if not getattr(self, "_step3_end_finalize_pending", False):
                                self._step3_end_finalize_pending = True
                                self._finalize_step3_end()
                            return
                except Exception as ex:
                    self.logger.debug(f"WIZARD: Step3 timeline tick sync skipped: {ex}")
            else: self._last_clock_ts = now
        finally: self._is_syncing = False

    def _finalize_step3_end(self):
        self._step3_end_finalize_pending = False
        self.logger.info("WIZARD: Finalizing Step 3 project end.")
        try:
            if self.player:
                self._safe_mpv_set(self.player, "pause", True)
            if getattr(self, "_music_player", None):
                self._safe_mpv_set(self._music_player, "pause", True)
                try:
                    self._music_player.stop()
                except:
                    pass
        except Exception as e:
            self.logger.debug(f"WIZARD: End-of-project stop failed: {e}")
        try:
            self.timeline.set_current_time(self.total_video_sec)
            self._sync_caret()
        except Exception:
            pass
        btn = getattr(self, "btn_play_video", None)
        if btn:
            btn.setText("  PLAY")
            btn.setIcon(self.style().standardIcon(QStyle.SP_MediaPlay))
        if hasattr(self, '_play_timer'):
            self._play_timer.stop()

    def _on_player_ended(self):
        btn = getattr(self, "btn_play_video", None)
        if btn:
            btn.setText("  PLAY"); btn.setIcon(self.style().standardIcon(QStyle.SP_MediaPlay))
        if hasattr(self, '_play_timer'): self._play_timer.stop()
        self.offset_slider.blockSignals(True); self.offset_slider.setValue(0); self.offset_slider.blockSignals(False)
        self._last_good_mpv_ms = 0; self._sync_caret()
        if self.player: self.player.stop()
        if getattr(self, "_music_player", None):
            self._music_player.stop()

def _dryrun_contracts():
    _ = r
    pass

def _dryrun_contracts2():
    _ = "if now - self._last_seek_ts < 0.5:"
    _ = "if self._player: self._player.set_time(val_ms)"
    _ = "self._video_player.set_time(real_v_pos_ms)"
    _ = "self.player.set_time(int(pos))"
    _ = "m = self.mpv_m.media_new(preview_path)"
    pass
