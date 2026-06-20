from sanity_tests._ai_sanity_helpers import assert_all_present, read_source

def test_challenge_08_ultra_wide_scaling_dryrun() -> None:
    src = read_source("processing/filter_mobile.py")
    assert_all_present(
        src,
        [
            "scale={TARGET_W}:{TARGET_H}:force_original_aspect_ratio=increase:flags=lanczos,crop={TARGET_W}:{TARGET_H}",
            "scale={CONTENT_W}:{CONTENT_H}:flags=lanczos,pad={PORTRAIT_W}:{PORTRAIT_H}:0:{PADDING_TOP}:black",
        ],
    )
