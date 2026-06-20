from sanity_tests._ai_sanity_helpers import assert_all_present, read_source

def test_challenge_01_granular_speed_sync_dryrun() -> None:
    src = read_source("ui/parts/player_mixin.py")
    assert_all_present(
        src,
        [
            "def _calculate_wall_clock_time(self, video_ms, segments, base_speed):",
            "accumulated_wall_time += partial_dur / speed",
            "project_pos_sec = (wall_now - wall_start) / 1000.0",
        ],
    )
