from __future__ import annotations
import types
from sanity_tests._real_sanity_harness import DummyCheckBox, DummyMediaPlayer, install_qt_mpv_stubs
install_qt_mpv_stubs()

from ui.parts.player_mixin import PlayerMixin
import ui.widgets.granular_speed_editor as granular_mod
from ui.widgets.granular_speed_editor import GranularSpeedEditor
from ui.widgets.music_wizard_waveform import MergerMusicWizardWaveformMixin as MainWaveformMixin
from ui.widgets.music_wizard_timeline import MergerMusicWizardTimelineMixin as MainTimelineMixin
from utilities.merger_music_wizard_waveform import MergerMusicWizardWaveformMixin as MergerWaveformMixin
from utilities.merger_music_wizard_timeline import MergerMusicWizardTimelineMixin as MergerTimelineMixin

def _logger() -> object:
    return types.SimpleNamespace(
        info=lambda *a, **k: None,
        warning=lambda *a, **k: None,
        error=lambda *a, **k: None,
        exception=lambda *a, **k: None,
        critical=lambda *a, **k: None,
        debug=lambda *a, **k: None,
    )

class _Slider:
    def __init__(self, down: bool) -> None:
        self._down = bool(down)
        self.values: list[int] = []

    def isSliderDown(self) -> bool:
        return self._down

    def blockSignals(self, _block: bool) -> None:
        return None

    def setValue(self, value: int) -> None:
        self.values.append(int(value))

class _SeekOnlyPlayer:
    def __init__(self) -> None:
        self.set_time_calls: list[int] = []
        self._time = 0

    def command(self, cmd: str, *args) -> None:
        if cmd == "seek":
            self.set_time(int(float(args[0]) * 1000))

    def seek(self, seconds: float, reference='absolute', precision='exact') -> None:
        self.set_time(int(seconds * 1000))
        
    def set_time(self, ms: int) -> None:
        self._time = int(ms)
        self.set_time_calls.append(int(ms))
        
    def get_time(self) -> int:
        return self._time

class _SeekHost:
    """Tiny host object compatible with PlayerMixin.set_player_position self-contract."""

    def __init__(self, player: _SeekOnlyPlayer) -> None:
        self.player = player
        self._wizard_tracks = []
        self.wants_to_play = False
        self.music_timeline_start_ms = 0
        self.music_timeline_end_ms = 0
        self._mpv_lock = __import__("threading").Lock()
        self._scrub_lock = __import__("threading").RLock()
        self._pending_seek_ms = None

    def _safe_mpv_get(self, prop, default=None):
        return default

    def _safe_mpv_set(self, prop, value, target_player=None):
        p = target_player if target_player is not None else self.player
        try:
            setattr(p, prop, value)
        except Exception:
            return None

    def _execute_throttled_seek(self):
        PlayerMixin._execute_throttled_seek(self)

def test_main_preview_slider_guard_and_seek_throttle_reduce_stutter(monkeypatch) -> None:
    """
    Main opening preview should avoid fighting user drag and should throttle seeks
    to prevent stutter/sluggish behavior during rapid scrubbing.
    """
    host = types.SimpleNamespace(
        player=DummyMediaPlayer(playing=True, current_ms=1300, rate=1.1),
        positionSlider=_Slider(down=True),
        granular_checkbox=DummyCheckBox(False),
        speed_spinbox=types.SimpleNamespace(value=lambda: 1.1),
        logger=_logger(),
        is_playing=False,
    )
    PlayerMixin.update_player_state(host)
    assert host.positionSlider.values == [], (
        "Expected no slider write while user is dragging (to avoid jitter), "
        f"but got writes={host.positionSlider.values}."
    )
    t = {"v": 100.0}
    monkeypatch.setattr("time.time", lambda: t["v"])
    seek_host = _SeekHost(_SeekOnlyPlayer())
    PlayerMixin.set_player_position(seek_host, 1000, sync_only=False)
    if hasattr(seek_host, "_seek_timer"):
        seek_host._execute_throttled_seek()
    t["v"] = 100.01
    PlayerMixin.set_player_position(seek_host, 1200, sync_only=False)
    assert len(seek_host.player.set_time_calls) == 1, (
        "Expected second rapid seek (<50ms) to be throttled for anti-stutter, "
        f"but set_time_calls={seek_host.player.set_time_calls}."
    )

