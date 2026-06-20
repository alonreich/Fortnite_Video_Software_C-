from __future__ import annotations
from pathlib import Path
import tempfile
import types
import os
import sys
from sanity_tests._real_sanity_harness import (
    DummyButton,
    DummyCheckBox,
    DummyConfigManager,
    DummyMediaPlayer,
    DummySlider,
    DummySpinBox,
    DummyTimer,
    DummyListItem,
    DummyKeyEvent,
    DummyLogger,
    install_qt_mpv_stubs,
)
install_qt_mpv_stubs()

from processing.filter_builder import FilterBuilder
from system.config import ConfigManager
from system.utils import ConsoleManager
from ui.parts.player_mixin import PlayerMixin
from ui.parts.music_mixin import MusicMixin
from ui.widgets.music_wizard_widgets import SearchableListWidget
import threading

def _player_host(*, speed: float = 2.0, granular: bool = False) -> object:
    host = types.SimpleNamespace()

    import threading
    host._mpv_lock = threading.RLock()
    host._safe_mpv_get = lambda p, d=None, **k: d
    host._safe_mpv_set = lambda p, v, target_player=None, **k: setattr(target_player or host.player, p, v) if hasattr(target_player or host.player, p) else True
    host.player = DummyMediaPlayer(playing=True, current_ms=0, rate=speed)
    host.music_timeline_start_ms = 1000
    host.music_timeline_end_ms = 9000
    host.wants_to_play = True
    host.speed_spinbox = DummySpinBox(speed)
    host.granular_checkbox = DummyCheckBox(granular)
    host.speed_segments = [
        {"start": 0, "end": 2000, "speed": 0.5},
        {"start": 2000, "end": 4000, "speed": 2.0},
        {"start": 4000, "end": 9000, "speed": 1.1},
    ]
    host._wizard_tracks = [("song.mp3", 0.5, 8.0)]
    host._get_music_offset_ms = lambda: 500
    host._calculate_wall_clock_time = lambda video_ms, _segments, base: float(video_ms) / base
    host.logger = DummyLogger()
    host._music_eff = lambda: 80
    host._get_music_offset_ms = lambda: 0
    host._scrub_lock = threading.RLock()
    return host

def test_core_01_constant_tempo_music_rate_locked_to_1x() -> None:
    host = _player_host(speed=3.1, granular=False)
    host.mpv_music_player = DummyMediaPlayer(playing=True, current_ms=0, rate=0.5)
    host._music_preview_player = host.mpv_music_player
    host._last_scrub_ts = 0.0
    PlayerMixin.set_player_position(host, 3000, sync_only=True)
    assert 1.0 in host.mpv_music_player.set_rate_calls, "Music rate must be locked to 1.0x"

def test_core_03_scrub_protection_throttles_under_50ms(monkeypatch) -> None:
    host = _player_host(speed=2.0)
    t = {"v": 10.0}
    monkeypatch.setattr("time.time", lambda: t["v"])
    PlayerMixin.set_player_position(host, 2000, sync_only=True)
    calls_after_first = len(host.player.set_time_calls)
    t["v"] = 10.02
    PlayerMixin.set_player_position(host, 2600, sync_only=True)
    assert len(host.player.set_time_calls) == calls_after_first

def test_core_04_smart_fading_scales_for_short_clips() -> None:
    fb = FilterBuilder(logger=types.SimpleNamespace(info=lambda *a, **k: None))
    chain = fb.build_audio_chain(
        music_config={"path": "song.mp3", "timeline_start_sec": 0.0, "timeline_end_sec": 0.1, "file_offset_sec": 0.0, "volume": 1.0, "main_vol": 1.0},
        video_start_time=0.0,
        video_end_time=0.1,
        speed_factor=1.0,
        disable_fades=False,
        vfade_in_d=0,
        audio_filter_cmd="anull",
        sample_rate=48000,
    )
    music_prepared = chain[1]
    assert "[a_music_prepared]" in music_prepared
    assert "afade=t=in" not in music_prepared
    assert "afade=t=out" not in music_prepared

def test_core_06_type_ahead_multi_char_focus() -> None:
    lst = SearchableListWidget()
    lst._items = [DummyListItem("alpha"), DummyListItem("akita"), DummyListItem("zebra")]
    lst.keyPressEvent(DummyKeyEvent("a"))
    lst.keyPressEvent(DummyKeyEvent("k"))
    lst.keyPressEvent(DummyKeyEvent("i"))
    assert getattr(lst, "_current", None) is lst._items[1]

