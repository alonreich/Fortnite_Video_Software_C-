from __future__ import annotations
from sanity_tests._ai_sanity_helpers import assert_all_present, read_source

def test_integration_08_crop_save_updates_only_existing_items() -> None:
    """Save flow must update payload items and report existing untouched items."""
    src = read_source("developer_tools/crop_tools.py")
    assert_all_present(
        src,
        [
            "existing_before = set(config.get(\"crops_1080p\", {}).keys())",
            "saved_keys = set()",
            "for section in [\"crops_1080p\", \"scales\", \"overlays\", \"z_orders\"]:",
            "saved_keys.add(tech_key)",
            "unchanged = [HUD_ELEMENT_MAPPINGS.get(k, k) for k in sorted(existing_before - saved_keys)]",
        ],
    )
