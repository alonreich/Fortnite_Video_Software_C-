import time
import threading
from PyQt5.QtCore import QTimer
from PyQt5.QtWidgets import QStyle
from system.time_sync import TimeSyncEngine
from system import diagnostic_runtime
from system.utils import MPVSafetyManager

def _qt_single_shot(delay_ms, callback):
    single_shot = getattr(QTimer, "singleShot", None)
    if callable(single_shot):
        single_shot(delay_ms, callback)
    elif delay_ms <= 0 and callable(callback):
        callback()

def _active_speed_segments(host):
    raw_segments = list(getattr(host, "speed_segments", []) or [])
    segments = []
    for seg in raw_segments:
        if not isinstance(seg, dict):
            continue
        try:
            start = int(seg.get("start", seg.get("start_ms", 0)))
            end = int(seg.get("end", seg.get("end_ms", 0)))
            speed = float(seg.get("speed", getattr(host, "playback_rate", 1.0)))
        except Exception:
            continue
        if end > start:
            segments.append({"start": start, "end": end, "start_ms": start, "end_ms": end, "speed": speed})
    if not segments:
        return []
    segments.sort(key=lambda item: (item["start"], item["end"]))
    return segments

def _host_mpv_set(host, prop, value, target_player=None):
    setter = getattr(host, "_safe_mpv_set", None)
    if callable(setter):
        return setter(prop, value, target_player=target_player)
    player = target_player if target_player is not None else getattr(host, "player", None)
    if not player:
        return False
    try:
        setattr(player, prop.replace("-", "_"), value)
        return True
    except Exception:
        return False

def _host_mpv_get(host, prop, default=None, target_player=None):
    getter = getattr(host, "_safe_mpv_get", None)
    if callable(getter):
        return getter(prop, default, target_player=target_player)
    player = target_player if target_player is not None else getattr(host, "player", None)
    if not player:
        return default
    return getattr(player, prop.replace("-", "_"), default)

def _host_mpv_command(host, *args, target_player=None):
    commander = getattr(host, "_safe_mpv_command", None)
    if callable(commander):
        return commander(*args, target_player=target_player)
    player = target_player if target_player is not None else getattr(host, "player", None)
    if not player or not args:
        return False
    if args[0] == "seek" and len(args) >= 2:
        try:
            target_ms = int(round(float(args[1]) * 1000.0))
            if hasattr(player, "set_time"):
                player.set_time(target_ms)
            else:
                setattr(player, "time_pos", float(args[1]))
            return True
        except Exception:
            return False
    return False