def test_core_07_and_08_open_wizard_pauses_video_and_adds_overlay(monkeypatch) -> None:
    class DummyWizard:
        def __init__(self, *_a, **_k):
            self._vv = 100
            self.video_vol_slider = types.SimpleNamespace(
                setValue=lambda v: setattr(self, "_vv", v),
                value=lambda: self._vv,
            )
            self.music_vol_slider = types.SimpleNamespace(value=lambda: 80, setValue=lambda *_: None)
            self.selected_tracks = [("song.mp3", 0.0, 5.0)]

        def exec_(self):
            return 1

        def stop_previews(self):
            return None

    import sys
    sys.modules["ui.widgets.music_wizard"] = types.SimpleNamespace(MergerMusicWizard=DummyWizard)
    host = types.SimpleNamespace()

    import threading
    host._mpv_lock = threading.RLock()
    host._safe_mpv_get = lambda p, d=None, **k: d
    host._safe_mpv_set = lambda p, v, target_player=None, **k: setattr(target_player or host.player, p, v) if hasattr(target_player or host.player, p) else True
    host.player = DummyMediaPlayer(playing=True)
    host.wants_to_play = True
    host.playPauseButton = DummyButton()
    host.style = lambda: types.SimpleNamespace(standardIcon=lambda *_: None)
    host.timer = DummyTimer(active=True)
    host.original_duration_ms = 10_000
    host.base_dir = tempfile.gettempdir()
    host.bin_dir = tempfile.gettempdir()
    host.player_instance = types.SimpleNamespace(media_player_new=lambda: DummyMediaPlayer(), media_new=lambda *_: object())
    host.speed_spinbox = DummySpinBox(1.5)
    host._get_master_eff = lambda: 100
    host.volume_slider = types.SimpleNamespace(maximum=lambda: 100, minimum=lambda: 0, invertedAppearance=lambda: False, setValue=lambda *_: None)
    host.trim_start_ms = 1200
    host.trim_end_ms = 8200
    host.positionSlider = DummySlider()
    host.logger = DummyLogger()
    host.config_manager = DummyConfigManager(config={})
    host._mp3_dir = types.MethodType(MusicMixin._mp3_dir, host)
    host._reset_music_player = lambda: None
    host._delayed_wizard_launch = lambda: None
    MusicMixin.open_music_wizard(host)
    assert host.player.paused >= 1
    assert host.wants_to_play is False
    assert host.positionSlider.time_calls and host.positionSlider.time_calls[-1] == (1200, 8200)
    host._music_preview_player = DummyMediaPlayer()
    host._ensure_music_player_ready = lambda: True
    host._safe_mpv_command = lambda *a, **k: True
    host._sync_music_preview = lambda: None
    host._safe_seek_to_start = lambda *_: None
    host._final_unmute_after_wizard = lambda: None
    host._set_transition_false = lambda: None
    host.raise_ = lambda: None
    host.activateWindow = lambda: None
    host.video_surface = types.SimpleNamespace(show=lambda: None)
    host._bind_main_player_output = lambda: None
    host._set_music_button_state = lambda has_music: setattr(host, "_music_button_has_music", bool(has_music))
    MusicMixin._continue_wizard_return(host, 1, 1200, 8200, [], 1.5, DummyWizard())
    assert host.positionSlider._show_music is True
    assert host.positionSlider.music_start_ms == 1200
    assert host.positionSlider.music_end_ms == 8200
    assert host._music_button_has_music is True

def test_core_09_native_logs_faulthandler_pipeline(monkeypatch, tmp_path: Path) -> None:
    log_dir = tmp_path / "logs"
    log_dir.mkdir(parents=True, exist_ok=True)
    fh_calls: list[object] = []
    monkeypatch.setattr("faulthandler.enable", lambda *a, **_k: fh_calls.append(a[0] if a else None))
    monkeypatch.setattr("sys.stdout", open(os.devnull, "w", encoding="utf-8"), raising=False)
    monkeypatch.setattr("sys.stderr", open(os.devnull, "w", encoding="utf-8"), raising=False)
    logger_name = f"sanity_test_logger_{tmp_path.name}"
    ConsoleManager.initialize(str(tmp_path), "main_app.log", logger_name)
    log_dir_path = tmp_path / "logs"
    assert log_dir_path.exists()
    logs = list(log_dir_path.glob("*.log"))
    assert len(logs) >= 1, f"No log files found in {log_dir_path}"
    assert ConsoleManager._stdout_stream is sys.stdout
    assert ConsoleManager._stderr_stream is sys.stderr
    assert ConsoleManager._stdout_stream.__class__.__name__ == "ReopenableTextStream"
    assert ConsoleManager._stderr_stream.__class__.__name__ == "ReopenableTextStream"
    assert len(fh_calls) == 1

def test_core_10_folder_persistence_prioritizes_custom_path(tmp_path: Path) -> None:
    custom = tmp_path / "custom_music"
    custom.mkdir(parents=True, exist_ok=True)
    host = types.SimpleNamespace(base_dir=str(tmp_path), config_manager=DummyConfigManager(config={"custom_mp3_dir": str(custom)}))
    chosen = MusicMixin._mp3_dir(host)
    assert chosen == str(custom)

def test_core_10_multi_instance_config_reload_visibility(tmp_path: Path) -> None:
    conf = tmp_path / "main_app.conf"
    a = ConfigManager(str(conf))
    b = ConfigManager(str(conf))
    a.save_config({"custom_mp3_dir": "X:/music"})
    loaded = b.load_config()
    assert loaded.get("custom_mp3_dir") == "X:/music"
