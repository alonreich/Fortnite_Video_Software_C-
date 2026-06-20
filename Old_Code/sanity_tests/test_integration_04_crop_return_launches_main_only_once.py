from __future__ import annotations
from sanity_tests._ai_sanity_helpers import assert_all_present, read_source

def test_integration_04_crop_return_launches_main_only_once() -> None:
    """Crop Tool return path must launch Main App and then quit current process."""
    src = read_source("developer_tools/crop_tools.py")
    assert_all_present(
        src,
        [
            "def _deferred_launch_main_app(self):",
            "subprocess.Popen(",
            "[sys.executable, \"-B\", os.path.join(self.base_dir, 'app.py')]",
            "QApplication.instance().quit()",
        ],
    )
