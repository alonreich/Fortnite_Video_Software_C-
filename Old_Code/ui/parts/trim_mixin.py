from PyQt5.QtWidgets import QStyle

class TrimMixin:
    MIN_TRIM_GAP = 1000

    def _update_trim_inputs(self):
        if not hasattr(self, "start_minute_input"): return
        if getattr(self, "_restoring_recovery_state", False): return
        total_ms = getattr(self, "original_duration_ms", 0)
        total_seconds = total_ms // 1000
        max_m = total_seconds // 60
        max_s = total_seconds % 60
        max_ms = total_ms % 1000
        self.start_minute_input.setRange(0, max_m)
        self.start_second_input.setRange(0, 59)
        self.start_ms_input.setRange(0, 999)
        self.end_minute_input.setRange(0, max_m)
        self.end_second_input.setRange(0, 59)
        self.end_ms_input.setRange(0, 999)
        self.start_minute_input.setValue(0)
        self.start_second_input.setValue(0)
        self.start_ms_input.setValue(0)
        self.end_minute_input.setValue(max_m)
        self.end_second_input.setValue(max_s)
        self.end_ms_input.setValue(max_ms)
        self.trim_start_ms = 0
        self.trim_end_ms = total_ms
        if hasattr(self, "_on_slider_trim_changed"):
            self._on_slider_trim_changed(self.trim_start_ms, self.trim_end_ms)
        elif hasattr(self, "positionSlider") and self.positionSlider:
            self.positionSlider.set_trim_times(self.trim_start_ms, self.trim_end_ms)
    
    def set_start_time(self):
        if not hasattr(self, "positionSlider") or not self.positionSlider: return
        pos_ms = int(self.positionSlider.value())
        if getattr(self, "trim_end_ms", 0) > 0:
            if pos_ms > self.trim_end_ms - self.MIN_TRIM_GAP:
                self.trim_end_ms = min(getattr(self, "original_duration_ms", 0), pos_ms + self.MIN_TRIM_GAP)
                if self.trim_end_ms - pos_ms < self.MIN_TRIM_GAP:
                    pos_ms = max(0, self.trim_end_ms - self.MIN_TRIM_GAP)
        self.trim_start_ms = max(0, pos_ms)
        if hasattr(self, "logger"):
            self.logger.info("TRIM: start set at %d ms from slider", self.trim_start_ms)
        if hasattr(self, "_on_slider_trim_changed"):
            self._on_slider_trim_changed(self.trim_start_ms, self.trim_end_ms)
            self.positionSlider.set_trim_times(self.trim_start_ms, self.trim_end_ms)
        else:
            self._update_trim_widgets_from_trim_times()
            self.positionSlider.set_trim_times(self.trim_start_ms, self.trim_end_ms)

    def set_end_time(self):
        if not hasattr(self, "positionSlider") or not self.positionSlider: return
        pos_ms = int(self.positionSlider.value())
        if not hasattr(self, "trim_start_ms") or self.trim_start_ms < 0:
            self.trim_start_ms = 0
        if pos_ms < self.trim_start_ms + self.MIN_TRIM_GAP:
            self.trim_start_ms = max(0, pos_ms - self.MIN_TRIM_GAP)
            if pos_ms - self.trim_start_ms < self.MIN_TRIM_GAP:
                pos_ms = min(getattr(self, "original_duration_ms", 0), self.trim_start_ms + self.MIN_TRIM_GAP)
        if getattr(self, "original_duration_ms", 0) > 0 and pos_ms > self.original_duration_ms:
            pos_ms = self.original_duration_ms
        self.trim_end_ms = pos_ms
        if hasattr(self, "logger"):
            self.logger.info("TRIM: end set at %d ms from slider (start=%d)", self.trim_end_ms, self.trim_start_ms)
        if hasattr(self, "_on_slider_trim_changed"):
            self._on_slider_trim_changed(self.trim_start_ms, self.trim_end_ms)
            self.positionSlider.set_trim_times(self.trim_start_ms, self.trim_end_ms)
        else:
            self._update_trim_widgets_from_trim_times()
            self.positionSlider.set_trim_times(self.trim_start_ms, self.trim_end_ms)
        is_playing = getattr(self, "player", None) and not getattr(self.player, "pause", True)
        if is_playing:
            self._safe_mpv_set("pause", True)
            if hasattr(self, "playPauseButton"):
                self.playPauseButton.setText("PLAY")
                self.playPauseButton.setIcon(self.style().standardIcon(QStyle.SP_MediaPlay))
    
    def _update_trim_widgets_from_trim_times(self):
        if not hasattr(self, "start_minute_input") or self.start_minute_input is None:
            return
        inputs = (self.start_minute_input, self.start_second_input, self.start_ms_input, 
                  self.end_minute_input, self.end_second_input, self.end_ms_input)
        for spinbox in inputs:
            spinbox.blockSignals(True)
        try:
            ts = getattr(self, "trim_start_ms", 0)
            te = getattr(self, "trim_end_ms", 0)
            s_total_s = ts // 1000
            self.start_minute_input.setValue(s_total_s // 60)
            self.start_second_input.setValue(s_total_s % 60)
            self.start_ms_input.setValue(ts % 1000)
            e_total_s = te // 1000
            self.end_minute_input.setValue(e_total_s // 60)
            self.end_second_input.setValue(e_total_s % 60)
            self.end_ms_input.setValue(te % 1000)
        except Exception:
            pass
        finally:
            for spinbox in inputs:
                spinbox.blockSignals(False)

    def _on_trim_spin_changed(self):
        if not hasattr(self, "start_minute_input") or self.start_minute_input is None:
            return
        try:
            start_ms = (self.start_minute_input.value() * 60 * 1000) + \
                       (self.start_second_input.value() * 1000) + \
                       self.start_ms_input.value()
            end_ms = (self.end_minute_input.value() * 60 * 1000) + \
                     (self.end_second_input.value() * 1000) + \
                     self.end_ms_input.value()
            dur = getattr(self, "original_duration_ms", 0)
            if dur > 0:
                start_ms = max(0, min(start_ms, dur))
                end_ms = max(0, min(end_ms, dur))
                if end_ms < start_ms + self.MIN_TRIM_GAP:
                    end_ms = min(dur, start_ms + self.MIN_TRIM_GAP)
                    if end_ms < start_ms + self.MIN_TRIM_GAP:
                        start_ms = max(0, end_ms - self.MIN_TRIM_GAP)
            self.trim_start_ms = int(start_ms)
            self.trim_end_ms = int(end_ms)
            if hasattr(self, "_on_slider_trim_changed"):
                self._on_slider_trim_changed(self.trim_start_ms, self.trim_end_ms)
            elif hasattr(self, "positionSlider") and self.positionSlider:
                self.positionSlider.set_trim_times(self.trim_start_ms, self.trim_end_ms)
        except Exception:
            pass