class _RatePlayer:
    def __init__(self, rate: float = 1.1) -> None:
        self._rate = float(rate)
        self.set_rate_calls: list[float] = []
    @property
    def speed(self) -> float:
        return float(self._rate)
    @speed.setter
    def speed(self, value: float) -> None:
        self._rate = float(value)
        self.set_rate_calls.append(float(value))

def test_granular_preview_rate_updates_are_debounced_to_avoid_sluggy_playback(monkeypatch) -> None:
    """
    Granular preview should not spam rate changes too quickly, which can cause
    sluggish/stuttery playback while scrubbing.
    """
    clock = {"v": 10.0}
    monkeypatch.setattr(granular_mod.import_time, "time", lambda: clock["v"])
    host = types.SimpleNamespace(
        base_speed=1.1,
        speed_segments=[{"start": 0, "end": 4000, "speed": 2.0}],
        timeline=types.SimpleNamespace(isSliderDown=lambda: False),
        player=_RatePlayer(1.1),
        _last_rate_update=0.0,
        _safe_mpv_get=lambda prop, default=None: getattr(host.player, prop, default),
        _safe_mpv_set=lambda prop, val: setattr(host.player, prop, val),
    )
    GranularSpeedEditor.update_playback_speed(host, 1000)
    host.player._rate = 1.1
    clock["v"] = 10.01
    GranularSpeedEditor.update_playback_speed(host, 1100)
    host.player._rate = 1.1
    clock["v"] = 10.20
    GranularSpeedEditor.update_playback_speed(host, 1200)
    assert len(host.player.set_rate_calls) == 2, (
        "Expected debounce to skip immediate 2nd rate update and allow later update; "
        f"actual set_rate_calls={host.player.set_rate_calls}."
    )

def test_granular_preview_seek_commands_are_throttled_and_release_deduped(monkeypatch) -> None:
    """
    Granular preview scrubbing must not issue one MPV seek per mouse event, and
    the playhead release path must not send an exact seek followed by the same
    keyframe seek.
    """
    clock = {"v": 100.0}
    monkeypatch.setattr(granular_mod.import_time, "monotonic", lambda: clock["v"])
    commands: list[tuple] = []
    speed_updates: list[int] = []
    host = types.SimpleNamespace(
        player=object(),
        duration=10_000,
        abs_trim_start=0,
        last_position_ms=0,
        _pending_seek=None,
        _seek_timer=None,
        _last_seek_command_ts=0.0,
        _last_seek_target_ms=None,
        _last_seek_exact=False,
        _in_freeze_segment=False,
        _freeze_seg_idx=-1,
        _safe_mpv_command=lambda *args: commands.append(args) or True,
        update_playback_speed=lambda rel: speed_updates.append(int(rel)),
    )
    for name in (
        "_normalize_seek_position",
        "_start_seek_timer",
        "_stop_seek_timer",
        "_last_seek_timestamp",
        "_is_duplicate_seek",
        "_issue_seek_now",
    ):
        setattr(host, name, types.MethodType(getattr(GranularSpeedEditor, name), host))
    GranularSpeedEditor.seek_video(host, 1000, exact=False)
    clock["v"] += 0.010
    GranularSpeedEditor.seek_video(host, 1200, exact=False)
    clock["v"] += 0.010
    GranularSpeedEditor.seek_video(host, 1500, exact=False)
    assert len(commands) == 1, f"Expected rapid drag seeks to coalesce; commands={commands!r}."
    assert host._pending_seek == (1500, False), "Expected latest scrub position to be retained as pending."
    clock["v"] += 0.050
    GranularSpeedEditor._flush_pending_seek(host)
    assert len(commands) == 2, f"Expected one flushed pending seek; commands={commands!r}."
    assert commands[-1] == ("seek", 1.5, "absolute", "keyframes")
    GranularSpeedEditor.seek_video(host, 1500, exact=True)
    assert len(commands) == 3, "Expected exact release seek after a keyframe drag seek."
    GranularSpeedEditor.seek_video(host, 1500, exact=False)
    assert len(commands) == 3, (
        "Expected duplicate keyframe seek after exact release to be suppressed; "
        f"commands={commands!r}."
    )
    assert speed_updates[-1] == 1500

