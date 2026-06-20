from sanity_tests._ai_sanity_helpers import assert_all_present, read_source

def test_challenge_09_constant_pitch_dryrun() -> None:
    src = read_source("ui/parts/player_mixin.py")
    assert_all_present(
        src,
        [
            '_host_mpv_set(self, "speed", 1.0, target_player=music_player)',
            "speed_factor = self.speed_spinbox.value() if hasattr(self, 'speed_spinbox') else 1.1",
            "project_pos_sec = (wall_now - wall_start) / 1000.0",
        ],
    )
