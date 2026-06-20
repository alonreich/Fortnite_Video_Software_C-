from __future__ import annotations
import types
from sanity_tests._real_sanity_harness import install_qt_mpv_stubs
from sanity_tests._real_sanity_harness import DummyButton as HarnessButton
from sanity_tests._real_sanity_harness import DummyCheckBox, DummyLogger, DummySlider, DummySpinBox
install_qt_mpv_stubs()

from processing.filter_builder import FilterBuilder
from system.recovery_manager import RecoveryManager
from ui.main_window import (
    FortniteVideoSoftware,
    _deserialize_recovery_music_tracks,
    _normalize_recovery_speed_segments,
    _serialize_recovery_music_tracks,
)

from ui.parts.music_mixin import MusicMixin

class DummyButton:
    def __init__(self):
        self.text_value = ""
        self.style_value = ""
        self.tooltip_value = ""

    def setText(self, value):
        self.text_value = value

    def setStyleSheet(self, value):
        self.style_value = value

    def setToolTip(self, value):
        self.tooltip_value = value

def test_recovery_music_tracks_round_trip_as_json_safe_dicts(tmp_path):
    mp3 = tmp_path / "song.mp3"
    mp3.write_bytes(b"fake")
    serialized = _serialize_recovery_music_tracks([(str(mp3), 275.0, 28.7)])
    assert serialized == [{"path": str(mp3), "offset_sec": 275.0, "duration_sec": 28.7}]
    assert _deserialize_recovery_music_tracks(serialized) == [(str(mp3), 275.0, 28.7)]

def test_recovery_keeps_freeze_and_speed_segments():
    segments = _normalize_recovery_speed_segments(
        [
            {"start": 4000, "end": 5000, "speed": 0.0},
            {"start_ms": 1000, "end_ms": 3000, "speed": 2.5},
        ]
    )
    assert segments == [
        {"start": 1000, "end": 3000, "start_ms": 1000, "end_ms": 3000, "speed": 2.5},
        {"start": 4000, "end": 5000, "start_ms": 4000, "end_ms": 5000, "speed": 0.0},
    ]

def test_recovery_manager_validates_dict_and_tuple_music_tracks(tmp_path):
    mp3_a = tmp_path / "a.mp3"
    mp3_b = tmp_path / "b.mp3"
    mp3_a.write_bytes(b"a")
    mp3_b.write_bytes(b"b")
    state = {
        "assets": {
            "wizard_tracks": [
                {"path": str(mp3_a), "offset_sec": 1.0, "duration_sec": 2.0},
                [str(mp3_b), 3.0, 4.0],
            ]
        }
    }
    valid, missing = RecoveryManager("test_recovery_music").validate_assets(state)
    assert valid is True
    assert missing == []

def test_filter_builder_accepts_recovered_dict_music_track():
    fb = FilterBuilder(logger=types.SimpleNamespace(info=lambda *args, **kwargs: None))
    chain, out_label = fb.build_audio_chain(
        music_config={"timeline_start_sec": 0.0, "timeline_end_sec": 28.7, "music_vol": 0.7, "main_vol": 0.8},
        video_start_time=0.0,
        video_end_time=31.5,
        speed_factor=1.1,
        disable_fades=True,
        vfade_in_d=0,
        audio_filter_cmd="anull",
        music_tracks=[{"path": "song.mp3", "offset_sec": 275.0, "duration_sec": 28.7}],
        total_project_duration=28.7,
    )
    joined = ";".join(chain)
    assert out_label == "[a_music_prepared]"
    assert "[1:a]atrim=start=275.000:duration=28.700" in joined
    assert "volume=0.7000" in joined

def test_music_button_reflects_recovered_music_state():
    host = types.SimpleNamespace(music_button=DummyButton())
    MusicMixin._set_music_button_state(host, True)
    assert "MUSIC ADDED" in host.music_button.text_value
    assert "edit the selected music" in host.music_button.tooltip_value
    MusicMixin._set_music_button_state(host, False)
    assert "ADD MUSIC" in host.music_button.text_value

