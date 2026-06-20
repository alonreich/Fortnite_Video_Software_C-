from sanity_tests._ai_sanity_helpers import assert_all_present, read_source

def test_challenge_05_trimming_conflict_dryrun() -> None:
    src = read_source("ui/parts/main_window_core_c.py")
    assert_all_present(
        src,
        [
            "def _on_slider_trim_changed(self, start_ms, end_ms):",
            'm_s = getattr(self, "music_timeline_start_ms", 0)',
            "self.music_timeline_start_ms = max(start_ms, min(m_s, end_ms))",
            "self.positionSlider.set_music_times(self.music_timeline_start_ms, self.music_timeline_end_ms)",
        ],
    )
