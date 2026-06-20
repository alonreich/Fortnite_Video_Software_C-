from __future__ import annotations
from sanity_tests._ai_sanity_helpers import assert_all_present, read_source

def test_integration_15_drift_protection_map_loot_pixel_bias() -> None:
    """Map/Stats and Loot drift-protection pixel bias contract should stay intact."""
    cfg_src = read_source("developer_tools/config.py")
    hud_src = read_source("processing/hud_config.py")
    filter_src = read_source("processing/filter_mobile.py")
    assert_all_present(
        cfg_src,
        [
            "HUD_SAFE_PADDING = {",
            '"stats": {"left": -1},',
            '"loot": {"right": 1}',
        ],
    )
    assert_all_present(
        hud_src,
        [
            "def crop_drift_type",
            'return "left"',
            'if key == "loot":',
            'return "right"',
        ],
    )
    assert_all_present(
        filter_src,
        [
            "inverse_transform_from_content_area_int",
            "crop_drift_type(ck)",
        ],
    )
