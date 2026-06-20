from __future__ import annotations
from sanity_tests._ai_sanity_helpers import assert_all_present, read_source

def test_integration_07_crop_close_cleans_temp_snapshots() -> None:
    """Closing Crop Tool should run temporary snapshot cleanup."""
    src = read_source("developer_tools/crop_tools.py")
    assert_all_present(
        src,
        [
            "def closeEvent(self, event):",
            "cleanup_temp_snapshots()",
        ],
    )
