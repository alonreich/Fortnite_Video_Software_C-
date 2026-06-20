from __future__ import annotations
import os
from pathlib import Path
from sanity_tests._real_sanity_harness import (
    DummyButton,
    DummyCheckBox,
    DummyConfigManager,
    DummyLogger,
    DummySignal,
    DummySlider,
    DummySpinBox,
    DummyTimer,
)

from ui.parts.ffmpeg_mixin import FfmpegMixin
from processing.worker import ProcessThread

class _Button(DummyButton):
    def setVisible(self, value):
        self.visible = bool(value)

class _Progress:
    def setRange(self, *args):
        self.range = args

    def setValue(self, value):
        self.value = value

class _LineEdit:
    def text(self):
        return ""

class _Style:
    def standardIcon(self, *args):
        return None

class _Host(FfmpegMixin):
    def __init__(self, input_file):
        self.is_processing = False
        self.input_file_path = str(input_file)
        self.trim_start_ms = 0
        self.trim_end_ms = 8671
        self.original_duration_ms = 85172
        self.original_resolution = "2560x1440"
        self.base_dir = str(input_file.parent)
        self.mobile_checkbox = DummyCheckBox(True)
        self.speed_spinbox = DummySpinBox(1.1)
        self.granular_checkbox = DummyCheckBox(False)
        self.speed_segments = []
        self.positionSlider = DummySlider()
        self.quality_slider = DummySpinBox(3)
        self.process_button = _Button()
        self.cancel_button = _Button()
        self.progress_bar = _Progress()
        self.progress_update_signal = DummySignal()
        self.status_update_signal = DummySignal()
        self.process_finished_signal = DummySignal()
        self.config_manager = DummyConfigManager({})
        self.teammates_checkbox = DummyCheckBox(True)
        self.boss_hp_checkbox = DummyCheckBox(False)
        self.no_fade_checkbox = DummyCheckBox(False)
        self.logger = DummyLogger()
        self._pulse_timer = DummyTimer()
        self._video_volume_pct = 100
        self._wizard_tracks = []
        self.selected_intro_abs_time = 5.722

    def style(self):
        return _Style()

    def _show_processing_overlay(self):
        return None

    def _calculate_wall_clock_time(self, value, segments, speed):
        return float(value)

    def _get_master_eff(self):
        return 100

    def show_message(self, title, message):
        raise AssertionError(f"{title}: {message}")

def test_process_button_path_uses_exact_100ms_intro(monkeypatch, tmp_path: Path) -> None:
    video = tmp_path / "source.mp4"
    video.write_bytes(b"ok")
    captured = {}

    class FakeProcessThread:
        def __init__(self, **kwargs):
            captured.update(kwargs)

        def start(self):
            captured["started"] = True
    monkeypatch.setattr("ui.parts.ffmpeg_mixin.ProcessThread", FakeProcessThread)
    monkeypatch.setattr("processing.system_utils.check_disk_space", lambda *a, **k: True)
    host = _Host(video)
    host.start_processing()
    assert captured.get("started") is True
    assert abs(captured["intro_still_sec"] - 0.1) < 1e-9
    assert captured["intro_still_sec"] * 1000 == 100.0

def test_worker_intro_filter_is_exactly_100ms_at_60fps(monkeypatch, tmp_path: Path) -> None:
    scripts = []
    commands = []

    class FakeEncoderManager:
        def __init__(self, *args, **kwargs):
            return None

        def get_initial_encoder(self):
            return "h264_nvenc"

        def get_codec_flags(self, *args, **kwargs):
            return ["-c:v", "h264_nvenc"], "fake"

        def get_fallback_list(self, *args, **kwargs):
            return []

        def get_encoder_preflight_error(self):
            return None

    class Proc:
        pid = 123
        returncode = 0

        def wait(self, timeout=None):
            return 0

    def fake_create_subprocess(cmd, logger=None):
        commands.append(list(cmd))
        script_path = cmd[cmd.index("-filter_complex_script") + 1]
        scripts.append(Path(script_path).read_text(encoding="utf-8"))
        Path(cmd[-1]).write_bytes(b"ok")
        return Proc()
    monkeypatch.setattr("processing.worker.EncoderManager", FakeEncoderManager)
    monkeypatch.setattr("processing.worker.create_subprocess", fake_create_subprocess)
    monkeypatch.setattr("processing.worker.monitor_ffmpeg_progress", lambda *a, **k: {"critical_lines": [], "dup_frames": 0, "drop_frames": 0})
    monkeypatch.setattr("processing.worker.check_disk_space", lambda *a, **k: True)
    monkeypatch.setattr("processing.worker.calculate_video_bitrate", lambda *a, **k: 1200)
    monkeypatch.setattr("processing.worker.MediaProber.has_audio", lambda self: False)
    monkeypatch.setattr("processing.worker.MediaProber.get_audio_bitrate", lambda self: 128)
    monkeypatch.setattr("processing.worker.MediaProber.get_video_timing_info", lambda self: {"is_vfr": False, "observed_fps": 60.0})
    monkeypatch.setattr("processing.worker.MediaProber.get_video_fps_expr", lambda self, fallback="60": "60")
    monkeypatch.setattr("processing.worker.ProcessThread._validate_render_output", lambda *a, **k: (True, "OK"))
    monkeypatch.setattr("processing.worker.ProcessThread._target_size_bounds", lambda self: None)
    source = tmp_path / "source.mp4"
    source.write_bytes(b"ok")
    finished = DummySignal()
    thread = ProcessThread(
        input_path=str(source),
        start_time_ms=0,
        end_time_ms=1000,
        original_resolution="1920x1080",
        is_mobile_format=False,
        speed_factor=1.0,
        script_dir=str(tmp_path),
        progress_update_signal=DummySignal(),
        status_update_signal=DummySignal(),
        finished_signal=finished,
        logger=DummyLogger(),
        intro_still_sec=0.1,
        intro_abs_time_ms=500,
        hardware_strategy="NVIDIA",
    )
    thread.run()
    assert scripts
    assert "loop=loop=5:size=1:start=0" in scripts[0]
    assert "atrim=duration=0.1000" in scripts[0]
    cmd = commands[0]
    assert cmd[cmd.index("-t", cmd.index("-map")) + 1] == "1.100"
