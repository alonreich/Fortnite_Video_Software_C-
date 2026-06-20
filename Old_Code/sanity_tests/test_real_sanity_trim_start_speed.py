from __future__ import annotations
from pathlib import Path
import types
from sanity_tests._real_sanity_harness import install_qt_mpv_stubs
from sanity_tests._ai_sanity_helpers import read_source
install_qt_mpv_stubs()

from processing.filter_builder import FilterBuilder
from processing.worker import ProcessThread
from ui.widgets.granular_speed_editor import GranularSpeedEditor

class _Sig:
    def __init__(self) -> None:
        self.calls: list[tuple] = []

    def emit(self, *args) -> None:
        self.calls.append(args)

def _logger() -> object:
    return types.SimpleNamespace(
        info=lambda *a, **k: None,
        warning=lambda *a, **k: None,
        error=lambda *a, **k: None,
        exception=lambda *a, **k: None,
        critical=lambda *a, **k: None,
    )

def test_trim_start_ss_is_correct_with_speed_and_granular_segments(monkeypatch, tmp_path: Path) -> None:
    captured_cmds: list[list[str]] = []

    class _Proc:
        pid = 111
        returncode = 0

        def wait(self, timeout=None):
            return 0

    def _fake_create_subprocess(cmd, _logger=None):
        captured_cmds.append(list(cmd))
        Path(cmd[-1]).write_bytes(b"ok")
        return _Proc()
    monkeypatch.setattr("processing.worker.create_subprocess", _fake_create_subprocess)
    monkeypatch.setattr("processing.worker.monitor_ffmpeg_progress", lambda *a, **k: None)
    monkeypatch.setattr("processing.worker.check_disk_space", lambda *a, **k: True)
    monkeypatch.setattr("processing.worker.calculate_video_bitrate", lambda *a, **k: 1500)
    monkeypatch.setattr("processing.worker.MediaProber.get_audio_bitrate", lambda self: 128)
    monkeypatch.setattr("processing.worker.MediaProber.get_sample_rate", lambda self: 48000)
    monkeypatch.setattr("processing.worker.MediaProber.get_video_timing_info", lambda self: {"is_vfr": False, "observed_fps": 60.0})
    monkeypatch.setattr("processing.worker.MediaProber.get_video_fps_expr", lambda self, fallback="60": "60")
    monkeypatch.setattr("processing.worker.ProcessThread._validate_render_output", lambda *a, **k: (True, "OK"))
    monkeypatch.setattr("processing.worker.ProcessThread._target_size_bounds", lambda self: None)
    out_file = tmp_path / "rendered.mp4"
    out_file.write_bytes(b"ok")
    thr = ProcessThread(
        input_path=str(out_file),
        start_time_ms=12000,
        end_time_ms=22000,
        original_resolution="1920x1080",
        is_mobile_format=False,
        speed_factor=2.7,
        script_dir=str(tmp_path),
        progress_update_signal=_Sig(),
        status_update_signal=_Sig(),
        finished_signal=_Sig(),
        logger=_logger(),
        disable_fades=True,
        intro_still_sec=0.0,
        speed_segments=[
            {"start": 12000, "end": 15000, "speed": 0.5},
            {"start": 15000, "end": 18000, "speed": 2.4},
            {"start": 18000, "end": 22000, "speed": 1.3},
        ],
    )
    thr.run()
    assert captured_cmds, "ffmpeg command should be invoked"
    core_cmd = captured_cmds[0]
    ss_idx = core_cmd.index("-ss")
    assert core_cmd[ss_idx + 1] == "12.000"

def test_trim_relative_time_mapper_with_multiple_speed_segments() -> None:
    fb = FilterBuilder(logger=_logger())
    _chain, _v, _a, _dur, tmap = fb.build_granular_speed_chain(
        video_path="dummy.mp4",
        duration_ms=7000,
        speed_segments=[
            {"start": 12000, "end": 14000, "speed": 0.5},
            {"start": 14000, "end": 16000, "speed": 2.0},
            {"start": 16000, "end": 19000, "speed": 1.0},
        ],
        base_speed=1.5,
        source_cut_start_ms=12000,
    )
    assert abs(tmap(12.0) - 0.0) < 1e-6
    assert abs(tmap(14.0) - 4.0) < 0.05
    assert abs(tmap(16.0) - 5.0) < 0.05

