from __future__ import annotations
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
    install_qt_mpv_stubs,
)
install_qt_mpv_stubs()

from ui.parts.ffmpeg_mixin import FfmpegMixin

class _Progress:
    def setRange(self, *args):
        self.range = args

    def setValue(self, value):
        self.value = value

class _Style:
    def standardIcon(self, *args):
        return None

class _Host(FfmpegMixin):
    def __init__(self, video: Path, music_tracks, *, granular_checked: bool = True):
        self.is_processing = False
        self.input_file_path = str(video)
        self.trim_start_ms = 10_000
        self.trim_end_ms = 40_000
        self.original_duration_ms = 60_000
        self.original_resolution = "1920x1080"
        self.base_dir = str(video.parent)
        self.mobile_checkbox = DummyCheckBox(False)
        self.speed_spinbox = DummySpinBox(1.25)
        self.granular_checkbox = DummyCheckBox(granular_checked)
        self.speed_segments = [{"start": 15_000, "end": 25_000, "speed": 2.0}]
        self.positionSlider = DummySlider()
        self.quality_slider = DummySpinBox(9)
        self.process_button = DummyButton("PROCESS")
        self.cancel_button = DummyButton("Cancel")
        self.progress_bar = _Progress()
        self.progress_update_signal = DummySignal()
        self.status_update_signal = DummySignal()
        self.process_finished_signal = DummySignal()
        self.config_manager = DummyConfigManager({})
        self.teammates_checkbox = DummyCheckBox(False)
        self.boss_hp_checkbox = DummyCheckBox(False)
        self.no_fade_checkbox = DummyCheckBox(False)
        self.logger = DummyLogger()
        self._pulse_timer = DummyTimer()
        self._video_volume_pct = 65
        self._music_volume_pct = 72
        self._wizard_tracks = list(music_tracks)
        self.music_timeline_start_ms = 16_000
        self.music_timeline_end_ms = 31_000
        self.selected_intro_abs_time = 21.0
        self.messages = []

    def style(self):
        return _Style()

    def _show_processing_overlay(self):
        return None

    def _calculate_wall_clock_time(self, value, segments, speed):
        return float(value)

    def _get_master_eff(self):
        return 65

    def _music_eff(self):
        return 72

    def show_message(self, title, message):
        self.messages.append((title, message))

def test_main_music_export_intent_reaches_process_thread(monkeypatch, tmp_path: Path) -> None:
    video = tmp_path / "source.mp4"
    music = tmp_path / "selected.mp3"
    video.write_bytes(b"video")
    music.write_bytes(b"music")
    captured = {}

    class FakeProcessThread:
        def __init__(self, **kwargs):
            captured.update(kwargs)

        def start(self):
            captured["started"] = True
    monkeypatch.setattr("ui.parts.ffmpeg_mixin.ProcessThread", FakeProcessThread)
    monkeypatch.setattr("processing.system_utils.check_disk_space", lambda *a, **k: True)
    host = _Host(video, [(str(music), 4.5, 15.0)])
    host.start_processing()
    assert captured["started"] is True
    assert captured["bg_music_path"] == str(music)
    assert captured["bg_music_offset_ms"] == 4500
    assert captured["music_tracks"] == [(str(music), 4.5, 15.0)]
    assert captured["bg_music_volume"] == 0.72
    assert captured["music_config"]["path"] == str(music)
    assert captured["music_config"]["file_offset_sec"] == 4.5
    assert captured["music_config"]["timeline_start_sec"] == 6.0
    assert captured["music_config"]["timeline_end_sec"] == 21.0
    assert captured["speed_segments"] == [{"start_ms": 15_000, "end_ms": 25_000, "speed": 2.0}]

def test_main_granular_segments_export_even_if_hidden_checkbox_desynced(monkeypatch, tmp_path: Path) -> None:
    video = tmp_path / "source.mp4"
    video.write_bytes(b"video")
    captured = {}

    class FakeProcessThread:
        def __init__(self, **kwargs):
            captured.update(kwargs)

        def start(self):
            captured["started"] = True
    monkeypatch.setattr("ui.parts.ffmpeg_mixin.ProcessThread", FakeProcessThread)
    monkeypatch.setattr("processing.system_utils.check_disk_space", lambda *a, **k: True)
    host = _Host(video, [], granular_checked=False)
    host.start_processing()
    assert captured["started"] is True
    assert host.granular_checkbox.isChecked() is True
    assert captured["speed_segments"] == [{"start_ms": 15_000, "end_ms": 25_000, "speed": 2.0}]

def test_main_selected_music_missing_fails_before_silent_no_music_export(monkeypatch, tmp_path: Path) -> None:
    video = tmp_path / "source.mp4"
    missing_music = tmp_path / "missing.mp3"
    video.write_bytes(b"video")

    class FakeProcessThread:
        def __init__(self, **kwargs):
            raise AssertionError("ProcessThread must not start when selected music is missing")
    monkeypatch.setattr("ui.parts.ffmpeg_mixin.ProcessThread", FakeProcessThread)
    monkeypatch.setattr("processing.system_utils.check_disk_space", lambda *a, **k: True)
    host = _Host(video, [(str(missing_music), 0.0, 10.0)])
    host.start_processing()
    assert host.is_processing is False
    assert host.messages
    assert host.messages[-1][0] == "Music unavailable"
    assert "reselect the music" in host.messages[-1][1]
