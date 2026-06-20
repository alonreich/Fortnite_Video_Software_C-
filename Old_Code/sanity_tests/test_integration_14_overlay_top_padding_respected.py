from __future__ import annotations
from sanity_tests._ai_sanity_helpers import assert_all_present, read_source

def test_integration_14_overlay_top_padding_respected() -> None:
    """Overlay clamping must respect reserved top padding area."""
    src = read_source("processing/coordinate_math.py")
    assert_all_present(
        src,
        [
            "def clamp_overlay_position",
            "padding_top_ui: int = UI_PADDING_TOP",
            "min_y = Fraction(padding_top_ui)",
            "max_y = max(min_y, Fraction(PORTRAIT_H - padding_bottom_ui) - fh)",
        ],
    )