class _FakeMedia:
    def __init__(self, duration_ms: int) -> None:
        self._duration_ms = int(duration_ms)

    def parse(self) -> None:
        return None

    def get_duration(self) -> int:
        return self._duration_ms

class _FakeMpvInstance:
    def __init__(self, duration_ms: int) -> None:
        self._duration_ms = int(duration_ms)

    def media_new(self, _path: str) -> _FakeMedia:
        return _FakeMedia(self._duration_ms)

class _FakePlayer:
    def __init__(self) -> None:
        self._time = 0
        self._playing = False
        self.set_time_calls: list[int] = []
        self.command_calls: list[list] = []
        self.volume = 100
        self.mute = False
        self.speed = 1.0
        self.pause = True
        self.duration = 100.0

    def play(self, *args) -> None:
        self._playing = True
        self.pause = False

    def stop(self, *args) -> None:
        self._playing = False
        self.pause = True

    def seek(self, seconds: float, reference='absolute', precision='exact') -> None:
        self._time = int(seconds * 1000)
        self.set_time_calls.append(int(self._time))
        
    def set_time(self, ms: int) -> None:
        self._time = int(ms)
        self.set_time_calls.append(int(ms))
        
    def command(self, *args) -> None:
        self.command_calls.append(list(args))
        if args and args[0] == "seek":
            ms = int(float(args[1]) * 1000)
            self.set_time_calls.append(ms)

class _FakeTimeline:
    def __init__(self) -> None:
        self.range_calls: list[tuple[int, int]] = []
        self.trim_calls: list[tuple[int, int]] = []

    def setRange(self, start: int, end: int) -> None:
        self.range_calls.append((int(start), int(end)))

    def set_duration_ms(self, _duration: int) -> None:
        return None

    def set_segments(self, _segments) -> None:
        return None

    def set_trim_times(self, start: int, end: int) -> None:
        self.trim_calls.append((int(start), int(end)))

    def setValue(self, _value: int) -> None:
        return None

def test_granular_editor_uses_trim_window_and_resume_frame_from_main_preview(monkeypatch) -> None:
    main_preview_paused_ms = 4500
    trim_start_ms = 2000
    trim_end_ms = 7000
    editor = GranularSpeedEditor.__new__(GranularSpeedEditor)
    editor.input_file_path = "dummy.mp4"
    editor.parent_app = types.SimpleNamespace(
        trim_start_ms=trim_start_ms,
        trim_end_ms=trim_end_ms,
        logger=_logger(),
    )
    editor.player = _FakePlayer()
    editor.video_frame = types.SimpleNamespace(winId=lambda: 1)
    editor.timeline = _FakeTimeline()
    editor.speed_segments = []
    editor.start_time_ms = main_preview_paused_ms
    editor.volume = 100
    editor.selection_modified = False

    import threading
    editor._mpv_lock = threading.RLock()
    editor._mpv_lock_timeout = 0.1
    editor.update_pending_visualization = lambda: None
    editor.play_btn = types.SimpleNamespace(setText=lambda *_: None, setIcon=lambda *_: None)
    editor.style = lambda: types.SimpleNamespace(standardIcon=lambda *_: None)

    from system.utils import MPVSafetyManager
    monkeypatch.setattr(MPVSafetyManager, "create_safe_mpv", lambda **k: editor.player)
    editor.duration = trim_end_ms - trim_start_ms
    editor.abs_trim_start = trim_start_ms
    GranularSpeedEditor.setup_player(editor)
    editor.timeline.setRange(0, int(editor.duration))
    GranularSpeedEditor._finalize_startup(editor)
    assert editor.timeline.range_calls, "Granular editor must set timeline range"
    assert editor.timeline.range_calls[-1] == (0, int(trim_end_ms - trim_start_ms))
    assert editor.player.set_time_calls, "Granular editor must seek to startup frame"
    assert editor.player.set_time_calls[-1] == (trim_start_ms + main_preview_paused_ms)

def test_main_app_click_contract_passes_paused_time_into_granular_editor() -> None:
    src = read_source("ui/main_window.py")
    assert "current_ms = max(0, int((getattr(self.player, 'time-pos', 0) or 0) * 1000))" in src
    assert "start_time_ms=current_ms" in src
