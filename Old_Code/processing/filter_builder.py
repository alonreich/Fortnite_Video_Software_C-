from fractions import Fraction
from .processing_utils import make_multiple, make_even, fps_to_float
from .filter_mobile import MobileFilterMixin

class FilterResult(tuple):
    def __contains__(self, item):
        return any(item in str(x) for x in self)

class AudioFilterMixin:
    def build_audio_chain(self, music_config, video_start_time, video_end_time, speed_factor, disable_fades, vfade_in_d, audio_filter_cmd, sample_rate=48000, music_tracks=None, music_start_index=1, total_project_duration=None, main_audio_label="[0:a]", volume_normalize_db=0.0):
        chain = []
        music_config = music_config if isinstance(music_config, dict) else {}
        target_sample_rate = sample_rate or 48000
        raw_parts = []
        if isinstance(audio_filter_cmd, list): raw_parts.extend(audio_filter_cmd)
        elif audio_filter_cmd: raw_parts.append(audio_filter_cmd)
        if vfade_in_d > 0: raw_parts.append(f"afade=t=in:st=0:d={vfade_in_d:.3f}")
        cleaned_parts = []

        def flatten(item):
            if isinstance(item, list):
                for sub in item: flatten(sub)
            elif item:
                s = str(item).strip().strip(",")
                if s: cleaned_parts.append(s)
        flatten(raw_parts)
        if not cleaned_parts: cleaned_parts = ["anull"]
        main_audio_filter = ",".join(cleaned_parts)
        main_duration = total_project_duration if total_project_duration is not None else ((video_end_time - video_start_time) / speed_factor if speed_factor else (video_end_time - video_start_time))
        if main_audio_label:
            chain.append(f"{main_audio_label}{main_audio_filter}[a_main_raw]")
        else:
            chain.append(f"anullsrc=r={target_sample_rate}:cl=stereo,atrim=duration={max(0.01, float(main_duration)):.4f},asetpts=PTS-STARTPTS[a_main_raw]")

        def normalize_music_track(track, fallback_duration=0.0):
            if isinstance(track, dict):
                path = track.get("path")
                offset = track.get("offset_sec", track.get("offset", track.get("file_offset_sec", 0.0)))
                duration = track.get("duration_sec", track.get("duration", track.get("dur", fallback_duration)))
            elif isinstance(track, (list, tuple)) and len(track) >= 1:
                path = track[0]
                offset = track[1] if len(track) >= 2 else 0.0
                duration = track[2] if len(track) >= 3 else fallback_duration
            else:
                return None
            if not path:
                return None
            try:
                offset_val = max(0.0, float(offset or 0.0))
            except Exception:
                offset_val = 0.0
            try:
                dur_val = max(0.0, float(duration or 0.0))
            except Exception:
                dur_val = 0.0
            if dur_val <= 0.001 and fallback_duration:
                dur_val = max(0.001, float(fallback_duration))
            return (str(path), offset_val, dur_val)
        tracks = []
        fallback_music_duration = total_project_duration if total_project_duration is not None else main_duration
        for raw_track in list(music_tracks or []):
            normalized = normalize_music_track(raw_track, fallback_music_duration)
            if normalized:
                tracks.append(normalized)
        if not tracks and music_config and music_config.get("path"):
            path = music_config.get("path")
            offset = music_config.get("file_offset_sec", 0.0)
            if total_project_duration is not None: dur = total_project_duration
            else: dur = (video_end_time - video_start_time) / speed_factor
            tracks = [(path, offset, dur)]
        music_window_sec = None
        if music_config:
            try:
                m_start = float(music_config.get('timeline_start_sec', 0.0) or 0.0)
                m_end = float(music_config.get('timeline_end_sec', 0.0) or 0.0)
                if m_end > m_start: music_window_sec = max(0.0, m_end - m_start)
            except Exception: music_window_sec = None
        if tracks and music_window_sec is not None:
            clipped_tracks = []; remaining = music_window_sec
            for path, offset, dur in tracks:
                try:
                    dur_val, offset_val = max(0.0, float(dur)), max(0.0, float(offset))
                    if dur_val <= 0.001:
                        dur_val = remaining
                except Exception: continue
                take = min(dur_val, remaining)
                if take > 0.001:
                    clipped_tracks.append((path, offset_val, take)); remaining -= take
                if remaining <= 0.001: break
            tracks = clipped_tracks
        if not tracks:
            v_vol_single = float(music_config.get('main_vol', music_config.get('video_volume', 0.8))) if music_config else 0.8
            if volume_normalize_db != 0.0:
                v_vol_single *= (10**(volume_normalize_db / 20.0))
            chain.append(f"[a_main_raw]volume={v_vol_single:.4f},aresample={target_sample_rate}:async=1[a_main_prepared]")
            return chain, "[a_main_prepared]"
        initial_delay_sec = 0.0
        if music_config:
            try:
                m_start_proj = float(music_config.get('timeline_start_sec', 0.0))
            except Exception:
                m_start_proj = 0.0
            initial_delay_sec = max(0.0, m_start_proj)
        prepared_music_labels = []; accum_project_sec = initial_delay_sec
        for i, (path, file_offset, dur_sec) in enumerate(tracks):
            input_label, out_label, pre_label = f"[{music_start_index + i}:a]", f"[a_mus_{i}]", f"[a_mus_{i}_pre]"
            music_filters = [f"atrim=start={file_offset:.3f}:duration={dur_sec:.3f}", "asetpts=PTS-STARTPTS"]
            if not disable_fades and dur_sec > 0.5:
                FADE_DUR = min(0.5, dur_sec / 4.0)
                music_filters.append(f"afade=t=in:st=0:d={FADE_DUR:.3f}")
                music_filters.append(f"afade=t=out:st={max(0.0, dur_sec - FADE_DUR):.3f}:d={FADE_DUR:.3f}")
            m_vol = float(music_config.get('music_vol', music_config.get('volume', 0.8))) if music_config else 0.8
            music_filters.append(f"volume={m_vol:.4f}")
            chain.append(f"{input_label}{','.join(music_filters)}{pre_label}")
            delay_ms = int(accum_project_sec * 1000)
            if delay_ms > 0: chain.append(f"{pre_label}adelay={delay_ms}|{delay_ms}{out_label}")
            else: chain.append(f"{pre_label}anull{out_label}")
            prepared_music_labels.append(out_label)
            accum_project_sec += dur_sec
        if len(prepared_music_labels) > 1:
            mix_inputs = "".join(prepared_music_labels)
            chain.append(f"{mix_inputs}amix=inputs={len(prepared_music_labels)}:duration=longest:dropout_transition=0:weights='{' '.join(['1']*len(prepared_music_labels))}'[a_bg_music]")
            bg_music_label = "[a_bg_music]"
        else: bg_music_label = prepared_music_labels[0]
        v_vol = float(music_config.get('main_vol', music_config.get('video_volume', 0.8))) if music_config else 0.8
        if volume_normalize_db != 0.0:
            v_vol *= (10**(volume_normalize_db / 20.0))
        chain.append(f"[a_main_raw]volume={v_vol:.4f}[game_scaled]")
        chain.append(f"[game_scaled]asplit=2[game_out_pre][game_trig]")
        chain.append("[game_trig]highpass=f=200,lowpass=f=3500,agate=threshold=0.05:attack=5:release=100[trig_cleaned]")
        chain.append("[trig_cleaned]equalizer=f=1000:t=q:w=2:g=10[trig_final]")
        chain.append(f"{bg_music_label}asplit=2[mus_base][mus_to_filter]")
        chain.append("[mus_base]lowpass=f=150[mus_low]")
        chain.append("[mus_to_filter]highpass=f=150[mus_high]")
        d_thresh, d_ratio = music_config.get('ducking_threshold', 0.15), music_config.get('ducking_ratio', 2.5)
        duck_params = f"threshold={d_thresh}:ratio={d_ratio}:attack=1:release=400:detection=rms"
        chain.append(f"[mus_high][trig_final]sidechaincompress={duck_params}[mus_high_ducked]")
        chain.append("[mus_low][mus_high_ducked]amix=inputs=2:weights='1 1':normalize=0[a_music_reconstructed]")
        chain.append(f"[game_out_pre][a_music_reconstructed]amix=inputs=2:duration=first:dropout_transition=3:weights='1 1':normalize=0,dynaudnorm=f=150:g=15,alimiter=limit=0.95:attack=5:release=50,aresample={target_sample_rate}:async=1[a_music_prepared]")
        return [c for c in chain if c and c.strip()], "[a_music_prepared]"

