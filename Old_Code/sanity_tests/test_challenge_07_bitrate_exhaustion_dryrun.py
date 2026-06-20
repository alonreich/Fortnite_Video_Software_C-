from sanity_tests._ai_sanity_helpers import assert_all_present, read_source

def test_challenge_07_bitrate_exhaustion_dryrun() -> None:
    src = read_source("processing/media_utils.py")
    assert_all_present(
        src,
        [
            "if video_bits <= 0:",
            "return 300",
            "final_kbps = max(300, min(calculated_kbps, max_h264_level_42_kbps))",
            "return final_kbps",
        ],
    )
