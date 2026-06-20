from __future__ import annotations
from sanity_tests._ai_sanity_helpers import assert_all_present, read_source
import sys

def test_integration_13_coordinate_space_content_vs_overlay() -> None:
    """Coordinate math must keep crop in content area logic and overlay in full portrait space."""
    src = read_source("processing/coordinate_math.py")
    assert_all_present(
        src,
        [
            "UI_CONTENT_H = 1620",
            "PADDING_TOP = UI_PADDING_TOP",
            "def transform_to_content_area",
            "internal_y = (y * scale) - crop_y",
            "clamp_overlay_position",
        ],
    )

    from developer_tools.coordinate_math import transform_to_content_area, inverse_transform_from_content_area
    rect = (100.0, 100.0, 200.0, 200.0)
    original_resolution = "1920x1080"
    transformed = transform_to_content_area(rect, original_resolution)
    assert transformed[1] >= 150.0, "Transformed Y must be within the content area (>= UI_PADDING_TOP)"
    inversed = inverse_transform_from_content_area(transformed, original_resolution)
    assert abs(inversed[0] - rect[0]) < 2.0
    assert abs(inversed[1] - rect[1]) < 2.0

