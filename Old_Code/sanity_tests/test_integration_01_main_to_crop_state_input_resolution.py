from __future__ import annotations
from sanity_tests._ai_sanity_helpers import assert_all_present, read_source

def test_integration_01_main_to_crop_state_input_resolution() -> None:
    """Main app must pass input file + resolution when launching Crop Tool."""
    src = read_source("ui/main_window.py")
    assert_all_present(
        src,
        [
            "def launch_crop_tool(self):",
            '"input_file": self.input_file_path,',
            '"resolution": getattr(self, "original_resolution", None)',
            "StateTransfer.save_state(state)",
        ],
    )
