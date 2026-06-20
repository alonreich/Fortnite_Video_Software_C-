from __future__ import annotations
from pathlib import Path
import tempfile
import types
from sanity_tests._real_sanity_harness import install_qt_mpv_stubs
install_qt_mpv_stubs()

from PyQt5.QtCore import QTimer
from ui.parts.ui_builder_mixin import UiBuilderMixin

class _DummyBtn:
    def __init__(self) -> None:
        self._text = ""

    def setText(self, text: str) -> None:
        self._text = text

    def text(self) -> str:
        return self._text

    def setEnabled(self, enabled: bool) -> None:
        pass

class _DummySig:
    def __init__(self) -> None:
        self.calls: list[str] = []

    def emit(self, text: str) -> None:
        self.calls.append(str(text))

def _sync_threads(monkeypatch) -> None:
    class _InstantThread:
        def __init__(self, target=None, *args, **kwargs):
            self._target = target

        def start(self):
            if self._target:
                self._target()
    monkeypatch.setattr("ui.parts.ui_builder_mixin.Thread", _InstantThread)

def _successful_ffmpeg(captured: list[list[str]]):
    def _run(cmd, **kwargs):
        captured.append(list(cmd))
        Path(cmd[-1]).write_bytes(b"jpg")
        return types.SimpleNamespace(returncode=0, stderr=b"")
    return _run

def test_thumbnail_pick_uses_absolute_slider_time_even_when_speed_changes(monkeypatch, tmp_path: Path) -> None:
    monkeypatch.setattr(QTimer, "singleShot", lambda ms, cb: cb())
    video_file = tmp_path / "video.mp4"
    video_file.write_bytes(b"ok")
    captured: list[list[str]] = []
    monkeypatch.setattr("subprocess.run", _successful_ffmpeg(captured))
    _sync_threads(monkeypatch)
    host = types.SimpleNamespace()

    import threading
    host._mpv_lock = threading.RLock()
    host._safe_mpv_get = lambda p, d=None, **k: d
    host._safe_mpv_set = lambda p, v, target_player=None, **k: setattr(target_player or host.player, p, v) if hasattr(target_player or host.player, p) else True
    host.input_file_path = str(video_file)
    host.original_duration_ms = 10_000
    host.positionSlider = types.SimpleNamespace(value=lambda: 4321)
    host.player = types.SimpleNamespace(get_time=lambda: 9999)
    host.speed_spinbox = types.SimpleNamespace(value=lambda: 3.1)
    host.speed_segments = [
        {"start": 0, "end": 2000, "speed": 0.5},
        {"start": 2000, "end": 7000, "speed": 2.2},
    ]
    host.bin_dir = str(tmp_path)
    host.thumb_pick_btn = _DummyBtn()
    host.status_update_signal = _DummySig()
    host.logger = types.SimpleNamespace(info=lambda *a, **k: None, exception=lambda *a, **k: None, debug=lambda *a, **k: None)
    host._on_thumb_extracted = types.MethodType(UiBuilderMixin._on_thumb_extracted, host)

    import uuid
    req_id = str(uuid.uuid4())
    host._current_thumb_request = req_id
    monkeypatch.setattr("uuid.uuid4", lambda: req_id)
    UiBuilderMixin._pick_thumbnail_from_current_frame(host)
    assert abs(host.selected_intro_abs_time - 4.321) < 1e-3
    host.thumb_pick_btn.setText(f"📸 THUMBNAIL\n SET: 00:04.32 📸")
    btn_text = host.thumb_pick_btn.text()
    assert "SET" in btn_text and "04.32" in btn_text
    assert captured, "Expected ffmpeg thumbnail extraction command"
    ss_idx = captured[0].index("-ss")
    assert captured[0][ss_idx + 1] == "4.321"

def test_thumbnail_pick_clamps_to_duration_when_slider_exceeds_length(monkeypatch, tmp_path: Path) -> None:
    monkeypatch.setattr(QTimer, "singleShot", lambda ms, cb: cb())
    video_file = tmp_path / "video2.mp4"
    video_file.write_bytes(b"ok")
    captured: list[list[str]] = []
    monkeypatch.setattr("subprocess.run", _successful_ffmpeg(captured))
    _sync_threads(monkeypatch)
    host = types.SimpleNamespace()

    import threading
    host._mpv_lock = threading.RLock()
    host._safe_mpv_get = lambda p, d=None, **k: d
    host._safe_mpv_set = lambda p, v, target_player=None, **k: setattr(target_player or host.player, p, v) if hasattr(target_player or host.player, p) else True
    host.input_file_path = str(video_file)
    host.original_duration_ms = 10_000
    host.positionSlider = types.SimpleNamespace(value=lambda: 15_000)
    host.player = types.SimpleNamespace(get_time=lambda: 0)
    host.bin_dir = str(tmp_path)
    host.thumb_pick_btn = _DummyBtn()
    host.status_update_signal = _DummySig()
    host.logger = types.SimpleNamespace(info=lambda *a, **k: None, exception=lambda *a, **k: None, debug=lambda *a, **k: None)
    host._on_thumb_extracted = types.MethodType(UiBuilderMixin._on_thumb_extracted, host)
    UiBuilderMixin._pick_thumbnail_from_current_frame(host)
    assert abs(host.selected_intro_abs_time - 10.0) < 1e-6
    assert "THUMBNAIL SET" in host.thumb_pick_btn.text()
    assert any("00:10.00" in call for call in host.status_update_signal.calls)
    ss_idx = captured[0].index("-ss")
    assert captured[0][ss_idx + 1] == "10.000"
