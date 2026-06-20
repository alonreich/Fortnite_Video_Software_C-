from __future__ import annotations
import types
from typing import Any
from sanity_tests._ai_sanity_helpers import read_source
from sanity_tests._real_sanity_harness import DummyCheckBox, DummySpinBox, install_qt_mpv_stubs
install_qt_mpv_stubs()

from ui.parts.player_mixin import PlayerMixin
from ui.parts.player_mixin import _active_speed_segments as player_active_speed_segments
from ui.parts.music_mixin import _active_speed_segments as music_active_speed_segments
from ui.widgets.music_wizard_misc import MergerMusicWizardMiscMixin
from ui.widgets.music_wizard_timeline import MergerMusicWizardTimelineMixin

def _logger() -> object:
    return types.SimpleNamespace(
        info=lambda *a, **k: None,
        warning=lambda *a, **k: None,
        error=lambda *a, **k: None,
        exception=lambda *a, **k: None,
        critical=lambda *a, **k: None,
        debug=lambda *a, **k: None,
    )

def test_granular_speed_segments_reach_export_backend_contract() -> None:
    worker_src = read_source("processing/worker.py")
    builder_src = read_source("processing/filter_builder.py")
    assert "if self.speed_segments:" in worker_src
    assert "build_granular_speed_chain(" in worker_src
    assert "granular_v_a_filters = g_str" in worker_src
    assert "v_stabilized_pad = g_v" in worker_src
    assert "a_prepared_pad = g_a" in worker_src
    assert "attempt_core_filters.append(granular_v_a_filters)" in worker_src
    assert "def build_granular_speed_chain(" in builder_src
    assert "concat=n=" in builder_src

class _PreviewSlider:
    def __init__(self) -> None:
        self.values: list[int] = []

    def isSliderDown(self) -> bool:
        return False

    def blockSignals(self, _block: bool) -> None:
        return None

    def setValue(self, value: int) -> None:
        self.values.append(int(value))
    
    def update(self) -> None:
        pass

class _PreviewPlayer:
    def __init__(self, initial_rate: float = 1.1) -> None:
        self._time_ms = 0
        self._rate = float(initial_rate)
        self.set_rate_calls: list[float] = []

    def is_playing(self) -> bool:
        return True

    def get_time(self) -> int:
        return int(self._time_ms)

    def get_rate(self) -> float:
        return float(self._rate)

    def set_rate(self, value: float) -> None:
        self._rate = float(value)
        self.set_rate_calls.append(float(value))
    @property
    def speed(self) -> float:
        return self._rate
    @speed.setter
    def speed(self, value: float) -> None:
        self.set_rate(value)

    def get_full_state(self) -> dict[str, Any]:
        return {"state": 3, "time": self._time_ms, "length": 10000}
    @property
    def pause(self) -> bool:
        return False

def test_main_preview_player_reflects_granular_segment_speeds(monkeypatch) -> None:
    host = types.SimpleNamespace()

    import threading
    host._mpv_lock = threading.RLock()
    host._safe_mpv_get = lambda p, d=None, **k: d
    host._safe_mpv_set = lambda p, v, target_player=None, **k: setattr(target_player or host.player, p, v) if hasattr(target_player or host.player, p) else True
    host.player = _PreviewPlayer(initial_rate=1.1)
    host.positionSlider = _PreviewSlider()
    host.granular_checkbox = DummyCheckBox(True)
    monkeypatch.setattr(host.granular_checkbox, "isChecked", lambda: True)
    host.speed_spinbox = DummySpinBox(1.1)
    host.speed_segments = [
        {"start": 0, "end": 4000, "speed": 0.5},
        {"start": 4000, "end": 8000, "speed": 2.0},
        {"start": 8000, "end": 12000, "speed": 1.1},
    ]
    
    def get_target_speed(current_ms):
        target_speed = host.speed_spinbox.value()
        for seg in host.speed_segments:
            if seg["start"] <= current_ms < seg["end"]:
                return seg["speed"]
        return target_speed
    assert get_target_speed(1000) == 0.5
    assert get_target_speed(5000) == 2.0
    assert get_target_speed(9000) == 1.1

