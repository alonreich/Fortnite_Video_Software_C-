from __future__ import annotations
import types
from pathlib import Path
from sanity_tests._real_sanity_harness import (
    DummyButton,
    DummyCheckBox,
    DummyLogger,
    DummySlider,
    DummySpinBox,
    install_qt_mpv_stubs,
)
install_qt_mpv_stubs()

from ui.main_window import FortniteVideoSoftware
from ui.parts.music_mixin import MusicMixin

class _Geometry:
    def toBase64(self):
        return b"geometry"

class _TextInput:
    def __init__(self, value=""):
        self.value = value

    def setText(self, value):
        self.value = str(value)

    def text(self):
        return self.value

class _CaptureRecoveryManager:
    def __init__(self):
        self.saved = []

    def save_state_async(self, state):
        self.saved.append(state)

def _make_day_to_day_host(tmp_path: Path, *, mobile: bool, teammates: bool):
    video = tmp_path / ("portrait.mp4" if mobile else "landscape.mp4")
    music_a = tmp_path / "music_a.mp3"
    music_b = tmp_path / "music_b.mp3"
    video.write_bytes(b"video")
    music_a.write_bytes(b"music a")
    music_b.write_bytes(b"music b")
    recovery = _CaptureRecoveryManager()
    slider = DummySlider(22_222)
    slider.set_thumbnail_pos_ms(33_333)
    host = types.SimpleNamespace(
        recovery_manager=recovery,
        input_file_path=str(video),
        source_file_path=str(video),
        trim_start_ms=10_000,
        trim_end_ms=50_000,
        speed_spinbox=DummySpinBox(1.35),
        speed_segments=[
            {"start": 12_000, "end": 16_000, "speed": 0.0},
            {"start": 18_000, "end": 28_000, "speed": 2.75},
            {"start_ms": 35_000, "end_ms": 44_000, "speed": 0.65},
        ],
        volume_slider=DummySlider(64),
        quality_slider=DummySlider(17),
        _wizard_tracks=[(str(music_a), 7.25, 12.0), (str(music_b), 2.0, 8.5)],
        _current_music_path=str(music_a),
        _current_music_offset=7.25,
        _music_volume_pct=73,
        _video_volume_pct=66,
        music_timeline_start_ms=18_000,
        music_timeline_end_ms=38_500,
        positionSlider=slider,
        selected_intro_abs_time=33.333,
        hardware_strategy="NVIDIA",
        mobile_checkbox=DummyCheckBox(mobile),
        teammates_checkbox=DummyCheckBox(teammates),
        boss_hp_checkbox=DummyCheckBox(True),
        granular_checkbox=DummyCheckBox(True),
        no_fade_checkbox=DummyCheckBox(True),
        portrait_text_input=_TextInput("Recovery overlay text"),
        last_dir=str(tmp_path),
        saveGeometry=lambda: _Geometry(),
    )
    return host, recovery, video, music_a, music_b

def _make_restore_host(state):
    commands = []
    sets = []
    saved_after_restore = []
    host = types.SimpleNamespace(
        recovery_manager=types.SimpleNamespace(load_state=lambda: state),
        logger=DummyLogger(),
        input_file_path=None,
        original_duration_ms=60_000,
        trim_end_ms=60_000,
        positionSlider=DummySlider(),
        speed_spinbox=DummySpinBox(1.0),
        volume_slider=DummySlider(),
        quality_slider=DummySlider(),
        mobile_checkbox=DummyCheckBox(False),
        teammates_checkbox=DummyCheckBox(False),
        boss_hp_checkbox=DummyCheckBox(False),
        granular_checkbox=DummyCheckBox(False),
        no_fade_checkbox=DummyCheckBox(False),
        portrait_text_input=_TextInput(),
        music_button=DummyButton(),
        _music_preview_player=object(),
        hardware_strategy="Scanning...",
    )
    host.handle_file_selection = lambda path: setattr(host, "input_file_path", path)
    host._set_music_button_state = types.MethodType(MusicMixin._set_music_button_state, host)
    host._ensure_music_player_ready = lambda: True
    host._safe_mpv_command = lambda *args, **kwargs: commands.append((args, kwargs)) or True
    host._safe_mpv_set = lambda *args, **kwargs: sets.append((args, kwargs)) or True
    host._update_trim_widgets_from_trim_times = lambda: None
    host._update_quality_label = lambda: None
    host._update_granular_button_state = lambda: None
    host._maybe_enable_process = lambda: None
    host._save_recovery_state = lambda: saved_after_restore.append(True)
    host._apply_restored_slider_state = types.MethodType(FortniteVideoSoftware._apply_restored_slider_state, host)
    return host, commands, sets, saved_after_restore