class PlayerMixin:
    def __init__(self, *args, **kwargs):
        super().__init__(*args, **kwargs)
        self._mpv_lock = threading.RLock()
        self._is_seeking_active = False
        self._in_freeze_segment = False
        self._freeze_start_ts = 0
        self._freeze_seg = None
        self._fvs_last_user_interaction_mono = 0.0

    def _refresh_seek_state(self):
        player = getattr(self, "player", None)
        if not player: return
        player_busy = bool(getattr(player, '_seeking_active', False))
        try:
            is_native_seeking = bool(self._safe_mpv_get("seeking", False))
            if not is_native_seeking and player_busy:
                setattr(player, '_seeking_active', False)
                player_busy = False
        except: pass
        if getattr(self, "_is_seeking_active", False) and not player_busy:
            self._is_seeking_active = False

    def _apply_native_seek(self, data):
        target_ms = data['ms']
        force_pause = data['force_pause']
        slider_is_down = data['slider_is_down']
        speed_factor = data['speed_factor']
        try:
            if getattr(self, "_in_transition", False) or getattr(self, "_shutting_down", False):
                return False
            p = getattr(self, "player", None)
            if not p or getattr(p, '_core_shutdown', False): return False
            target_m_sec = -1.0
            precision = "keyframes" if slider_is_down else "exact"
            music_player = getattr(self, "_music_preview_player", None)
            if music_player and getattr(self, "_wizard_tracks", None):
                try:
                    mapper = getattr(self, "_calculate_wall_clock_time", None)
                    if not callable(mapper):
                        mapper = lambda video_ms, segments, base: PlayerMixin._calculate_wall_clock_time(self, video_ms, segments, base)
                    t_start = getattr(self, "trim_start_ms", 0)
                    speed_segments = _active_speed_segments(self)
                    wall_target = mapper(target_ms, speed_segments, speed_factor)
                    wall_start = mapper(t_start, speed_segments, speed_factor)
                    project_pos_sec = (wall_target - wall_start) / 1000.0
                    accum = 0.0
                    for path, offset, dur in self._wizard_tracks:
                        if accum <= project_pos_sec < accum + dur:
                            target_m_sec = offset + (project_pos_sec - accum)
                            break
                        accum += dur
                except: pass
            if not getattr(p, '_safe_shutdown_initiated', False):
                now = time.time()
                last_scrub_ts = float(getattr(self, "_last_scrub_ts", 0.0) or 0.0)
                if last_scrub_ts and (now - last_scrub_ts) < 0.05:
                    return True
                self._last_scrub_ts = now
                if force_pause: _host_mpv_set(self, "pause", True)
                _host_mpv_command(self, "seek", target_ms / 1000.0, "absolute", precision)
                if music_player and target_m_sec >= 0 and not getattr(music_player, '_core_shutdown', False):
                    if force_pause: _host_mpv_set(self, "pause", True, target_player=music_player)
                    _host_mpv_command(self, "seek", target_m_sec, "absolute", "exact", target_player=music_player)
                    _host_mpv_set(self, "speed", 1.0, target_player=music_player)
            if hasattr(self, "timer") and not self.timer.isActive():
                if getattr(self, "input_file_path", None):
                    _qt_single_shot(0, self.timer.start)
            return True
        except:
            return False

    def _safe_mpv_set(self, prop, value, target_player=None):
        p = target_player if target_player is not None else getattr(self, "player", None)
        if not p: return False
        return MPVSafetyManager.safe_mpv_set(p, prop, value, lock=self._mpv_lock)

    def _safe_mpv_get(self, prop, default=None, target_player=None):
        p = target_player if target_player is not None else getattr(self, "player", None)
        if not p: return default
        return MPVSafetyManager.safe_mpv_get(p, prop, default, lock=self._mpv_lock)

    def _safe_mpv_command(self, *args, target_player=None):
        p = target_player if target_player is not None else getattr(self, "player", None)
        if not p: return False
        return MPVSafetyManager.safe_mpv_command(p, args[0], *args[1:], lock=self._mpv_lock)

    def toggle_play_pause(self):
        if getattr(self, "_in_transition", False): return
        if not getattr(self, "input_file_path", None): return
        if not getattr(self, "player", None): return
        is_paused = self._safe_mpv_get("pause", True)
        if not is_paused:
            if self.timer.isActive(): self.timer.stop()
            self._safe_mpv_set("pause", True)
            music_player = getattr(self, "_music_preview_player", None)
            if music_player: self._safe_mpv_set("pause", True, target_player=music_player)
            self.wants_to_play = False
            curr_pos = self._safe_mpv_get("time-pos", 0) or 0
            self.set_player_position(curr_pos * 1000, sync_only=True, force_pause=True)
            self.playPauseButton.setText("PLAY")
            self.playPauseButton.setIcon(self.style().standardIcon(QStyle.SP_MediaPlay))
        else:
            idle_active = self._safe_mpv_get("idle-active", False)
            if idle_active:
                restart_ms = int(getattr(self, "trim_start_ms", 0) or 0)
                self._safe_mpv_command("seek", restart_ms / 1000.0, "absolute", "exact")
                if getattr(self, "positionSlider", None):
                    self.positionSlider.blockSignals(True)
                    self.positionSlider.setValue(restart_ms)
                    self.positionSlider.blockSignals(False)
            self.wants_to_play = True
            curr_v_ms = (self._safe_mpv_get("time-pos", 0) or 0) * 1000
            music_player = getattr(self, "_music_preview_player", None)
            if music_player and getattr(self, "_wizard_tracks", None):
                t_start = getattr(self, "trim_start_ms", 0)
                speed_factor = self.speed_spinbox.value() if hasattr(self, 'speed_spinbox') else 1.1
                speed_segments = _active_speed_segments(self)
                wall_now = self._calculate_wall_clock_time(curr_v_ms, speed_segments, speed_factor)
                wall_start = self._calculate_wall_clock_time(t_start, speed_segments, speed_factor)
                project_pos_sec = (wall_now - wall_start) / 1000.0
                target_m_sec = 0.0
                accum = 0.0
                for path, offset, dur in self._wizard_tracks:
                    if accum <= project_pos_sec < accum + dur:
                        target_m_sec = offset + (project_pos_sec - accum)
                        break
                    accum += dur
                self._safe_mpv_set("speed", 1.0, target_player=music_player)
                curr_m_pos = self._safe_mpv_get("time-pos", 0, target_player=music_player) or 0
                if abs(curr_m_pos - target_m_sec) > 0.15:
                    self._safe_mpv_command("seek", target_m_sec, "absolute", "exact", target_player=music_player)
                self._safe_mpv_set("pause", False, target_player=music_player)
            self._safe_mpv_set("speed", self.playback_rate)
            self._safe_mpv_set("pause", False)
            self._check_and_update_speed(curr_v_ms)
            if not self.timer.isActive(): self.timer.start(50)
            self.playPauseButton.setText("PAUSE")
            self.playPauseButton.setIcon(self.style().standardIcon(QStyle.SP_MediaPause))

    def update_player_state(self):
        PlayerMixin._refresh_seek_state(self)
        if getattr(self, "_in_transition", False): return
        try:
            if getattr(self, "_in_freeze_segment", False):
                now_wall = time.time()
                elapsed = (now_wall - self._freeze_start_ts) * 1000
                seg = self._freeze_seg
                seg_dur = seg['end'] - seg['start']
                if elapsed >= seg_dur:
                    resume_ms = int(seg['start'])
                    self._in_freeze_segment = False
                    self._freeze_seg = None
                    self._safe_mpv_command("seek", resume_ms / 1000.0, "absolute", "exact")
                    self._check_and_update_speed(resume_ms)
                    self._safe_mpv_set("pause", False)
                    music_player = getattr(self, "_music_preview_player", None)
                    if music_player:
                        if hasattr(self, "_sync_music_preview"): self._sync_music_preview()
                        self._safe_mpv_set("pause", False, target_player=music_player)
                    if hasattr(self, "positionSlider"): self.positionSlider.setValue(resume_ms)
                return
            p = getattr(self, "player", None)
            if not p: return
            idle_active = self._safe_mpv_get("idle-active", True)
            curr_pos = self._safe_mpv_get("time-pos", 0) or 0
            dur = self._safe_mpv_get("duration", 0) or 0
            is_really_at_end = (dur > 0 and curr_pos > (dur - 0.5))
            if idle_active and (is_really_at_end or (curr_pos == 0 and not getattr(self, "wants_to_play", False))):
                if getattr(self, "playPauseButton", None):
                    self.playPauseButton.setText("PLAY")
                    self.playPauseButton.setIcon(self.style().standardIcon(QStyle.SP_MediaPlay))
                self.is_playing = False; self.wants_to_play = False
                music_player = getattr(self, "_music_preview_player", None)
                if music_player: self._safe_mpv_set("pause", True, target_player=music_player)
                if getattr(self, "timer", None) and self.timer.isActive(): self.timer.stop()
                return
            slider = getattr(self, "positionSlider", None)
            if slider and slider.isSliderDown(): return
            player_busy = bool(getattr(p, '_seeking_active', False))
            if player_busy or getattr(self, "_is_seeking_active", False):
                return
            current_time_ms = (self._safe_mpv_get("time-pos", 0) or 0) * 1000
            if current_time_ms >= 0:
                if slider:
                    if slider.maximum() <= 0:
                        dur_p = self._safe_mpv_get("duration", 0)
                        if dur_p > 0:
                            slider.setRange(0, int(dur_p * 1000))
                            if hasattr(slider, 'set_duration_ms'): slider.set_duration_ms(int(dur_p * 1000))
                    slider.blockSignals(True); slider.setValue(int(current_time_ms)); slider.blockSignals(False); slider.update()
                    if hasattr(self, "_sync_main_timeline_badges"): self._sync_main_timeline_badges()
                if hasattr(self, "_sync_music_preview"): self._sync_music_preview()
                self._check_and_update_speed(current_time_ms)
                is_p = not self._safe_mpv_get("pause", True)
                if is_p != getattr(self, "is_playing", None):
                    self.is_playing = is_p
                    icon = QStyle.SP_MediaPause if self.is_playing else QStyle.SP_MediaPlay
                    label = "PAUSE" if self.is_playing else "PLAY"
                    if hasattr(self, "playPauseButton"): self.playPauseButton.setText(label); self.playPauseButton.setIcon(self.style().standardIcon(icon))
        except Exception: pass

    def _check_and_update_speed(self, current_ms):
        speed_segments = _active_speed_segments(self)
        if not speed_segments:
            if getattr(self, "_in_freeze_segment", False):
                self._in_freeze_segment = False
                self._freeze_seg = None
            return
        try:
            base_rate = float(getattr(self, "playback_rate", self.speed_spinbox.value() if hasattr(self, "speed_spinbox") else 1.0))
        except Exception:
            base_rate = 1.0
        target_speed = base_rate; target_seg = None
        for seg in speed_segments:
            if abs(seg["speed"]) < 0.001 and seg["start"] <= current_ms < seg["end"]:
                target_speed = 0.0; target_seg = seg; break
        if abs(target_speed) > 0.001:
            for seg in speed_segments:
                if abs(seg["speed"]) >= 0.001 and seg["start"] <= current_ms < seg["end"]:
                    target_speed = seg["speed"]; target_seg = seg; break
        if abs(target_speed) < 0.001:
            if not getattr(self, "_in_freeze_segment", False):
                self._in_freeze_segment = True; self._freeze_start_ts = time.time(); self._freeze_seg = target_seg; self._safe_mpv_set("pause", True)
                music_player = getattr(self, "_music_preview_player", None)
                if music_player: self._safe_mpv_set("pause", True, target_player=music_player)
            return
        if getattr(self, "_in_freeze_segment", False):
            self._in_freeze_segment = False
            self._freeze_seg = None
        now = time.time()
        if not hasattr(self, "_last_speed_update"): self._last_speed_update = 0
        if now - self._last_speed_update < 0.20: return
        current_rate = self._safe_mpv_get("speed", 1.0)
        if abs(current_rate - target_speed) > 0.005:
            if self._safe_mpv_set("speed", target_speed):
                self._last_speed_update = now

    def set_player_position(self, position_ms, sync_only=False, force_pause=False):
        self._fvs_last_user_interaction_mono = time.monotonic()
        target_ms = int(position_ms)
        self._in_freeze_segment = False
        self._freeze_seg = None
        checker = getattr(self, "_check_and_update_speed", None)
        if callable(checker):
            checker(target_ms)
        else:
            PlayerMixin._check_and_update_speed(self, target_ms)
        self._pending_seek_ms = target_ms
        self._pending_force_pause = force_pause
        if hasattr(self, "positionSlider"):
            self._captured_slider_down = self.positionSlider.isSliderDown()
        self._captured_speed = 1.1
        if hasattr(self, "speed_spinbox"):
            self._captured_speed = self.speed_spinbox.value()
        if sync_only:
            executor = getattr(self, "_execute_throttled_seek", None)
            if callable(executor):
                executor()
            else:
                PlayerMixin._execute_throttled_seek(self)
        else:
            if not hasattr(self, "_seek_timer"):
                self._seek_timer = QTimer(self)
                self._seek_timer.setSingleShot(True)
                self._seek_timer.timeout.connect(self._execute_throttled_seek)
            if not self._seek_timer.isActive():
                self._seek_timer.start(0 if force_pause else 50)

    def _execute_throttled_seek(self):
        if not hasattr(self, "_pending_seek_ms") or self._pending_seek_ms is None:
            return
        player = getattr(self, "player", None)
        if not player or getattr(player, '_core_shutdown', False): return
        target_ms = self._pending_seek_ms
        force_pause = getattr(self, "_pending_force_pause", False)
        slider_is_down = getattr(self, "_captured_slider_down", False)
        speed_factor = getattr(self, "_captured_speed", 1.1)
        self._pending_seek_ms = None
        self._is_seeking_active = True
        seek_payload = {
            'ms': target_ms,
            'force_pause': force_pause,
            'slider_is_down': slider_is_down,
            'speed_factor': speed_factor
        }
        apply_native_seek = getattr(self, "_apply_native_seek", None)
        if callable(apply_native_seek):
            apply_native_seek(seek_payload)
            self._is_seeking_active = False
            return
        if PlayerMixin._apply_native_seek(self, seek_payload):
            self._is_seeking_active = False
            return
        try:
            precision = "keyframes" if slider_is_down else "exact"
            if force_pause: _host_mpv_set(self, "pause", True)
            _host_mpv_command(self, "seek", target_ms / 1000.0, "absolute", precision)
        finally:
            self._is_seeking_active = False
        
    def _calculate_wall_clock_time(self, video_ms, segments, base_speed):
        accumulated_wall_time = 0.0
        current_v = 0.0
        for seg in sorted(segments or [], key=lambda item: (item.get('start', 0), item.get('end', 0))):
            if video_ms <= seg['start']: break
            if seg['start'] > current_v:
                if base_speed < 0.001: accumulated_wall_time += (seg['start'] - current_v)
                else: accumulated_wall_time += (seg['start'] - current_v) / base_speed
            partial_dur = min(video_ms, seg['end']) - seg['start']
            speed = seg['speed']
            if speed < 0.001: accumulated_wall_time += partial_dur
            else: accumulated_wall_time += partial_dur / speed
            current_v = seg['end']
        if video_ms > current_v:
            if base_speed < 0.001: accumulated_wall_time += (video_ms - current_v)
            else: accumulated_wall_time += (video_ms - current_v) / base_speed
        return accumulated_wall_time

    def _on_mpv_end_reached(self, event=None):
        try:
            _qt_single_shot(0, self._safe_handle_mpv_end)
        except Exception as e:
            if hasattr(self, 'logger'):
                self.logger.error(f"MPV End Event failed to defer: {e}")

    def _bind_main_player_output(self):
        player = getattr(self, "player", None)
        if not player: return
        if getattr(player, "_wid_bound_once", False): return
        if getattr(self, "_binding_player_output", False): return
        self._binding_player_output = True
        try:
            wid = None
            surf = getattr(self, 'video_surface', None)
            if surf:
                try: wid = int(surf.winId())
                except: wid = None
            if wid is None or wid <= 0:
                self._binding_player_output = False
                return
            try:
                current_wid = self._safe_mpv_get("wid")
                if current_wid == wid:
                    player._wid_bound_once = True
                    return
            except Exception: pass
            if hasattr(self, "logger"):
                self.logger.info(f"HARDWARE_SET: Binding MPV to Main Surface WID {wid}")
            if self._safe_mpv_set("wid", wid):
                player._wid_bound_once = True
        except Exception as e:
            if hasattr(self, 'logger'): self.logger.error(f"Failed to bind MPV output: {e}")
        finally:
            self._binding_player_output = False

    def _safe_handle_mpv_end(self):
        try:
            if not self.signalsBlocked():
                self.video_ended_signal.emit()
        except Exception: pass
