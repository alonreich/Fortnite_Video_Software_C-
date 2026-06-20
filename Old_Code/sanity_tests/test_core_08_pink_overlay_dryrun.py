from sanity_tests._ai_sanity_helpers import assert_all_present, read_source

def test_core_08_pink_overlay_dryrun() -> None:
    src = read_source("ui/parts/music_mixin.py")
    assert_all_present(
        src,
        [
            "self.positionSlider.set_music_visible(True)",
            "self.positionSlider.set_music_times(t_s, t_e)",
            "self.music_timeline_start_ms = t_s",
            "self.music_timeline_end_ms = t_e",
        ],
    )
