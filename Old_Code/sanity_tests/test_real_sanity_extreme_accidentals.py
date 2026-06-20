from __future__ import annotations
from pathlib import Path
import threading
import types
from sanity_tests._real_sanity_harness import DummyCheckBox, DummySpinBox, install_qt_mpv_stubs
install_qt_mpv_stubs()

from processing.media_utils import MediaProber
from processing.system_utils import monitor_ffmpeg_progress, parse_time_to_seconds
from system.config import ConfigManager
from ui.parts.player_mixin import PlayerMixin
from ui.widgets.music_wizard_misc import MergerMusicWizardMiscMixin
from ui.widgets.music_wizard_waveform import MergerMusicWizardWaveformMixin

def _logger() -> object:
    return types.SimpleNamespace(
        info=lambda *a, **k: None,
        warning=lambda *a, **k: None,
        error=lambda *a, **k: None,
        exception=lambda *a, **k: None,
        critical=lambda *a, **k: None,
        debug=lambda *a, **k: None,
    )

def test_extreme_01_parse_time_to_seconds_supports_hh_mm_ss_fraction() -> None:
    assert abs(parse_time_to_seconds("01:02:03.250") - 3723.25) < 1e-6

def test_extreme_02_parse_time_to_seconds_returns_zero_on_corrupted_input() -> None:
    assert parse_time_to_seconds("::bad::timestamp::") == 0.0

class _Out:
    def __init__(self, lines: list[str], proc) -> None:
        self._lines = list(lines)
        self._proc = proc

    def readline(self) -> str:
        if self._lines:
            return self._lines.pop(0)
        self._proc._done = True
        return ""

class _Proc:
    def __init__(self, lines: list[str], pid: int = 123) -> None:
        self.pid = pid
        self._done = False
        self.stdout = _Out(lines, self)

    def poll(self):
        return 0 if self._done else None

class _Prog:
    def __init__(self) -> None:
        self.values: list[int] = []

    def emit(self, value: int) -> None:
        self.values.append(int(value))

def test_extreme_03_monitor_ffmpeg_progress_clamps_overshoot_to_100() -> None:
    proc = _Proc(["out_time_us=500000\n", "out_time_us=3500000\n"])
    progress = _Prog()
    monitor_ffmpeg_progress(
        proc=proc,
        duration_sec=1.0,
        progress_signal=progress,
        check_disk_space_callback=lambda: False,
        logger=_logger(),
    )
    assert progress.values, "Expected progress updates from out_time_us lines"
    assert progress.values[-1] == 100

def test_extreme_04_monitor_ffmpeg_progress_cancels_when_disk_callback_flags(monkeypatch) -> None:
    proc = _Proc(lines=[], pid=987)
    killed: list[int] = []

    import processing.system_utils as su
    monkeypatch.setattr(su, "kill_process_tree", lambda pid, _logger: killed.append(int(pid)))
    tick = {"i": 0}

    def _fake_time() -> float:
        tick["i"] += 1
        return float(tick["i"])
    monkeypatch.setattr("time.time", _fake_time)
    monitor_ffmpeg_progress(
        proc=proc,
        duration_sec=20.0,
        progress_signal=_Prog(),
        check_disk_space_callback=lambda: True,
        logger=_logger(),
    )
    assert killed == [987]

def test_extreme_05_config_manager_recovers_from_corrupted_json_and_persists(tmp_path: Path) -> None:
    cfg = tmp_path / "config" / "main_app.conf"
    cfg.parent.mkdir(parents=True, exist_ok=True)
    cfg.write_text("{not: valid json", encoding="utf-8")
    cm = ConfigManager(str(cfg))
    assert cm.config == {}, "Corrupted JSON must fail closed to empty config"
    cm.save_config({"custom_mp3_dir": "D:/music", "last_speed": 1.7})
    reloaded = ConfigManager(str(cfg))
    assert reloaded.config.get("custom_mp3_dir") == "D:/music"
    assert reloaded.config.get("last_speed") == 1.7

def test_extreme_06_media_prober_sample_rate_falls_back_to_48000_on_invalid_probe(monkeypatch) -> None:
    p = MediaProber(bin_dir="C:/invalid", input_path="dummy.mp4")
    monkeypatch.setattr(p, "_run_command", lambda _args: "N/A")
    assert p.get_sample_rate() == 48000

