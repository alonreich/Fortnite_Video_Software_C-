class TimeSyncEngine:
    @staticmethod
    def calculate_wall_clock_ms(video_ms: float, segments: list, base_speed: float) -> float:
        base_speed = max(0.01, float(base_speed))
        if not segments or not isinstance(segments, list):
            return float(video_ms) / base_speed
        current_video_time = 0.0
        accumulated_wall_time = 0.0
        target = float(video_ms)
        sorted_segments = sorted(segments, key=lambda x: x.get('start_ms', x.get('start', 0)))
        for seg in sorted_segments:
            start = float(seg.get('start_ms', seg.get('start', 0)))
            end = float(seg.get('end_ms', seg.get('end', 0)))
            speed = max(0.01, float(seg.get('speed', base_speed)))
            if start >= target:
                break
            if start > current_video_time:
                gap_dur = start - current_video_time
                accumulated_wall_time += gap_dur / base_speed
                current_video_time = start
            effective_end = min(end, target)
            if effective_end > current_video_time:
                seg_dur = effective_end - current_video_time
                accumulated_wall_time += seg_dur / speed
                current_video_time = effective_end
        if current_video_time < target:
            remaining = target - current_video_time
            accumulated_wall_time += remaining / base_speed
        return accumulated_wall_time
    @staticmethod
    def calculate_video_time_ms(wall_clock_ms: float, segments: list, base_speed: float) -> float:
        base_speed = max(0.01, float(base_speed))
        if not segments or not isinstance(segments, list):
            return float(wall_clock_ms) * base_speed
        current_wall_time = 0.0
        accumulated_video_time = 0.0
        target_wall = float(wall_clock_ms)
        sorted_segments = sorted(segments, key=lambda x: x.get('start_ms', x.get('start', 0)))
        last_video_pos = 0.0
        for seg in sorted_segments:
            start = float(seg.get('start_ms', seg.get('start', 0)))
            end = float(seg.get('end_ms', seg.get('end', 0)))
            speed = max(0.01, float(seg.get('speed', base_speed)))
            if start > last_video_pos:
                gap_v_dur = start - last_video_pos
                gap_w_dur = gap_v_dur / base_speed
                if current_wall_time + gap_w_dur >= target_wall:
                    remaining_w = target_wall - current_wall_time
                    return accumulated_video_time + (remaining_w * base_speed)
                current_wall_time += gap_w_dur
                accumulated_video_time += gap_v_dur
            seg_v_dur = end - start
            seg_w_dur = seg_v_dur / speed
            if current_wall_time + seg_w_dur >= target_wall:
                remaining_w = target_wall - current_wall_time
                return start + (remaining_w * speed)
            current_wall_time += seg_w_dur
            accumulated_video_time = end
            last_video_pos = end
        remaining_w = target_wall - current_wall_time
        return accumulated_video_time + (remaining_w * base_speed)
