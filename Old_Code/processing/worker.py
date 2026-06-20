import os
import tempfile
import uuid
import shutil
from fractions import Fraction
from typing import Tuple, Dict, Any, Optional, List
from PyQt5.QtCore import QThread, pyqtSignal
from .system_utils import create_subprocess, kill_process_tree, check_disk_space, monitor_ffmpeg_progress
from .filter_builder import FilterBuilder
from .encoders import EncoderManager
from .media_utils import MediaProber, calculate_video_bitrate, choose_audio_bitrate
from .processing_utils import ProgressScaler, generate_text_overlay_png
from .config_data import VideoConfig

class ProcessThread(QThread):
    progress_update_signal = pyqtSignal(int)
    status_update_signal = pyqtSignal(str)
    finished_signal = pyqtSignal(bool, str)

    def __init__(self, 
                 input_path, start_time_ms, end_time_ms, original_resolution,
                 is_mobile_format, speed_factor, base_dir=None,
                 progress_signal=None, status_signal=None, finished_signal=None,
                 logger=None, is_boss_hp=False, show_teammates_overlay=False,
                 show_spectating_overlay=False,
                 quality_level=2, bg_music_path=None, bg_music_volume=0.8,
                 bg_music_offset_ms=0, original_total_duration_ms=0,
                  disable_fades=False, intro_still_sec=0,
                  intro_from_midpoint=False, intro_abs_time_ms=None,
                  portrait_text=None, music_config=None, speed_segments=None,
                  hardware_strategy='CPU', music_tracks=None, script_dir=None,
                  target_mb_override=None,
                  progress_update_signal=None, status_update_signal=None,
                  volume_normalize_db=0.0):
        super().__init__()
        self.input_path = input_path
        self.start_time_ms = start_time_ms
        self.end_time_ms = end_time_ms
        self.original_resolution = original_resolution
        self.is_mobile_format = is_mobile_format
        self.speed_factor = speed_factor
        self.base_dir = base_dir or script_dir
        if self.base_dir:
            self.base_dir = os.path.normpath(self.base_dir)
            if os.path.basename(self.base_dir) == 'processing':
                self.base_dir = os.path.dirname(self.base_dir)
        if progress_update_signal: progress_signal = progress_update_signal
        if status_update_signal: status_signal = status_update_signal
        self.progress_update_signal = progress_signal
        self.status_update_signal = status_signal
        self.finished_signal = finished_signal
        self.logger = logger
        self.is_boss_hp = is_boss_hp
        self.show_teammates_overlay = show_teammates_overlay
        self.show_spectating_overlay = show_spectating_overlay
        self.portrait_text = portrait_text
        self.bg_music_path = bg_music_path
        self.bg_music_offset_ms = bg_music_offset_ms
        self.disable_fades = disable_fades
        self.intro_still_sec = float(intro_still_sec or 0.0)
        self.intro_from_midpoint = bool(intro_from_midpoint)
        self.intro_abs_time_ms = int(intro_abs_time_ms) if intro_abs_time_ms is not None else None
        self.speed_segments = self._normalize_speed_segments(speed_segments)
        self.volume_normalize_db = float(volume_normalize_db or 0.0)
        if self.logger:
            try:
                self.logger.info(
                    "GRANULAR_WORKER_STATE: normalized=%d base_speed=%.3f trim=%d-%d segments=%s",
                    len(self.speed_segments), float(self.speed_factor),
                    int(self.start_time_ms), int(self.end_time_ms), self.speed_segments,
                )
            except Exception:
                pass
        self.hardware_strategy = hardware_strategy
        self.music_config = dict(music_config or {})
        if self.bg_music_path and "music_vol" not in self.music_config and "volume" not in self.music_config:
            try:
                self.music_config["music_vol"] = float(bg_music_volume)
            except (TypeError, ValueError):
                self.music_config["music_vol"] = 0.8
        self.music_tracks = self._normalize_music_tracks(music_tracks)
        self.config = VideoConfig(self.base_dir)
        self.keep_highest_res, self.target_mb, self.quality_level = self.config.get_quality_settings(quality_level, target_mb_override=target_mb_override)
        self.filter_builder = FilterBuilder(self.logger)
        self.ffmpeg_path = os.path.join(self.base_dir, 'binaries', 'ffmpeg.exe')
        if not os.path.exists(self.ffmpeg_path):
            self.ffmpeg_path = 'ffmpeg'
        self.encoder_mgr = EncoderManager(self.logger, hardware_strategy=self.hardware_strategy, ffmpeg_path=self.ffmpeg_path)
        self.prober = MediaProber(os.path.join(self.base_dir, 'binaries'), self.input_path)
        self.current_process = None
        self.is_canceled = False
        self._finish_emitted = False
        self.duration_corrected_sec = (self.end_time_ms - self.start_time_ms) / 1000.0 / self.speed_factor
        self._output_dir = os.path.join(os.path.expanduser("~"), "Downloads")

    def _normalize_speed_segments(self, raw_segments):
        normalized = []
        trim_lo = int(getattr(self, "start_time_ms", 0) or 0)
        trim_hi = int(getattr(self, "end_time_ms", 0) or 0)
        for seg in (raw_segments or []):
            if not isinstance(seg, dict):
                continue
            try:
                s_ms = int(seg.get("start_ms", seg.get("start", 0)))
                e_ms = int(seg.get("end_ms", seg.get("end", 0)))
                spd = float(seg.get("speed", self.speed_factor))
            except Exception:
                continue
            if e_ms <= s_ms:
                continue
            if trim_hi > trim_lo:
                if e_ms <= trim_lo or s_ms >= trim_hi:
                    if self.logger:
                        self.logger.warning(f"GRANULAR: dropped out-of-trim segment [{s_ms},{e_ms}] (trim=[{trim_lo},{trim_hi}])")
                    continue
                clamped_s = max(trim_lo, s_ms)
                clamped_e = min(trim_hi, e_ms)
                if clamped_e - clamped_s <= 0:
                    continue
                s_ms, e_ms = clamped_s, clamped_e
            normalized.append({"start_ms": s_ms, "end_ms": e_ms, "speed": spd})
        normalized.sort(key=lambda x: x["start_ms"])
        return normalized

    def _normalize_music_tracks(self, raw_tracks):
        normalized = []
        try:
            fallback_duration = max(0.001, (float(self.end_time_ms) - float(self.start_time_ms)) / 1000.0 / max(0.001, float(self.speed_factor)))
        except Exception:
            fallback_duration = 0.001
        for track in list(raw_tracks or []):
            try:
                if isinstance(track, dict):
                    path = track.get("path")
                    offset = track.get("offset_sec", track.get("offset", track.get("file_offset_sec", 0.0)))
                    duration = track.get("duration_sec", track.get("duration", track.get("dur", fallback_duration)))
                else:
                    path = track[0]
                    offset = track[1] if len(track) > 1 else 0.0
                    duration = track[2] if len(track) > 2 else fallback_duration
                if not path:
                    continue
                duration = float(duration or fallback_duration)
                if duration <= 0.001:
                    duration = fallback_duration
                normalized.append((str(path), max(0.0, float(offset or 0.0)), max(0.001, duration)))
            except Exception:
                continue
        return normalized

    def _hardware_decode_flags(self, encoder_name: str) -> list[str]:
        return []

    def _uses_cuda_frames(self, encoder_name: str) -> bool:
        return "nvenc" in encoder_name.lower()

    def _target_size_bounds(self):
        if not self.target_mb:
            return None
        target = int(Fraction(str(self.target_mb)) * 1024 * 1024)
        variance = int(target * 0.01)
        return target - variance, target + variance

    def _choose_audio_bitrate(self, source_audio_kbps, duration_sec):
        return choose_audio_bitrate(source_audio_kbps, duration_sec, self.target_mb)

    def _output_resolution_for_bitrate(self):
        if self.is_mobile_format: return "1080x1920"
        return self.original_resolution or "1920x1080"

    def _validate_render_output(self, path, expected_duration_sec, target_fps_expr, monitor_stats):
        if not os.path.exists(path) or os.path.getsize(path) <= 0: return False, "Missing output."
        critical = (monitor_stats or {}).get("critical_lines", [])
        if critical:
            return False, f"Critical errors in log: {critical[0]}"
        out_probe = MediaProber(os.path.join(self.base_dir, 'binaries'), path)
        fps_expr = out_probe.get_video_fps_expr(target_fps_expr)
        try: fps_q = Fraction(str(fps_expr))
        except Exception: fps_q = Fraction(0, 1)
        if fps_q <= 0 or abs(fps_q - Fraction(60, 1)) > Fraction(1, 100): return False, f"FPS mismatch: {float(fps_q)}"
        duration = out_probe.get_duration()
        if duration > 0 and abs(duration - expected_duration_sec) > 0.5: return False, "Duration mismatch."
        return True, "OK"
    @staticmethod
    def _emit_signal_or_callback(target, *args):
        try:
            if hasattr(target, "emit"): target.emit(*args)
            elif callable(target): target(*args)
        except Exception: pass

    def cancel(self):
        self.is_canceled = True
        if self.current_process: kill_process_tree(self.current_process.pid, self.logger)

    def _monitor_disk_space(self):
        if self.is_canceled: return 1
        dynamic_threshold_gb = float(max(Fraction(1, 2), (Fraction(str(self.target_mb or 50)) * 3) / 1024))
        if not check_disk_space(self._output_dir, dynamic_threshold_gb): return 2
        return 0

    def _emit_status(self, msg): self._emit_signal_or_callback(self.status_update_signal, msg)

    def _emit_progress(self, value): self._emit_signal_or_callback(self.progress_update_signal, int(value))

    def _emit_finished(self, success, message):
        if self._finish_emitted: return
        self._finish_emitted = True
        self._emit_signal_or_callback(self.finished_signal, bool(success), str(message))

    def _resolve_final_output_path(self):
        idx = 1
        while True:
            target_path = os.path.join(self._output_dir, f"Fortnite-Video-{idx}.mp4")
            if not os.path.exists(target_path): return target_path
            idx += 1

    def run(self):
        try:
            pre_err = self.encoder_mgr.get_encoder_preflight_error()
            if pre_err:
                self._emit_finished(False, pre_err)
                return
            self.job_id = str(uuid.uuid4())[:8]
            self.temp_job_dir = os.path.join(tempfile.gettempdir(), f"fvs_job_{self.job_id}")
            os.makedirs(self.temp_job_dir, exist_ok=True)
            scaler_core = ProgressScaler(self.progress_update_signal, 0, 100)
            source_has_audio = self.prober.has_audio()
            source_audio_kbps = self.prober.get_audio_bitrate() or 192
            target_fps_expr = "60"
            text_png_path = None
            if self.portrait_text:
                text_png_path = os.path.join(self.temp_job_dir, "portrait_text.png")
                try:
                    from .text_ops import TextWrapper
                    wrapper = TextWrapper(self.config)
                    _, wrapped = wrapper.fit_and_wrap(self.portrait_text, 1000, self.logger)

                    from .processing_utils import generate_text_overlay_png
                    generate_text_overlay_png("\n".join(wrapped), 1080, 150, 40, 10, text_png_path, self.config, self.logger)
                except Exception: text_png_path = None
            music_cfg = self.music_config or {}
            g_v, g_a, g_dur = None, None, self.duration_corrected_sec
            granular_v_a_filters = ""
            if self.speed_segments:
                granular_v_a_filters, g_v, g_a, g_dur, _ = self.filter_builder.build_granular_speed_chain(
                    self.input_path, (self.end_time_ms - self.start_time_ms), self.speed_segments, self.speed_factor,
                    source_cut_start_ms=self.start_time_ms, input_v_label="[0:v]", input_a_label="[0:a]" if source_has_audio else None,
                    target_fps=target_fps_expr
                )
            audio_kbps = self._choose_audio_bitrate(source_audio_kbps, g_dur)
            if self.keep_highest_res and self.quality_level >= 20 and self.target_mb is None:
                video_bitrate_kbps = None
                if self.logger:
                    self.logger.info("BITRATE: Max quality CQ mode active; file-size targeting disabled.")
            else:
                video_bitrate_kbps = calculate_video_bitrate(self.input_path, g_dur, audio_kbps, self.target_mb, self.keep_highest_res, self.logger, self._output_resolution_for_bitrate(), target_fps_expr, self.quality_level, self.prober)
            if self.bg_music_path and not self.music_tracks:
                 self.music_tracks = [(self.bg_music_path, self.bg_music_offset_ms/1000.0, g_dur)]
            intro_duration_sec = max(0.0, float(self.intro_still_sec or 0.0))
            if intro_duration_sec < 0.001:
                intro_duration_sec = 0.0
            intro_input_index = 1 + len(self.music_tracks) if intro_duration_sec > 0.0 else None
            text_input_label = f"[{1 + len(self.music_tracks) + (1 if intro_input_index is not None else 0)}:v]" if text_png_path else None
            render_duration_sec = g_dur + intro_duration_sec
            if self.logger:
                self.logger.info(f"INTRO_FRAME_STATE: duration={intro_duration_sec:.3f}s input_index={intro_input_index} text_label={text_input_label}")
            audio_chains, final_a_label = self.filter_builder.build_audio_chain(music_cfg, self.start_time_ms/1000.0, self.end_time_ms/1000.0, self.speed_factor, self.disable_fades, 0.5 if not self.disable_fades else 0, "", 48000, self.music_tracks, 1, g_dur, volume_normalize_db=self.volume_normalize_db)
            core_path = os.path.normpath(os.path.join(self.temp_job_dir, "core.mp4"))

            last_error = "Render failed."
            def run_ffmpeg(use_cuda, requested_bitrate_kbps):
                nonlocal last_error
                current_encoder = self.encoder_mgr.get_initial_encoder() if use_cuda else 'libx264'
                while True:
                    vcodec, rc_label = self.encoder_mgr.get_codec_flags(current_encoder, requested_bitrate_kbps, g_dur, target_fps_expr, quality_level=self.quality_level, size_locked=bool(self.target_mb))
                    v_label, working_duration_sec, cfr_filter = "[0:v]", g_dur, f"fps={target_fps_expr}:round=near"
                    attempt_core_filters = [granular_v_a_filters] if granular_v_a_filters else []
                    v_stabilized_pad, a_prepared_pad = g_v, g_a
                    if not granular_v_a_filters:
                        attempt_core_filters.extend([f"{v_label}setpts='(PTS-STARTPTS)/{self.speed_factor:.4f}',{cfr_filter}[v_stabilized]", f"[0:a]asetpts=PTS-STARTPTS,atempo={self.speed_factor:.4f},aresample=48000:async=1[a_prepared_base]" if source_has_audio else f"anullsrc=r=48000:cl=stereo,atrim=duration={working_duration_sec:.4f},asetpts=PTS-STARTPTS[a_prepared_base]"])
                        v_stabilized_pad, a_prepared_pad = "[v_stabilized]", "[a_prepared_base]"
                    if intro_duration_sec > 0.0 and intro_input_index is not None:
                        intro_frames = max(1, int(round(intro_duration_sec * 60.0)))
                        loop_frames = max(0, intro_frames - 1)
                        attempt_core_filters.append(f"[{intro_input_index}:v]trim=duration={max(0.2, intro_duration_sec + 0.1):.4f},setpts=PTS-STARTPTS,select='eq(n\\,0)',setsar=1,loop=loop={loop_frames}:size=1:start=0,fps={target_fps_expr}:round=near,trim=duration={intro_duration_sec:.4f},setpts=PTS-STARTPTS[v_intro_same_frame]")
                        attempt_core_filters.append(f"{v_stabilized_pad}setsar=1[v_main_after_intro]")
                        attempt_core_filters.append("[v_intro_same_frame][v_main_after_intro]concat=n=2:v=1:a=0[v_with_intro]")
                        v_stabilized_pad = "[v_with_intro]"
                    if self.is_mobile_format:
                        v_mobile, v_mobile_out = self.filter_builder.build_mobile_filter_chain(v_stabilized_pad, self.config.get_mobile_coordinates(self.logger), self.is_boss_hp, self.show_teammates_overlay, self.show_spectating_overlay, text_input_label, False, self.original_resolution)
                        attempt_core_filters.append(v_mobile); v_output_pad = v_mobile_out
                    else: v_output_pad = v_stabilized_pad
                    attempt_final_a_label = final_a_label
                    for part in audio_chains: attempt_core_filters.append(part.replace("[0:a]", a_prepared_pad))
                    if intro_duration_sec > 0.0:
                        attempt_core_filters.append(f"anullsrc=r=48000:cl=stereo,atrim=duration={intro_duration_sec:.4f},asetpts=PTS-STARTPTS[a_intro_silence]")
                        attempt_core_filters.append(f"[a_intro_silence]{attempt_final_a_label}concat=n=2:v=0:a=1[a_with_intro]")
                        attempt_final_a_label = "[a_with_intro]"
                    attempt_core_filters.append(f"{v_output_pad}fps={target_fps_expr}:round=near,setpts=N/({target_fps_expr})/TB[v_render_out]")
                    filter_script_path = os.path.join(self.temp_job_dir, "filter_complex.txt")
                    with open(filter_script_path, 'w', encoding='utf-8') as f: f.write(";".join([p for p in attempt_core_filters if p]))
                    ffmpeg_inputs = self._hardware_decode_flags(current_encoder) + ['-ss', f"{self.start_time_ms/1000.0:.3f}", '-t', f"{(self.end_time_ms-self.start_time_ms)/1000.0:.3f}", '-i', self.input_path]
                    for t, _, _ in self.music_tracks: ffmpeg_inputs += ['-i', t]
                    if intro_input_index is not None:
                        source_duration_sec = self.prober.get_duration()
                        intro_abs_sec = (float(self.intro_abs_time_ms) / 1000.0) if self.intro_abs_time_ms is not None else (float(self.start_time_ms) / 1000.0)
                        if source_duration_sec and source_duration_sec > 0.25:
                            intro_abs_sec = min(max(0.0, intro_abs_sec), max(0.0, source_duration_sec - 0.2))
                        ffmpeg_inputs += self._hardware_decode_flags(current_encoder) + ['-ss', f"{intro_abs_sec:.3f}", '-t', f"{max(0.2, intro_duration_sec + 0.1):.3f}", '-i', self.input_path]
                    if text_png_path: ffmpeg_inputs += ['-loop', '1', '-i', text_png_path]
                    ffmpeg_cmd = [self.ffmpeg_path, '-y', '-hide_banner', '-progress', 'pipe:1'] + ffmpeg_inputs + ['-filter_complex_script', filter_script_path, '-map', '[v_render_out]', '-map', attempt_final_a_label, '-c:v', vcodec[1]] + vcodec[2:] + ['-c:a', 'aac', '-b:a', f"{audio_kbps}k", '-t', f"{render_duration_sec:.3f}", '-movflags', '+faststart', core_path]
                    if self.logger:
                        bitrate_label = f"{int(requested_bitrate_kbps)}k" if requested_bitrate_kbps else "CQ"
                        self.logger.info(f"FFMPEG CMD (Encoder: {current_encoder}, RC: {rc_label}, Bitrate: {bitrate_label}): {' '.join(ffmpeg_cmd)}")
                    self.current_process = create_subprocess(ffmpeg_cmd)
                    monitor_stats = monitor_ffmpeg_progress(self.current_process, render_duration_sec, scaler_core, self._monitor_disk_space, self.logger)
                    if self.current_process.wait() == 0:
                        valid, err_msg = self._validate_render_output(core_path, render_duration_sec, target_fps_expr, monitor_stats)
                        if valid: return True, render_duration_sec, monitor_stats
                        last_error = err_msg
                    else:
                        last_error = f"FFmpeg exited with code {self.current_process.returncode}."
                    if use_cuda and not self.is_canceled:
                        fallbacks = self.encoder_mgr.get_fallback_list(current_encoder, False)
                        if fallbacks: 
                            if self.logger: self.logger.warning(f"FFmpeg failed with {current_encoder}, falling back to {fallbacks[0]}")
                            current_encoder = fallbacks[0]; continue
                    return False, g_dur, {}

            size_bounds = self._target_size_bounds()
            current_bitrate = int(video_bitrate_kbps) if video_bitrate_kbps else None
            for attempt in range(1, 3):
                if os.path.exists(core_path): os.remove(core_path)
                success, _, monitor_stats = run_ffmpeg(self.hardware_strategy != 'CPU', current_bitrate)
                if not success or not size_bounds: break
                actual = os.path.getsize(core_path)
                if size_bounds[0] <= actual <= size_bounds[1]: break
                current_bitrate = int(current_bitrate * (Fraction(size_bounds[1]) / Fraction(actual)))
            if not success:
                self._emit_finished(False, last_error)
                return
            final_output = self._resolve_final_output_path()
            shutil.move(core_path, final_output)
            self._emit_progress(100)
            self._emit_finished(True, final_output)
        except Exception as e:
            if self.logger: self.logger.exception(f"FATAL: {e}")
            self._emit_finished(False, str(e))
        finally:
            if hasattr(self, 'temp_job_dir'): shutil.rmtree(self.temp_job_dir, ignore_errors=True)
