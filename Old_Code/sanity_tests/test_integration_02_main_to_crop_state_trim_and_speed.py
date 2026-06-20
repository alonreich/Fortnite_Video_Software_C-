from __future__ import annotations
from sanity_tests._ai_sanity_helpers import assert_all_present, read_source

def test_integration_02_main_to_crop_state_trim_and_speed() -> None:
    """Main app must transfer trim + speed segment data when opening Crop Tool."""
    src = read_source("ui/main_window.py")
    assert_all_present(
        src,
        [
            '"trim_start": self.trim_start_ms,',
            '"trim_end": self.trim_end_ms,',
            '"speed_segments": self.speed_segments,',
            '"hardware_mode": getattr(self, "hardware_strategy", "CPU"),',
        ],
    )