def test_full_recovery_restores_music_trim_quality_thumbnail_and_granular_state(tmp_path, monkeypatch):
    video = tmp_path / "clip.mp4"
    music = tmp_path / "song.mp3"
    video.write_bytes(b"video")
    music.write_bytes(b"music")
    commands: list[tuple] = []
    sets: list[tuple] = []
    saved: list[bool] = []
    state = {
        "assets": {
            "input_file_path": str(video),
            "wizard_tracks": [{"path": str(music), "offset_sec": 3.25, "duration_sec": 8.5}],
            "current_music_path": str(music),
        },
        "volatile_settings": {
            "trim_start_ms": 1234,
            "trim_end_ms": 15678,
            "playback_rate": 2.2,
            "speed_segments": [
                {"start_ms": 1300, "end_ms": 2200, "speed": 0.0},
                {"start": 5000, "end": 8000, "speed": 3.1},
            ],
            "video_mix_volume": 64,
            "music_volume_pct": 71,
            "video_volume_pct": 86,
            "quality_slider_index": 12,
            "current_music_offset": 3.25,
            "music_timeline_start_ms": 2345,
            "music_timeline_end_ms": 10845,
            "thumbnail_pos_ms": 9876,
            "selected_intro_abs_time_sec": 9.876,
            "hardware_strategy": "NVIDIA",
        },
        "ui_dynamics": {
            "mobile_checked": True,
            "teammates_checked": True,
            "boss_hp_checked": True,
            "granular_checked": True,
            "no_fade_checked": True,
            "portrait_text": "Recovered overlay",
            "music_button_active": True,
            "slider_value_ms": 4321,
        },
    }

    class TextInput:
        def __init__(self):
            self.value = ""

        def setText(self, value):
            self.value = str(value)

        def text(self):
            return self.value
    host = types.SimpleNamespace(
        recovery_manager=types.SimpleNamespace(load_state=lambda: state),
        logger=DummyLogger(),
        input_file_path=None,
        original_duration_ms=20000,
        trim_end_ms=20000,
        positionSlider=DummySlider(),
        speed_spinbox=DummySpinBox(1.1),
        volume_slider=DummySlider(),
        quality_slider=DummySlider(),
        mobile_checkbox=DummyCheckBox(False),
        teammates_checkbox=DummyCheckBox(False),
        boss_hp_checkbox=DummyCheckBox(False),
        granular_checkbox=DummyCheckBox(False),
        no_fade_checkbox=DummyCheckBox(False),
        portrait_text_input=TextInput(),
        music_button=HarnessButton(),
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
    host._save_recovery_state = lambda: saved.append(True)
    host._apply_restored_slider_state = types.MethodType(FortniteVideoSoftware._apply_restored_slider_state, host)
    monkeypatch.setenv("FVS_RESTORE_SESSION", "1")
    FortniteVideoSoftware._restore_recovery_state(host)
    assert host.input_file_path == str(video)
    assert host.trim_start_ms == 1234
    assert host.trim_end_ms == 15678
    assert host.speed_spinbox.value() == 2.2
    assert host.quality_slider.value() == 12
    assert host.volume_slider.value() == 64
    assert host._wizard_tracks == [(str(music), 3.25, 8.5)]
    assert host._music_volume_pct == 71
    assert host._video_volume_pct == 86
    assert host.music_timeline_start_ms == 2345
    assert host.music_timeline_end_ms == 10845
    assert host.positionSlider.trimmed_start_ms == 1234
    assert host.positionSlider.trimmed_end_ms == 15678
    assert host.positionSlider.music_start_ms == 2345
    assert host.positionSlider.music_end_ms == 10845
    assert host.positionSlider.value() == 4321
    assert host.positionSlider.thumbnail_pos_ms == 9876
    assert host.positionSlider.speed_segments == host.speed_segments
    assert host.selected_intro_abs_time == 9.876
    assert host.granular_checkbox.isChecked() is True
    assert host.no_fade_checkbox.isChecked() is True
    assert host.portrait_text_input.text() == "Recovered overlay"
    assert "MUSIC ADDED" in host.music_button.text()
    assert commands and commands[0][0][:3] == ("loadfile", str(music), "replace")
    assert any(call[0][:2] == ("volume", 71) for call in sets)
    assert saved
    assert host._restoring_recovery_state is False
