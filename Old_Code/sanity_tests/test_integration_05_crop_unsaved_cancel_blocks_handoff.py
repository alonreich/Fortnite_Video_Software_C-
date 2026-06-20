from __future__ import annotations
from sanity_tests._ai_sanity_helpers import assert_all_present, read_source

def test_integration_05_crop_unsaved_cancel_blocks_handoff() -> None:
    """F12 return must be guarded by unsaved-changes confirmation."""
    src = read_source("developer_tools/crop_tools.py")
    assert_all_present(
        src,
        [
            "if key == Qt.Key_F12:",
            "if self._confirm_discard_changes(): self._deferred_launch_main_app()",
            "def _confirm_discard_changes(self):",
            "if not self._dirty:",
            "msg.setText(\"Discard changes?\")",
            "return msg.exec_() == QMessageBox.Yes",
        ],
    )
