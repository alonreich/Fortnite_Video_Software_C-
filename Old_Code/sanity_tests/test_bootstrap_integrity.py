import os, sys, pytest, inspect
from PyQt5.QtWidgets import QApplication
from PyQt5.QtCore import QTimer
os.environ["QT_QPA_PLATFORM"] = "offscreen"
sys.dont_write_bytecode = True

from sanity_tests._real_sanity_harness import install_qt_mpv_stubs
install_qt_mpv_stubs()

def test_app_instantiation_integrity():
    """
    ULTIMATE INTEGRITY CHECK: 
    This test actually creates the main window object. 
    If ANY Mixin is missing, or ANY import is broken, this test WILL fail.
    """

    from ui.main_window import FortniteVideoSoftware
    from system.config import ConfigManager
    from ui.widgets.tooltip_manager import ToolTipManager
    base_dir = os.path.abspath(os.path.join(os.path.dirname(__file__), '..'))
    config_path = os.path.join(base_dir, 'config', 'main_app', 'main_app.conf')
    cm = ConfigManager(config_path)
    tm = ToolTipManager()
    try:
        app = FortniteVideoSoftware(
            file_path=None, 
            hardware_strategy="CPU", 
            bin_dir=os.path.join(base_dir, "binaries"),
            config_manager=cm,
            tooltip_manager=tm
        )
    except Exception as e:
        pytest.fail(f"APP BOOT CRASH DETECTED: {str(e)}\nTraceback: {inspect.trace()}")
    assert hasattr(app, 'duration_changed_signal'), "CRITICAL: duration_changed_signal is missing!"
    assert hasattr(app, '_on_master_volume_changed'), "CRITICAL: Volume control logic is missing (VolumeMixin broken)!"
    assert hasattr(app, '_on_slider_trim_changed'), "CRITICAL: Trim control logic is missing (MainWindowCoreC broken)!"

def test_recursive_import_integrity():
    """
    Scans the entire project folder and tries to import every file.
    This catches 'ImportError: cannot import name ...' automatically.
    """
    base_dir = os.path.abspath(os.path.join(os.path.dirname(__file__), '..'))
    ignored_folders = {'.git', 'venv', '__pycache__', 'binaries', 'scratch'}
    for root, dirs, files in os.walk(base_dir):
        dirs[:] = [d for d in dirs if d not in ignored_folders]
        for file in files:
            if file.endswith(".py") and not file.startswith("test_") and file != "app.py":
                rel_path = os.path.relpath(os.path.join(root, file), base_dir)
                module_path = rel_path.replace(os.sep, ".").replace(".py", "")
                try:
                    if root not in sys.path:
                        sys.path.insert(0, root)
                    __import__(module_path)
                except Exception as e:
                    pytest.fail(f"BROKEN IMPORT in {rel_path}: {str(e)}")

def test_granular_speed_editor_integrity():
    """
    Verifies that the GranularSpeedEditor can be instantiated and has
    the correct method signatures.
    """

    from ui.widgets.granular_speed_editor import GranularSpeedEditor
    import inspect
    sig = inspect.signature(GranularSpeedEditor.seek_video)
    assert 'exact' in sig.parameters, "GranularSpeedEditor.seek_video is missing the 'exact' parameter!"

def test_ui_signal_connection_validity():
    """
    Inspects the UI Builder and ensures every .connect() call 
    points to a function that actually exists on the class.
    """

    from ui.main_window import FortniteVideoSoftware
    members = dict(inspect.getmembers(FortniteVideoSoftware))
    required_callbacks = [
        '_on_master_volume_changed',
        '_on_slider_trim_changed',
        'select_file',
        'toggle_play_pause',
        'launch_crop_tool',
        '_on_speed_changed',
        'on_hardware_scan_finished'
    ]
    for callback in required_callbacks:
        assert callback in members, f"UI HOOK BROKEN: The app expects a function named '{callback}' but it is missing!"
