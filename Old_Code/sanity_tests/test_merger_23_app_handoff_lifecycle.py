from sanity_tests._ai_sanity_helpers import assert_all_present, read_source

def test_main_to_merger_handoff_closes_main_without_killing_child() -> None:
    events_src = read_source("ui/parts/main_window_events.py")
    ui_src = read_source("ui/parts/ui_builder_mixin.py")
    assert_all_present(
        events_src,
        [
            'if not getattr(self, "_preserve_child_processes_on_close", False):',
            "current_process.children(recursive=True)",
        ],
    )
    assert_all_present(
        ui_src,
        [
            "proc = subprocess.Popen([sys.executable, p]",
            "QTimer.singleShot(900, _complete_merger_handoff)",
            'self._preserve_child_processes_on_close = True',
            'self._preserve_staged_input_on_close = True',
            "self.close()",
            "Video Merger closed unexpectedly",
        ],
    )

def test_merger_to_main_handoff_closes_merger_after_main_starts() -> None:
    merger_src = read_source("utilities/video_merger.py")
    assert_all_present(
        merger_src,
        [
            "from PyQt5.QtCore import QTimer",
            "proc = subprocess.Popen([sys.executable, main_app_path]",
            "QTimer.singleShot(900, _complete_main_handoff)",
            "window.hide()",
            "window.close()",
            "Main app closed unexpectedly",
        ],
    )
