from sanity_tests._ai_sanity_helpers import assert_all_present, read_source

def test_challenge_03_dj_scrubbing_dryrun() -> None:
    src = read_source("ui/parts/player_mixin.py")
    assert_all_present(
        src,
        [
            'if last_scrub_ts and (now - last_scrub_ts) < 0.05:',
            '_host_mpv_command(self, "seek", target_m_sec, "absolute", "exact", target_player=music_player)',
        ],
    )