def test_granular_safe_mpv_command_uses_guarded_seek_route() -> None:
    """
    Regression contract: granular editor must not bypass the MPV safety manager
    for seeks, otherwise the shared native seek gate cannot protect scrubbing.
    """

    import threading

    class _GatedSeekPlayer:
        def __init__(self) -> None:
            self.gated_calls: list[tuple] = []
            self.raw_commands: list[tuple] = []

        def _submit_gated_seek(self, target, reference='absolute', precision='exact') -> bool:
            self.gated_calls.append((target, reference, precision))
            return True

        def command(self, *args) -> None:
            self.raw_commands.append(args)
    player = _GatedSeekPlayer()
    host = types.SimpleNamespace(
        player=player,
        _mpv_lock=threading.RLock(),
        _mpv_lock_timeout=0.1,
    )
    assert GranularSpeedEditor._safe_mpv_command(host, "seek", 1.25, "absolute", "keyframes")
    assert player.gated_calls == [(1.25, "absolute", "keyframes")]
    assert player.raw_commands == [], "Raw player.command('seek') bypasses crash protection."

class _StrictIntTimeline:
    def __init__(self) -> None:
        self.values: list[int] = []

    def isSliderDown(self) -> bool:
        return False

    def blockSignals(self, _block: bool) -> None:
        return None

    def setValue(self, value: int) -> None:
        assert isinstance(value, int), (
            "Granular editor must pass int into timeline.setValue; "
            f"got type={type(value).__name__}, value={value!r}"
        )
        self.values.append(value)

    def update(self) -> None:
        return None

def test_granular_update_ui_writes_int_only_to_timeline_slider_contract() -> None:
    """
    Regression contract: update_ui must never pass float to QSlider.setValue.
    Qt requires int and raises TypeError on float (real crash seen by users).
    """

    class _FloatPosPlayer:
        def __init__(self) -> None:
            self._time_pos = 0.0
            setattr(self, "time-pos", 0.0)
    player = _FloatPosPlayer()
    setattr(player, "time-pos", 1.23456)
    speed_calls: list[int] = []

    import threading
    host = types.SimpleNamespace(
        player=player,
        timeline=_StrictIntTimeline(),
        duration=5000,
        _current_play_window_ms=lambda: (0, 5000),
        pause_video=lambda: None,
        update_playback_speed=lambda t: speed_calls.append(t),
        last_position_ms=0,
        abs_trim_start=0,
        _mpv_lock=threading.RLock(),
        _mpv_lock_timeout=0.1,
        _safe_mpv_get=lambda prop, default=None: getattr(player, prop, default),
        _safe_mpv_set=lambda prop, val: setattr(player, prop, val),
    )
    GranularSpeedEditor.update_ui(host)
    assert host.timeline.values, "Expected update_ui to push current time into slider."
    assert isinstance(host.timeline.values[-1], int), "Slider write must stay int-only."
    assert speed_calls and speed_calls[-1] == host.timeline.values[-1], (
        "Expected speed update to use same int timeline value written to slider."
    )
    assert isinstance(host.last_position_ms, int), "Resume position must be stored as int ms."

class _OffsetSlider:
    def __init__(self, max_ms: int) -> None:
        self._max = int(max_ms)
        self.value = 0

    def maximum(self) -> int:
        return self._max

    def setValue(self, value: int) -> None:
        self.value = int(value)

class _AudioPlayer:
    def __init__(self) -> None:
        self.set_time_calls: list[int] = []

    def set_time(self, ms: int) -> None:
        self.set_time_calls.append(int(ms))

    def seek(self, seconds: float, reference='absolute', precision='exact') -> None:
        self.set_time(int(seconds * 1000))

def test_main_wizard_step2_click_to_seek_keeps_player_and_caret_in_sync() -> None:
    caret = {"count": 0}
    host = types.SimpleNamespace(
        _draw_w=500,
        _draw_x0=0,
        offset_slider=_OffsetSlider(10_000),
        player=_AudioPlayer(),
        _last_good_mpv_ms=0,
        _sync_caret=lambda override_ms=None: caret.__setitem__("count", caret["count"] + 1),
        logger=_logger(),
        _wave_dragging=False,
        _safe_mpv_get=lambda p, prop, default=None: getattr(p, prop, default),
        _safe_mpv_set=lambda p, prop, val: setattr(p, prop, val),
    )
    host._request_step2_seek = types.MethodType(MainWaveformMixin._request_step2_seek, host)
    host._flush_step2_seek = types.MethodType(MainWaveformMixin._flush_step2_seek, host)
    MainWaveformMixin._set_time_from_wave_x(host, 250)
    assert host.offset_slider.value == 5000, (
        f"Expected click-to-seek midpoint to map to 5000ms, got {host.offset_slider.value}ms."
    )
    assert host.player.set_time_calls[-1] == 5000, (
        "Expected audio preview player to seek to clicked time, "
        f"got calls={host.player.set_time_calls}."
    )
    assert caret["count"] >= 1, "Expected caret sync call after click-to-seek, but none occurred."

