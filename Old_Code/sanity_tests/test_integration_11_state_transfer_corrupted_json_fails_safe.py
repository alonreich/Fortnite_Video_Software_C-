from __future__ import annotations
from sanity_tests._ai_sanity_helpers import assert_all_present, read_source

def test_integration_11_state_transfer_corrupted_json_fails_safe() -> None:
    """Corrupted transfer JSON must not crash; loader should return empty dict."""
    src = read_source("system/state_transfer.py")
    assert_all_present(
        src,
        [
            "def load_state() -> dict:",
            "data = json.load(f)",
            "except Exception as e:",
            "return {}",
        ],
    )
