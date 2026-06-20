from sanity_tests._ai_sanity_helpers import assert_all_present, read_source

def test_core_04_smart_fading_dryrun() -> None:
    src = read_source("processing/filter_builder.py")
    assert_all_present(
        src,
        [
            "FADE_DUR = min(0.5, dur_sec / 4.0)",
            "afade=t=in:st=0:d={FADE_DUR:.3f}",
            "afade=t=out:st={max(0.0, dur_sec - FADE_DUR):.3f}:d={FADE_DUR:.3f}",
        ],
    )