def test_merger_wizard_step2_click_to_seek_keeps_player_and_caret_in_sync() -> None:
    caret = {"count": 0}
    host = types.SimpleNamespace(
        _draw_w=500,
        _draw_x0=0,
        offset_slider=_OffsetSlider(10_000),
        player=_AudioPlayer(),
        _last_good_mpv_ms=0,
        _sync_caret=lambda override_ms=None: caret.__setitem__("count", caret["count"] + 1),
        logger=_logger(),
        _wave_dragging=False,
        _safe_mpv_get=lambda p, prop, default=None: getattr(p, prop, default),
        _safe_mpv_set=lambda p, prop, val: setattr(p, prop, val),
    )
    host._request_step2_seek = types.MethodType(MergerWaveformMixin._request_step2_seek, host)
    host._flush_step2_seek = types.MethodType(MergerWaveformMixin._flush_step2_seek, host)
    MergerWaveformMixin._set_time_from_wave_x(host, 100)
    assert host.offset_slider.value == 2000, (
        f"Expected click-to-seek at x=100/500 to map to 2000ms, got {host.offset_slider.value}ms."
    )
    assert host.player.set_time_calls[-1] == 2000, (
        "Expected merger step2 player to seek to clicked time, "
        f"got calls={host.player.set_time_calls}."
    )
    assert caret["count"] >= 1, "Expected caret sync call after merger step2 click-to-seek, but none occurred."

class _Media:
    def __init__(self, mrl: str) -> None:
        self._mrl = mrl

    def get_mrl(self) -> str:
        return self._mrl

class _MainTimelineVideoPlayer:
    def __init__(self, mrl: str) -> None:
        self._media = _Media(mrl)
        self.set_time_calls: list[int] = []
        self.path = mrl
        self.pause = True
        self.volume = 100
        self.mute = False

    def get_full_state(self) -> dict[str, int]:
        return {"state": 3, "time": 0, "length": 20_000}

    def get_media(self):
        return self._media

    def set_media(self, media) -> None:
        self._media = media

    def play(self, *args) -> None:
        self.pause = False

    def seek(self, seconds: float, reference='absolute', precision='exact') -> None:
        ms = int(seconds * 1000)
        self.set_time_calls.append(ms)

class _MergerTimelineVideoPlayer:
    def __init__(self, mrl: str) -> None:
        self._media = _Media(mrl)
        self.set_time_calls: list[int] = []
        self.path = mrl
        self.pause = True
        self.volume = 100
        self.mute = False

    def get_state(self) -> int:
        return 3

    def get_media(self):
        return self._media

    def set_media(self, media) -> None:
        self._media = media

    def play(self, *args) -> None:
        self.pause = False

    def seek(self, seconds: float, reference='absolute', precision='exact') -> None:
        ms = int(seconds * 1000)
        self.set_time_calls.append(ms)

class _Timeline:
    def __init__(self) -> None:
        self.values: list[float] = []

    def set_current_time(self, value: float) -> None:
        self.values.append(float(value))

