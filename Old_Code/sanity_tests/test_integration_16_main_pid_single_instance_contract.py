from __future__ import annotations
from sanity_tests._ai_sanity_helpers import assert_all_present, read_source

def test_integration_16_main_pid_single_instance_contract() -> None:
    """Main app must enforce single-instance PID lock behavior."""
    src = read_source("app.py")
    assert_all_present(
        src,
        [
            'PID_APP_NAME = "fortnite_video_software_main"',
            "success, pid_handle = ProcessManager.acquire_pid_lock(PID_APP_NAME)",
            'logger.warning("BOOT: Single instance lock active. Exiting.")',
            "sys.exit(0)",
        ],
    )
