from sanity_tests._ai_sanity_helpers import assert_all_present, read_source

def test_core_01_constant_tempo_dryrun() -> None:
    src = read_source("ui/parts/player_mixin.py")
    assert_all_present(
        src,
        [
            '_host_mpv_set(self, "speed", 1.0, target_player=music_player)',
            '_host_mpv_command(self, "seek", target_m_sec, "absolute", "exact", target_player=music_player)',
            "project_pos_sec = (wall_now - wall_start) / 1000.0",
        ],
    )
