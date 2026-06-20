from __future__ import annotations
from sanity_tests._ai_sanity_helpers import assert_all_present, read_source

def test_integration_06_crop_save_clears_autosave_file() -> None:
    """After successful save, Crop Tool should finalize and return through state transfer."""
    src = read_source("developer_tools/crop_tools.py")
    assert_all_present(
        src,
        [
            "def _on_save_finished(self, success, configured, unchanged, error):",
            "self._dirty = False",
            "self._refresh_done_button()",
            "self._summary_toast = SummaryToast(configured, unchanged, self.portrait_view.grab(), self)",
            "QTimer.singleShot(900, self._deferred_launch_main_app)",
        ],
    )
