from __future__ import annotations
import re
from sanity_tests._ai_sanity_helpers import read_source, assert_all_present

def test_core_11_bootstrap_paths_and_init_contracts_dryrun() -> None:
    app_src = read_source("app.py")
    ui_main_src = read_source("ui/main_window.py")
    merger_src = read_source("utilities/video_merger.py")
    crop_src = read_source("developer_tools/crop_tools.py")
    adv_src = read_source("advanced/advanced_video_editor.py")
    adv_mw_src = read_source("advanced/main_window.py")
    assert_all_present(
        app_src,
        [
            'ConsoleManager.initialize(BASE_DIR, "main_app.log", "Main_App")',
        ],
    )
    assert_all_present(
        ui_main_src,
        ["ConfigManager(os.path.join(self.base_dir, 'config', 'main_app', 'main_app.conf'))"],
    )
    assert_all_present(
        merger_src,
        [
            'MergerConsoleManager.initialize(project_root, "video_merger.log", "Video_Merger")',
            "config_path = os.path.join(BASE_DIR, 'config', 'video_merger.conf')",
        ],
    )
    assert_all_present(
        crop_src,
        [
            'ConsoleManager.initialize(project_root, "crop_tools.log", "Crop_Tool")',
            "self.app_config_path = os.path.join(self.base_dir, 'config', 'crop_tools.conf')",
        ],
    )
    assert_all_present(
        adv_src,
        ['ConsoleManager.initialize(project_root, "advanced_editor.log", "Advanced_Editor")'],
    )
    assert_all_present(
        adv_mw_src,
        [
            'ConfigManager(os.path.join(base_dir, "config", "Advanced_Video_Editor.conf"))',
            'ConfigManager(os.path.join(base_dir, "config", "Keyboard_Binds.conf"))',
        ],
    )

def test_core_11_app_log_files_are_unique_dryrun() -> None:
    targets = {
        "main": read_source("app.py"),
        "merger": read_source("utilities/video_merger.py"),
        "crop": read_source("developer_tools/crop_tools.py"),
        "advanced": read_source("advanced/advanced_video_editor.py"),
    }
    pat = re.compile(r'initialize\([^\)]*"([^"]+\.log)"')
    found: list[str] = []
    for text in targets.values():
        found.extend(pat.findall(text))
    expected = {"main_app.log", "video_merger.log", "crop_tools.log", "advanced_editor.log"}
    assert set(found) == expected, f"Unexpected per-app log names: {found}"
    assert len(found) == len(set(found)), f"Duplicate app log filenames detected: {found}"