def test_extreme_07_waveform_ready_ignores_stale_result_and_cleans_temp_files(tmp_path: Path) -> None:
    tmp_png = tmp_path / "wave.png"
    tmp_sync = tmp_path / "sync.wav"
    tmp_png.write_bytes(b"x")
    tmp_sync.write_bytes(b"x")
    host = types.SimpleNamespace(_wave_target_path="wanted.mp3")
    MergerMusicWizardWaveformMixin._on_waveform_ready(
        host,
        track_path="other.mp3",
        duration_sec=12.0,
        pixmap=None,
        temp_png_path=str(tmp_png),
        temp_sync_path=str(tmp_sync),
    )
    assert not tmp_png.exists()
    assert not tmp_sync.exists()

class _WaveSlider:
    def __init__(self) -> None:
        self.ranges: list[tuple[int, int]] = []
        self.values: list[int] = []

    def setRange(self, a: int, b: int) -> None:
        self.ranges.append((int(a), int(b)))

    def setValue(self, value: int) -> None:
        self.values.append(int(value))

class _WavePreview:
    def __init__(self) -> None:
        self.texts: list[str] = []

    def setText(self, text: str) -> None:
        self.texts.append(str(text))

def test_extreme_08_waveform_error_falls_back_to_probe_duration_and_resets_slider() -> None:
    host = types.SimpleNamespace(
        _wave_target_path="track.mp3",
        logger=_logger(),
        _probe_media_duration=lambda _p: 9.876,
        offset_slider=_WaveSlider(),
        wave_preview=_WavePreview(),
    )
    MergerMusicWizardWaveformMixin._on_waveform_error(host, "track.mp3", "Waveform timed out")
    assert abs(host.current_track_dur - 9.876) < 1e-9
    assert host.offset_slider.ranges[-1] == (0, 9876)
    assert host.offset_slider.values[-1] == 0
    assert host.wave_preview.texts[-1] == "Waveform timed out"

def test_extreme_09_project_time_to_source_ms_respects_gaps_between_speed_segments() -> None:
    host = types.SimpleNamespace(
        trim_start_ms=1000,
        speed_factor=1.0,
        speed_segments=[
            {"start": 1000, "end": 2000, "speed": 0.5},
            {"start": 3000, "end": 4000, "speed": 2.0},
        ],
    )
    host._calculate_wall_clock_time_raw = types.MethodType(
        MergerMusicWizardMiscMixin._calculate_wall_clock_time_raw, host
    )
    MergerMusicWizardMiscMixin._cache_wall_times(host)
    assert MergerMusicWizardMiscMixin._project_time_to_source_ms(host, 2.0) == 2000
    assert MergerMusicWizardMiscMixin._project_time_to_source_ms(host, 2.5) == 2500

class _MusicPlayer:
    def __init__(self) -> None:
        self._playing = True
        self._time = 0
        self._rate = 1.3
        self.pause_calls = 0

    def is_playing(self) -> bool:
        return self._playing

    def play(self) -> None:
        self._playing = True
    @property
    def pause(self) -> bool:
        return not self._playing
    @pause.setter
    def pause(self, value: bool) -> None:
        self._playing = not value
        if value:
            self.pause_calls += 1

    def pause_method(self) -> None:
        self.pause = True

    def get_time(self) -> int:
        return int(self._time)

    def set_time(self, value: int) -> None:
        self._time = int(value)

    def audio_set_mute(self, _mute: bool) -> None:
        return None

    def audio_set_volume(self, _vol: int) -> None:
        return None

    def get_rate(self) -> float:
        return float(self._rate)

    def set_rate(self, value: float) -> None:
        self._rate = float(value)

def test_extreme_10_set_player_position_force_pause_beats_autoplay_inside_music_window() -> None:
    music = _MusicPlayer()
    host = types.SimpleNamespace(
        _mpv_lock=threading.RLock(),
        _scrub_lock=threading.RLock(),
        mpv_music_player=music,
        _music_preview_player=music,
        _wizard_tracks=[("song.mp3", 0.0, 10.0)],
        music_timeline_start_ms=1000,
        music_timeline_end_ms=5000,
        wants_to_play=True,
        speed_spinbox=DummySpinBox(1.5),
        granular_checkbox=DummyCheckBox(False),
        _get_music_offset_ms=lambda: 0,
        _music_eff=lambda: 80,
        logger=_logger(),
        player=types.SimpleNamespace(set_time=lambda *_: None, command=lambda *a: None),
    )
    PlayerMixin.set_player_position(host, 2500, sync_only=True, force_pause=True)
    assert music.pause_calls >= 1
    assert music.is_playing() is False