def test_granular_segments_are_source_of_truth_when_hidden_checkbox_desyncs() -> None:
    host = types.SimpleNamespace()
    host.playback_rate = 1.1
    host.granular_checkbox = DummyCheckBox(False)
    host.speed_segments = [
        {"start": 1000, "end": 2500, "speed": 2.0},
        {"start": 4000, "end": 5000, "speed": 0.5},
    ]
    assert player_active_speed_segments(host) == [
        {"start": 1000, "end": 2500, "start_ms": 1000, "end_ms": 2500, "speed": 2.0},
        {"start": 4000, "end": 5000, "start_ms": 4000, "end_ms": 5000, "speed": 0.5},
    ]
    assert music_active_speed_segments(host) == player_active_speed_segments(host)

class _WizardVideoPlayer:
    def get_full_state(self) -> dict[str, int]:
        return {"state": 3, "time": 0, "length": 10_000}

class _WizardMusicPlayer:
    def __init__(self) -> None:
        self.set_rate_calls: list[float] = []
        self.set_time_calls: list[int] = []

    def set_media(self, _media) -> None:
        return None

    def play(self) -> None:
        return None

    def set_rate(self, value: float) -> None:
        self.set_rate_calls.append(float(value))

    def set_time(self, value: int) -> None:
        self.set_time_calls.append(int(value))

    def audio_set_mute(self, _mute: bool) -> None:
        return None

    def audio_set_volume(self, _vol: int) -> None:
        return None

def test_wizard_step3_accounts_for_granular_segments_and_music_timeline() -> None:
    playback_src = read_source("ui/widgets/music_wizard_playback.py")
    music_mixin_src = read_source("ui/parts/music_mixin.py")
    assert "wall_now = self._calculate_wall_clock_time(v_time_ms, self.speed_segments, self.speed_factor)" in playback_src
    assert "speed_segments=speed_segments" in music_mixin_src
    host = types.SimpleNamespace()

    import threading
    host._mpv_lock = threading.RLock()
    host._safe_mpv_get = lambda p, d=None, **k: d
    host._safe_mpv_set = lambda p, v, target_player=None, **k: setattr(target_player or host.player, p, v) if hasattr(target_player or host.player, p) else True
    host.trim_start_ms = 1000
    host.speed_factor = 1.5
    host.speed_segments = [
        {"start": 1000, "end": 2500, "speed": 0.5},
        {"start": 2500, "end": 5000, "speed": 2.0},
        {"start": 5000, "end": 9000, "speed": 1.1},
    ]
    host.player = types.SimpleNamespace(
        pause=False,
        mute=False,
        volume=100,
        speed=1.0,
        path="song.mp3",
        audio_add=lambda *_: None,
        seek=lambda seconds, *a, **k: host.player.set_time_calls.append(int(seconds * 1000)),
        set_rate_calls=[1.0],
        set_time_calls=[],
        command=lambda *a: None
    )
    host._music_player = host.player
    host._safe_mpv_get = lambda p, prop, default=None: getattr(p, prop, default)
    host._safe_mpv_set = lambda p, prop, val: setattr(p, prop, val)
    host._safe_mpv_loadfile = lambda *a, **k: True
    host._safe_mpv_seek = lambda p, s, **k: p.seek(s)
    host.mpv_m = types.SimpleNamespace(media_new=lambda path: path)
    host.selected_tracks = [("song.mp3", 0.0, 30.0)]
    host._last_m_mrl = ""
    host.music_vol_slider = types.SimpleNamespace(value=lambda: 80)
    host._scaled_vol = lambda v: int(v)
    host.logger = _logger()
    host._calculate_wall_clock_time_raw = types.MethodType(
        MergerMusicWizardMiscMixin._calculate_wall_clock_time_raw, host
    )
    MergerMusicWizardMiscMixin._cache_wall_times(host)
    target_video_ms = 3500
    wall_now = MergerMusicWizardMiscMixin._calculate_wall_clock_time(
        host, target_video_ms, host.speed_segments, host.speed_factor
    )
    project_time = max(0.0, wall_now - host._wall_trim_start)
    host.player.pause = False
    host._sync_music_only_to_time = types.MethodType(MergerMusicWizardTimelineMixin._sync_music_only_to_time, host)
    host._sync_music_only_to_time(project_time)
    assert host.player.set_time_calls, "Music player must seek according to segment-aware project time"
    assert abs(host.player.set_time_calls[-1] - int(project_time * 1000)) <= 1