class FilterBuilder(AudioFilterMixin, MobileFilterMixin):
    def __init__(self, logger=None):
        self.logger = logger

    def build_granular_speed_chain(self, input_path=None, total_duration_ms=0, segments=None, base_speed=1.0, source_cut_start_ms=0, input_v_label="[0:v]", input_a_label="[0:a]", target_fps="60", video_path=None, duration_ms=None, speed_segments=None, input_is_cuda=False):
        input_path = input_path or video_path; total_duration_ms = total_duration_ms or duration_ms or 0
        segments = segments or speed_segments or []; total_duration_sec = float(total_duration_ms) / 1000.0
        timeline_origin_sec = float(source_cut_start_ms or 0.0) / 1000.0
        input_v_work_label = input_v_label
        pre_chain_parts = []

        def _to_clip_relative_sec(t_abs_sec):
            try: rel = float(t_abs_sec) - timeline_origin_sec
            except: rel = 0.0
            return max(0.0, min(rel, total_duration_sec))

        def _read_segment(seg):
            if not isinstance(seg, dict):
                return None
            try:
                start = _to_clip_relative_sec(float(seg.get('start', seg.get('start_ms', 0))) / 1000.0)
                end = _to_clip_relative_sec(float(seg.get('end', seg.get('end_ms', 0))) / 1000.0)
                speed = float(seg.get('speed', base_speed))
            except Exception:
                return None
            if end <= start + 0.001:
                return None
            return {'start': start, 'end': end, 'speed': speed}
        normalized_segments = []
        for raw_seg in segments:
            normalized = _read_segment(raw_seg)
            if normalized is not None:
                normalized_segments.append(normalized)
        normalized_segments.sort(key=lambda item: (item['start'], item['end']))
        source_chunks = []; current_sec = 0.0
        speed_segs = [s for s in normalized_segments if abs(float(s.get('speed', base_speed))) > 0.001]
        for seg in speed_segs:
            s_start = float(seg['start'])
            s_end = float(seg['end'])
            if s_start < current_sec:
                s_start = current_sec
            if s_end <= s_start + 0.001:
                continue
            if s_start > current_sec + 0.001:
                source_chunks.append({'start': current_sec, 'end': s_start, 'speed': float(base_speed)})
            source_chunks.append({'start': s_start, 'end': s_end, 'speed': float(seg['speed'])})
            current_sec = s_end
        if current_sec < total_duration_sec - 0.001:
            source_chunks.append({'start': current_sec, 'end': total_duration_sec, 'speed': float(base_speed)})
        freezes = sorted([s for s in normalized_segments if abs(float(s.get('speed', base_speed))) < 0.001], key=lambda x: x['start'])

        def append_source_range(out_chunks, range_start, range_end):
            for source_chunk in source_chunks:
                overlap_start = max(float(range_start), float(source_chunk['start']))
                overlap_end = min(float(range_end), float(source_chunk['end']))
                if overlap_end > overlap_start + 0.001:
                    out_chunks.append({'start': overlap_start, 'end': overlap_end, 'speed': float(source_chunk['speed'])})
        chunks = []
        source_cursor = 0.0
        for f in freezes:
            f_start = max(source_cursor, float(f['start']))
            f_end = max(f_start, float(f['end']))
            if f_end <= source_cursor + 0.001:
                continue
            if f_start > source_cursor + 0.001:
                append_source_range(chunks, source_cursor, f_start)
            f_dur = max(0.001, f_end - f_start)
            chunks.append({'start': f_start, 'end': f_start + 0.001, 'speed': 0.0, 'freeze_dur': f_dur})
            source_cursor = f_start
        if total_duration_sec > source_cursor + 0.001:
            append_source_range(chunks, source_cursor, total_duration_sec)

        def time_mapper(timeline_sec):
            target = _to_clip_relative_sec(timeline_sec)
            mapped = 0.0
            for ch in chunks:
                ch_start, ch_end, ch_speed = ch['start'], ch['end'], ch['speed']
                if abs(ch_speed) < 0.001:
                    f_dur = ch.get('freeze_dur', 0.0)
                    if target >= ch_start: mapped += f_dur
                    continue
                if target <= ch_start: break
                if target >= ch_end: mapped += (ch_end - ch_start) / ch_speed
                else:
                    mapped += (target - ch_start) / ch_speed
                    break
            return max(0.0, mapped)
        n_chunks = len(chunks)
        if self.logger:
            try:
                self.logger.info(
                    "GRANULAR_CHAIN_STATE: chunks=%d input_is_cuda=%s base_speed=%.3f source_cut_ms=%d total_ms=%d normalized=%s",
                    n_chunks, bool(input_is_cuda), float(base_speed),
                    int(source_cut_start_ms or 0), int(total_duration_ms or 0), normalized_segments,
                )
            except Exception:
                pass
        if n_chunks == 0:
            v_chain = f"{input_v_work_label}setpts='(PTS-STARTPTS)/{base_speed:.4f}'[v_speed_out]"
            tmp_s = float(base_speed); audio_speed_filters = []
            while tmp_s < 0.5: audio_speed_filters.append("atempo=0.5"); tmp_s /= 0.5
            while tmp_s > 2.0: audio_speed_filters.append("atempo=2.0"); tmp_s /= 2.0
            audio_speed_filters.append(f"atempo={tmp_s:.4f}")
            if input_a_label:
                a_chain = f"{input_a_label}asetpts=PTS-STARTPTS,{','.join(audio_speed_filters)},aresample=48000:async=1:min_comp=0.01[a_speed_out]"
            else:
                a_chain = f"anullsrc=r=48000:cl=stereo,atrim=duration={total_duration_sec/base_speed:.4f},asetpts=PTS-STARTPTS[a_speed_out]"
            return ";".join(pre_chain_parts + [v_chain, a_chain]), "[v_speed_out]", "[a_speed_out]", (total_duration_sec/base_speed), time_mapper
        full_chain_parts = list(pre_chain_parts); v_a_pads, final_duration = [], 0.0
        v_splits = "".join([f"[v_split_{i}]" for i in range(n_chunks)])
        full_chain_parts.append(f"{input_v_work_label}split={n_chunks}{v_splits}")
        if input_a_label:
            a_splits = "".join([f"[a_split_{i}]" for i in range(n_chunks)])
            full_chain_parts.append(f"{input_a_label}asplit={n_chunks}{a_splits}")
        try:
            _fps_value = float(Fraction(str(target_fps)))
        except Exception:
            _fps_value = 60.0
        if _fps_value <= 0:
            _fps_value = 60.0
        for i, chunk in enumerate(chunks):
            start, end, speed = chunk['start'], chunk['end'], chunk['speed']; v_src = f"[v_split_{i}]"; a_src = f"[a_split_{i}]" if input_a_label else None; v_chunk_label = f"[v_chunk_{i}]"; a_chunk_label = f"[a_chunk_{i}]"
            if abs(speed) < 0.001:
                dur = chunk.get('freeze_dur', end - start)
                target_frame_count = max(1, int(round(dur * _fps_value)))
                loop_frames = max(0, target_frame_count - 1)
                sample_window = max(4.0 / _fps_value, 0.20)
                sample_until = min(total_duration_sec, start + sample_window)
                sample_window_actual = max(1.0 / _fps_value, sample_until - start)
                full_chain_parts.append(
                    f"{v_src}trim=start={start:.4f}:duration={sample_window_actual:.4f},"
                    f"setpts=PTS-STARTPTS,"
                    f"select='lte(n\\,0)',"
                    f"format=yuv420p,setsar=1,"
                    f"loop=loop={loop_frames}:size=1:start=0,"
                    f"fps={target_fps}:round=near,"
                    f"setpts=N/({target_fps})/TB,"
                    f"trim=duration={dur:.4f},setpts=PTS-STARTPTS{v_chunk_label}"
                )
                if input_a_label:
                    full_chain_parts.append(f"{a_src}anullsink")
                full_chain_parts.append(f"anullsrc=r=48000:cl=stereo,atrim=duration={dur:.4f},asetpts=PTS-STARTPTS{a_chunk_label}")
                out_dur = dur
            else:
                out_dur = (end - start) / speed
                full_chain_parts.append(f"{v_src}trim=start={start:.4f}:end={end:.4f},setpts=PTS-STARTPTS,setpts='PTS/{speed:.4f}',format=yuv420p,setsar=1{v_chunk_label}")
                tmp_s = speed; audio_speed_filters = []
                while tmp_s < 0.5: audio_speed_filters.append("atempo=0.5"); tmp_s /= 0.5
                while tmp_s > 2.0: audio_speed_filters.append("atempo=2.0"); tmp_s /= 2.0
                audio_speed_filters.append(f"atempo={tmp_s:.4f}")
                if input_a_label:
                    full_chain_parts.append(f"{a_src}atrim=start={start:.4f}:end={end:.4f},asetpts=PTS-STARTPTS,{','.join(audio_speed_filters)},asetpts=PTS-STARTPTS,aresample=48000:async=1:min_comp=0.001{a_chunk_label}")
                else:
                    full_chain_parts.append(f"anullsrc=r=48000:cl=stereo,atrim=duration={out_dur:.4f},asetpts=PTS-STARTPTS{a_chunk_label}")
            v_a_pads.append(f"{v_chunk_label}{a_chunk_label}"); final_duration += out_dur
        full_chain_parts.append(f"{''.join(v_a_pads)}concat=n={n_chunks}:v=1:a=1[v_speed_concat][a_speed_concat]")
        full_chain_parts.append(f"[v_speed_concat]setpts=PTS-STARTPTS[v_speed_out]")
        full_chain_parts.append(f"[a_speed_concat]aresample=48000:async=1:min_comp=0.01,asetpts=PTS-STARTPTS[a_speed_out]")
        return ";".join(full_chain_parts), "[v_speed_out]", "[a_speed_out]", final_duration, time_mapper
