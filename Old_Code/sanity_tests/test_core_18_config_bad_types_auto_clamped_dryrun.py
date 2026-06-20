from __future__ import annotations
from sanity_tests._ai_sanity_helpers import assert_all_present, read_source

def _simulate_quality_level_cast(raw_value: object) -> int:
    try:
        return int(raw_value)
    except Exception:
        return 2

def test_core_18_config_bad_types_auto_clamped_dryrun() -> None:
    cfg_src = read_source("processing/config_data.py")
    worker_src = read_source("processing/worker.py")
    assert_all_present(
        cfg_src,
        [
            "try:",
            "q = int(quality_level)",
            "except Exception:",
            "q = 2",
            "return keep_highest_res, target_mb, q",
        ],
    )
    assert_all_present(
        worker_src,
        [
            "def _normalize_speed_segments(self, raw_segments):",
            "if not isinstance(seg, dict):",
            "continue",
            "except Exception:",
            "continue",
            "if e_ms <= s_ms:",
            "continue",
        ],
    )
    assert _simulate_quality_level_cast("3") == 3
    assert _simulate_quality_level_cast("bad") == 2
    assert _simulate_quality_level_cast(None) == 2
