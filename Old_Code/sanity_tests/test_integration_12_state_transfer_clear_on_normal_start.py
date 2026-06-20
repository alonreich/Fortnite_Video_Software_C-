from __future__ import annotations
from sanity_tests._ai_sanity_helpers import assert_all_present, read_source

def test_integration_12_state_transfer_clear_on_normal_start() -> None:
    """Main window bootstrap should clear stale state-transfer session data."""
    src = read_source("ui/main_window.py")
    assert_all_present(
        src,
        [
            'restore_transfer = os.environ.pop("FVS_STATE_TRANSFER_RESTORE", "") == "1"',
            "StateTransfer.clear_state()",
            "except: pass",
        ],
    )