def test_recovery_captures_and_restores_full_day_to_day_portrait_music_state(tmp_path, monkeypatch) -> None:
    capture_host, recovery, video, music_a, music_b = _make_day_to_day_host(
        tmp_path, mobile=True, teammates=True
    )
    FortniteVideoSoftware._save_recovery_state(capture_host)
    state = recovery.saved[-1]
    assert state["assets"]["input_file_path"] == str(video)
    assert state["assets"]["wizard_tracks"] == [
        {"path": str(music_a), "offset_sec": 7.25, "duration_sec": 12.0},
        {"path": str(music_b), "offset_sec": 2.0, "duration_sec": 8.5},
    ]
    assert state["assets"]["current_music_path"] == str(music_a)
    volatile = state["volatile_settings"]
    assert volatile["trim_start_ms"] == 10_000
    assert volatile["trim_end_ms"] == 50_000
    assert volatile["music_timeline_start_ms"] == 18_000
    assert volatile["music_timeline_end_ms"] == 38_500
    assert volatile["speed_segments"][0]["speed"] == 0.0
    assert volatile["speed_segments"][1]["speed"] == 2.75
    assert volatile["quality_slider_index"] == 17
    assert volatile["thumbnail_pos_ms"] == 33_333
    assert volatile["selected_intro_abs_time_sec"] == 33.333
    ui = state["ui_dynamics"]
    assert ui["mobile_checked"] is True
    assert ui["teammates_checked"] is True
    assert ui["boss_hp_checked"] is True
    assert ui["granular_checked"] is True
    assert ui["no_fade_checked"] is True
    assert ui["portrait_text"] == "Recovery overlay text"
    assert ui["music_button_active"] is True
    assert ui["slider_value_ms"] == 22_222
    restore_host, commands, sets, saved_after_restore = _make_restore_host(state)
    monkeypatch.setenv("FVS_RESTORE_SESSION", "1")
    FortniteVideoSoftware._restore_recovery_state(restore_host)
    assert restore_host.input_file_path == str(video)
    assert restore_host.trim_start_ms == 10_000
    assert restore_host.trim_end_ms == 50_000
    assert restore_host._wizard_tracks == [(str(music_a), 7.25, 12.0), (str(music_b), 2.0, 8.5)]
    assert restore_host._current_music_path == str(music_a)
    assert restore_host._current_music_offset == 7.25
    assert restore_host.music_timeline_start_ms == 18_000
    assert restore_host.music_timeline_end_ms == 38_500
    assert restore_host.positionSlider.trimmed_start_ms == 10_000
    assert restore_host.positionSlider.trimmed_end_ms == 50_000
    assert restore_host.positionSlider.music_start_ms == 18_000
    assert restore_host.positionSlider.music_end_ms == 38_500
    assert restore_host.positionSlider.value() == 22_222
    assert restore_host.positionSlider.thumbnail_pos_ms == 33_333
    assert restore_host.positionSlider.speed_segments == restore_host.speed_segments
    assert any(seg["speed"] == 0.0 and seg["start"] == 12_000 for seg in restore_host.speed_segments)
    assert restore_host.quality_slider.value() == 17
    assert restore_host.mobile_checkbox.isChecked() is True
    assert restore_host.teammates_checkbox.isChecked() is True
    assert restore_host.boss_hp_checkbox.isChecked() is True
    assert restore_host.no_fade_checkbox.isChecked() is True
    assert restore_host.portrait_text_input.text() == "Recovery overlay text"
    assert "MUSIC ADDED" in restore_host.music_button.text()
    assert commands[0][0][:3] == ("loadfile", str(music_a), "replace")
    assert any(call[0][:2] == ("volume", 73) for call in sets)
    assert saved_after_restore

def test_recovery_captures_and_restores_non_portrait_without_teammate_health(tmp_path, monkeypatch) -> None:
    capture_host, recovery, _, _, _ = _make_day_to_day_host(tmp_path, mobile=False, teammates=False)
    FortniteVideoSoftware._save_recovery_state(capture_host)
    state = recovery.saved[-1]
    assert state["ui_dynamics"]["mobile_checked"] is False
    assert state["ui_dynamics"]["teammates_checked"] is False
    restore_host, _, _, _ = _make_restore_host(state)
    monkeypatch.setenv("FVS_RESTORE_SESSION", "1")
    FortniteVideoSoftware._restore_recovery_state(restore_host)
    assert restore_host.mobile_checkbox.isChecked() is False
    assert restore_host.teammates_checkbox.isChecked() is False
    assert restore_host.positionSlider.music_start_ms == 18_000
    assert restore_host.positionSlider.music_end_ms == 38_500

def test_recovery_schema_migration_requires_explicit_restore_and_normalizes_old_fields(tmp_path, monkeypatch) -> None:
    video = tmp_path / "legacy.mp4"
    music = tmp_path / "legacy.mp3"
    video.write_bytes(b"video")
    music.write_bytes(b"music")
    legacy_state = {
        "assets": {
            "input_file_path": str(video),
            "wizard_tracks": [{"path": str(music), "start_ms": 2500, "duration": 6.0}],
            "current_music_path": str(music),
        },
        "volatile_settings": {
            "trim_start_ms": 1000,
            "trim_end_ms": 9000,
            "playback_rate": 1.5,
            "speed_segments": [{"start": 2000, "end": 5000, "multiplier": 4.0}],
            "music_timeline_start_ms": 3000,
            "music_timeline_end_ms": 9000,
            "quality_slider_index": 4,
        },
        "ui_dynamics": {"mobile_checked": True, "teammates_checked": False, "granular_checked": True},
    }
    restore_host, _, _, _ = _make_restore_host(legacy_state)
    monkeypatch.delenv("FVS_RESTORE_SESSION", raising=False)
    FortniteVideoSoftware._restore_recovery_state(restore_host)
    assert restore_host.input_file_path is None
    assert not hasattr(restore_host, "_wizard_tracks")
    monkeypatch.setenv("FVS_RESTORE_SESSION", "1")
    FortniteVideoSoftware._restore_recovery_state(restore_host)
    assert restore_host.input_file_path == str(video)
    assert restore_host._wizard_tracks == [(str(music), 2.5, 6.0)]
    assert restore_host.speed_segments == [{"start": 2000, "end": 5000, "start_ms": 2000, "end_ms": 5000, "speed": 4.0}]
    assert restore_host.quality_slider.value() == 4
    assert restore_host.mobile_checkbox.isChecked() is True
    assert restore_host.teammates_checkbox.isChecked() is False
