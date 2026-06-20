from __future__ import annotations
import os
import types
from pathlib import Path
from sanity_tests._real_sanity_harness import install_qt_mpv_stubs, DummyLogger
install_qt_mpv_stubs()

from ui.widgets.trimmed_slider import TrimmedSlider
from processing.filter_builder import FilterBuilder
from ui.parts.main_window_core_c import MainWindowCoreCMixin
from ui.parts.music_mixin import MusicMixin

def _music_host():
    host = types.SimpleNamespace()
    host.logger = DummyLogger()
    host.positionSlider = TrimmedSlider()
    host.positionSlider.set_duration_ms(20000)
    host.positionSlider.setRange(0, 20000)
    host.trim_start_ms = 0
    host.trim_end_ms = 15000
    host.music_timeline_start_ms = 0
    host.music_timeline_end_ms = 0
    host._wizard_tracks = []
    host._update_trim_widgets_from_trim_times = lambda: None
    host._update_quality_label = lambda: None
    host._save_recovery_state = lambda: None
    host._set_music_button_state = lambda has_music: setattr(host, "_has_music_button", bool(has_music))
    return host

def test_music_state_wiped_on_fresh_startup_or_new_file(monkeypatch, tmp_path):
    """
    Test 1: Check if the app had background music in the last processing,
    then the app reopened (or a fresh file loaded), that the music state is completely wiped fresh.
    """
    main_app = _music_host()
    main_app._wizard_tracks = [("some_music.mp3", 0.0, 5.0)]
    main_app.music_timeline_start_ms = 2000
    main_app.music_timeline_end_ms = 7000
    main_app.positionSlider.set_music_times(2000, 7000)
    main_app.original_duration_ms = 15000
    MainWindowCoreCMixin._safe_handle_duration_changed(main_app, 15000)
    MusicMixin._reset_music_player(main_app)
    MainWindowCoreCMixin._on_slider_trim_changed(main_app, 0, 15000)
    assert main_app.music_timeline_start_ms == 0, "Music start should be wiped to 0 on fresh state"
    assert main_app.music_timeline_end_ms == 0, "Music end should be wiped to 0 on fresh state"
    assert main_app.positionSlider.music_start_ms == -1 or main_app.positionSlider.music_start_ms == 0, "Slider music start should reset"
    assert not main_app.positionSlider._show_music, "Pink music line should not be visible"

def test_video_trim_pushes_music_boundaries_and_ffmpeg_output():
    """
    Test 2 & 4: If trim start is at 7s and is pushed to 10s, music start should jump to 10s.
    Same logic for the end.
    Also verifies FFmpeg final output file and preview player constraints.
    """
    main_app = _music_host()
    main_app.original_duration_ms = 20000
    main_app.trim_start_ms = 7000
    main_app.trim_end_ms = 15000
    main_app._wizard_tracks = [("track.mp3", 0.0, 8.0)]
    main_app.music_timeline_start_ms = 7000
    main_app.music_timeline_end_ms = 15000
    main_app.positionSlider.set_trim_times(10000, 15000)
    main_app.trim_start_ms = 10000
    MainWindowCoreCMixin._on_slider_trim_changed(main_app, 10000, 15000)
    assert main_app.music_timeline_start_ms == 10000, "Music start must jump to the new trim start"
    assert main_app.music_timeline_end_ms == 15000
    assert main_app._wizard_tracks[0][2] == 5.0, "Music duration must be truncated to 5s"
    assert main_app.positionSlider.music_start_ms == 10000, "Pink note start must jump to 10s visually"
    fb = FilterBuilder(logger=DummyLogger())
    music_tracks = [("track.mp3", main_app._wizard_tracks[0][1], main_app._wizard_tracks[0][2])]
    music_config = {"timeline_start_sec": 0.0, "timeline_end_sec": 5.0, "file_offset_sec": 0.0}
    chain, out_label = fb.build_audio_chain(
        music_config=music_config,
        video_start_time=10000,
        video_end_time=15000,
        speed_factor=1.0,
        disable_fades=True,
        vfade_in_d=0,
        audio_filter_cmd="anull",
        music_tracks=music_tracks
    )
    chain_str = ";".join(chain)
    assert "atrim=start=0.000:duration=5.000" in chain_str
    assert "adelay" not in chain_str or "adelay=0" in chain_str

def test_music_dragged_independently_bounds_and_ffmpeg_output():
    """
    Test 3 & 4: If trim start is at 7s and user drags music start to 10s,
    the music should start at 10s. Verifies preview constraints and FFmpeg output.
    """
    slider = TrimmedSlider()
    slider.set_duration_ms(20000)
    slider.setRange(0, 20000)
    slider.set_trim_times(7000, 15000)
    slider.set_music_visible(True)
    slider.set_music_times(10000, 14000)
    assert slider.trimmed_start_ms == 7000
    assert slider.music_start_ms == 10000
    assert slider.music_end_ms == 14000
    slider._dragging_handle = 'end'
    slider._has_moved_since_press = True

    class MockEvent:
        def pos(self): return types.SimpleNamespace(x=lambda: 0)

        def x(self): return 0
    slider._map_pos_to_value = lambda x: 12000
    slider.mouseMoveEvent(MockEvent())
    assert slider.trimmed_end_ms == 12000
    assert slider.music_end_ms == 12000
    fb = FilterBuilder(logger=DummyLogger())
    music_tracks = [("track.mp3", 0.0, 2.0)]
    music_config = {"timeline_start_sec": 3.0, "timeline_end_sec": 5.0, "file_offset_sec": 0.0}
    chain, out_label = fb.build_audio_chain(
        music_config=music_config,
        video_start_time=7000,
        video_end_time=12000,
        speed_factor=1.0,
        disable_fades=True,
        vfade_in_d=0,
        audio_filter_cmd="anull",
        music_tracks=music_tracks
    )
    chain_str = ";".join(chain)
    assert "atrim=start=0.000:duration=2.000" in chain_str
    assert "adelay=3000" in chain_str, "FFmpeg chain must delay the music by exactly the relative offset (3s)"

