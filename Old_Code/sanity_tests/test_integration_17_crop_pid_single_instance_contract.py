from __future__ import annotations
from sanity_tests._ai_sanity_helpers import assert_all_present, read_source

def test_integration_17_crop_pid_single_instance_contract() -> None:
    """Crop Tool bootstrap must initialize logging and dependency checks."""
    src = read_source("developer_tools/crop_tools.py")
    assert_all_present(
        src,
        [
            'logger_initial = ConsoleManager.initialize(project_root, "crop_tools.log", "Crop_Tool")',
            "from system.logger import setup_native_logging",
            'logger = setup_native_logging("crop_tool")',
            "if not DependencyDoctor.check_ffmpeg(self.base_dir)[0]: sys.exit(1)",
            "sys.exit(app.exec_())",
        ],
    )
