from __future__ import annotations
from sanity_tests._ai_sanity_helpers import assert_all_present, read_source

def test_integration_20_crop_mpv_missing_fallback_mode() -> None:
    """Crop Tool should expose screenshot fallback UI when MPV is unavailable."""
    src = read_source("developer_tools/crop_tools.py")
    assert_all_present(
        src,
        [
            "if not self.media_processor.player:",
            "self.mpv_error_label.setVisible(True)",
            'self.mpv_error_label.setText("⚠️ MPV ENGINE MISSING")',
            "self.upload_hint_label.setVisible(False)",
            'msg.setWindowTitle("MPV Player Required")',
        ],
    )
