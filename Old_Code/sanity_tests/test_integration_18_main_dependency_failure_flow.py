from __future__ import annotations
from sanity_tests._ai_sanity_helpers import assert_all_present, read_source

def test_integration_18_main_dependency_failure_flow() -> None:
    """Missing dependency flow should show recovery dialog and allow retry/open/exit paths."""
    src = read_source("app.py")
    assert_all_present(
        src,
        [
            "is_valid_deps, ffmpeg_path, dep_error = DependencyDoctor.check_ffmpeg(BASE_DIR)",
            "if not is_valid_deps:",
            "action = show_dependency_error_dialog(ffmpeg_path, ffprobe_path, dep_error)",
            "if action == \"retry\":",
            "if action == \"open\": continue",
            "sys.exit(1)",
        ],
    )
