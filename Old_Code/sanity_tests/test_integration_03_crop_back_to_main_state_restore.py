from __future__ import annotations
from sanity_tests._ai_sanity_helpers import assert_all_present, read_source

def test_integration_03_crop_back_to_main_state_restore() -> None:
    """Crop Tool should load transfer state, and on return update it for Main App restore."""
    crop_src = read_source("developer_tools/crop_tools.py")
    main_src = read_source("ui/main_window.py")
    assert_all_present(
        crop_src,
        [
            "session_data = StateTransfer.load_state()",
            "if session_data.get('input_file'):",
            "if session_data.get('resolution'):",
            "self.media_processor.original_resolution = session_data['resolution']",
            "StateTransfer.update_state(updates)",
        ],
    )
    assert_all_present(
        main_src,
        [
            '"input_file": self.input_file_path,',
            '"resolution": getattr(self, "original_resolution", None)',
        ],
    )
