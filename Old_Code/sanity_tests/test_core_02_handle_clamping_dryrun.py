from sanity_tests._ai_sanity_helpers import assert_all_present, read_source

def test_core_02_handle_clamping_dryrun() -> None:
    src = read_source("ui/widgets/trimmed_slider.py")
    assert_all_present(
        src,
        [
            "new_start_ms = max(video_trim_start_ms, new_start_ms)",
            "new_end_ms = min(video_trim_end_ms, new_end_ms)",
            "self.music_start_ms = min(new_start_ms, self.music_end_ms - 100)",
            "self.music_end_ms = max(self.music_start_ms + 100, new_end_ms)",
        ],
    )
