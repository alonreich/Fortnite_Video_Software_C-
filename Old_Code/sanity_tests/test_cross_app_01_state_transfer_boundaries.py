from __future__ import annotations
from sanity_tests._ai_sanity_helpers import read_source
from system import state_transfer
from system.state_transfer import StateTransfer

def _function_body(src: str, name: str) -> str:
    marker = f"def {name}("
    start = src.index(marker)
    next_def = src.find("\n    def ", start + len(marker))
    return src[start:] if next_def == -1 else src[start:next_def]

def test_state_transfer_file_is_atomic_loadable_and_clearable(monkeypatch, tmp_path) -> None:
    monkeypatch.setattr(state_transfer.SharedPaths, "TEMP", str(tmp_path))
    StateTransfer.save_state({"input_file": "a.mp4", "trim_start": 10, "music_widget": {"stale": True}})
    assert StateTransfer.load_state()["input_file"] == "a.mp4"
    StateTransfer.update_state({"trim_end": 20})
    loaded = StateTransfer.load_state()
    assert loaded["trim_start"] == 10
    assert loaded["trim_end"] == 20
    StateTransfer.clear_state()
    assert StateTransfer.load_state() == {}

def test_main_to_merger_transfer_boundary_is_minimal_and_music_free() -> None:
    src = read_source("ui/parts/ui_builder_mixin.py")
    body = _function_body(src, "launch_video_merger")
    assert "StateTransfer.save_state" in body
    assert "'input_file'" in body
    assert "'trim_start'" in body
    assert "'trim_end'" in body
    assert "'mobile_checked'" in body
    assert "_wizard_tracks" not in body
    assert "music_timeline" not in body
    assert "music_widget" not in body
    assert "_preserve_child_processes_on_close" in body
    assert "self.close()" in body

def test_main_to_crop_transfer_boundary_keeps_crop_fields_without_music_session_state() -> None:
    src = read_source("ui/parts/main_window_tools.py")
    body = _function_body(src, "launch_crop_tool")
    assert "StateTransfer.save_state(state)" in body
    for key in ("input_file", "source_file", "trim_start", "trim_end", "speed_segments", "granular_checked", "hardware_mode", "resolution"):
        assert f'"{key}"' in body
    assert "_wizard_tracks" not in body
    assert "music_timeline" not in body
    assert "music_widget" not in body

def test_normal_main_start_clears_transfer_unless_restore_flag_is_present() -> None:
    src = read_source("ui/main_window.py")
    restore_body = _function_body(src, "_restore_state_transfer_session")
    assert 'restore_transfer = os.environ.pop("FVS_STATE_TRANSFER_RESTORE", "") == "1"' in src
    assert "StateTransfer.load_state()" in src
    assert "StateTransfer.clear_state()" in src
    assert "StateTransfer.clear_state()" in restore_body
    assert 'env["FVS_STATE_TRANSFER_RESTORE"] = "1"' in read_source("developer_tools/crop_tools.py")

def test_merger_to_main_handoff_closes_merger_only_after_main_process_survives() -> None:
    src = read_source("utilities/video_merger.py")
    assert "def restart_main_app():" in src
    assert "subprocess.Popen([sys.executable, main_app_path]" in src
    assert "window.hide()" in src
    assert "if proc.poll() is None:" in src
    assert "window.close()" in src
    assert "window.show()" in src