def test_main_wizard_step3_timeline_seek_syncs_video_and_caret_without_lag() -> None:
    sync_calls: list[tuple[float, bool | None]] = []
    caret = {"count": 0}
    host = types.SimpleNamespace(
        _last_seek_ts=0.0,
        total_video_sec=20.0,
        stack=types.SimpleNamespace(currentIndex=lambda: 2),
        timeline=_Timeline(),
        player=_MainTimelineVideoPlayer("clip_a.mp4"),
        video_segments=[{"path": "clip_a.mp4", "duration": 20.0}],
        selected_tracks=[],
        _current_elapsed_offset=0.0,
        mpv_v=types.SimpleNamespace(media_new=lambda path: _Media(path)),
        video_vol_slider=types.SimpleNamespace(value=lambda: 80),
        _scaled_vol=lambda v: int(v),
        speed_factor=1.1,
        _project_time_to_source_ms=lambda sec: int(sec * 1000),
        _sync_caret=lambda: caret.__setitem__("count", caret["count"] + 1),
        logger=_logger(),
        _is_scrubbing_timeline=False,
    )
    host._sync_all_players_to_time = types.MethodType(MainTimelineMixin._sync_all_players_to_time, host)
    host._ensure_step3_seek_timer = types.MethodType(MainTimelineMixin._ensure_step3_seek_timer, host)
    host._flush_pending_step3_seek = types.MethodType(MainTimelineMixin._flush_pending_step3_seek, host)
    host._apply_step3_seek_target = types.MethodType(MainTimelineMixin._apply_step3_seek_target, host)
    host._safe_mpv_get = lambda p, prop, default=None: getattr(p, prop, default)
    host._safe_mpv_set = lambda p, prop, val: setattr(p, prop, val)
    host._safe_mpv_loadfile = lambda *a, **k: True
    host._safe_mpv_seek = lambda p, s, **k: p.seek(s)
    MainTimelineMixin._on_timeline_seek(host, 0.4)
    assert host.timeline.values and abs(host.timeline.values[-1] - 8.0) < 1e-6, (
        f"Expected timeline seek target 8.0s, got {host.timeline.values[-1] if host.timeline.values else 'none'}."
    )
    assert host.player.set_time_calls and host.player.set_time_calls[-1] == 8000, (
        "Expected video preview seek to source 8000ms for 40% click on 20s timeline, "
        f"got calls={host.player.set_time_calls}."
    )
    assert caret["count"] >= 1, "Expected caret sync after main wizard step3 seek, but none occurred."

def test_merger_wizard_step3_timeline_seek_syncs_video_and_caret_without_lag() -> None:
    caret = {"count": 0}
    host = types.SimpleNamespace(
        _last_seek_ts=0.0,
        total_video_sec=20.0,
        stack=types.SimpleNamespace(currentIndex=lambda: 2),
        timeline=_Timeline(),
        player=_MergerTimelineVideoPlayer("clip_b.mp4"),
        video_segments=[{"path": "clip_b.mp4", "duration": 20.0}],
        selected_tracks=[],
        _current_elapsed_offset=0.0,
        mpv_v=types.SimpleNamespace(media_new=lambda path: _Media(path)),
        video_vol_slider=types.SimpleNamespace(value=lambda: 80),
        _scaled_vol=lambda v: int(v),
        speed_factor=1.1,
        _project_time_to_source_ms=lambda sec: int(sec * 1000),
        _sync_caret=lambda: caret.__setitem__("count", caret["count"] + 1),
        logger=_logger(),
        _is_scrubbing_timeline=False,
    )
    host._sync_all_players_to_time = types.MethodType(MergerTimelineMixin._sync_all_players_to_time, host)
    host._ensure_step3_seek_timer = types.MethodType(MergerTimelineMixin._ensure_step3_seek_timer, host)
    host._flush_pending_step3_seek = types.MethodType(MergerTimelineMixin._flush_pending_step3_seek, host)
    host._apply_step3_seek_target = types.MethodType(MergerTimelineMixin._apply_step3_seek_target, host)
    host._safe_mpv_get = lambda p, prop, default=None: getattr(p, prop, default)
    host._safe_mpv_set = lambda p, prop, val: setattr(p, prop, val)
    host._safe_mpv_loadfile = lambda *a, **k: True
    host._safe_mpv_seek = lambda p, s, **k: p.seek(s)
    MergerTimelineMixin._on_timeline_seek(host, 0.25)
    assert host.timeline.values and abs(host.timeline.values[-1] - 5.0) < 1e-6, (
        f"Expected merger timeline seek target 5.0s, got {host.timeline.values[-1] if host.timeline.values else 'none'}."
    )
    assert host.player.set_time_calls and host.player.set_time_calls[-1] == 5000, (
        "Expected merger step3 video seek to 5000ms for 25% click on 20s timeline, "
        f"got calls={host.player.set_time_calls}."
    )
    assert caret["count"] >= 1, "Expected caret sync after merger wizard step3 seek, but none occurred."
