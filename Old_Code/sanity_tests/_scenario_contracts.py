from __future__ import annotations
import json
import os
import types
import pytest
from sanity_tests._ai_sanity_helpers import assert_all_present, read_source
from sanity_tests._real_sanity_harness import (
    DummyButton,
    DummyCheckBox,
    DummyConfigManager,
    DummyLogger,
    DummyMediaPlayer,
    DummySignal,
    DummySlider,
    DummySpinBox,
    DummyTimer,
    install_qt_mpv_stubs,
)
install_qt_mpv_stubs()

def assert_source_contract(rel_path: str, snippets: list[str]) -> None:
    assert_all_present(read_source(rel_path), snippets)

def assert_extreme_speed_changes() -> None:
    from ui.parts.player_mixin import PlayerMixin
    host = types.SimpleNamespace()
    segments = [
        {"start": 0, "end": 1000, "speed": 0.2},
        {"start": 1000, "end": 2000, "speed": 8.0},
    ]
    wall_1 = PlayerMixin._calculate_wall_clock_time(host, 1000, segments, 1.0)
    wall_2 = PlayerMixin._calculate_wall_clock_time(host, 2000, segments, 1.0)
    assert wall_1 == pytest.approx(5000.0)
    assert wall_2 == pytest.approx(5125.0)
    assert wall_2 > wall_1

def assert_high_speed_music_sync() -> None:
    from ui.parts.player_mixin import PlayerMixin
    host = types.SimpleNamespace()
    host.player = DummyMediaPlayer(playing=True, current_ms=0, rate=3.1)
    host.mpv_music_player = DummyMediaPlayer(playing=True, current_ms=0, rate=0.25)
    host._music_preview_player = host.mpv_music_player
    host._wizard_tracks = [("song.mp3", 0.0, 5.0)]
    host.music_timeline_start_ms = 0
    host.music_timeline_end_ms = 5000
    host.wants_to_play = True
    host.speed_spinbox = DummySpinBox(3.1)
    host.granular_checkbox = DummyCheckBox(False)
    host.speed_segments = []
    host.positionSlider = DummySlider()
    host._mpv_lock = __import__("threading").RLock()
    host._scrub_lock = __import__("threading").RLock()
    host._safe_mpv_get = lambda prop, default=None, target_player=None, **_k: getattr(target_player or host.player, prop.replace("-", "_"), default)
    host._safe_mpv_set = lambda prop, value, target_player=None, **_k: setattr(target_player or host.player, prop.replace("-", "_"), value) or True
    host._get_music_offset_ms = lambda: 0
    host._calculate_wall_clock_time = types.MethodType(PlayerMixin._calculate_wall_clock_time, host)
    PlayerMixin.set_player_position(host, 3000, sync_only=True)
    assert host.mpv_music_player.speed == pytest.approx(1.0)

def assert_wizard_handoff_syncs_to_main() -> None:
    from PyQt5.QtWidgets import QDialog
    from ui.parts.music_mixin import MusicMixin
    commands: list[tuple] = []
    sets: list[tuple] = []
    host = types.SimpleNamespace(
        music_button=DummyButton(),
        positionSlider=DummySlider(),
        player=DummyMediaPlayer(),
        _music_preview_player=DummyMediaPlayer(),
        timer=DummyTimer(False),
        _mpv_lock=__import__("threading").RLock(),
        wants_to_play=True,
        logger=DummyLogger(),
    )
    host._ensure_music_player_ready = lambda: True
    host._safe_mpv_command = lambda *args, **kwargs: commands.append((args, kwargs)) or True
    host._safe_mpv_set = lambda *args, **kwargs: sets.append((args, kwargs)) or True
    host._sync_music_preview = lambda: None
    host._safe_seek_to_start = lambda _start: None
    host._save_recovery_state = lambda: None
    host._set_music_button_state = types.MethodType(MusicMixin._set_music_button_state, host)
    host.raise_ = lambda: None
    host.activateWindow = lambda: None
    host.video_surface = types.SimpleNamespace(show=lambda: None)
    host._bind_main_player_output = lambda: None
    wizard = types.SimpleNamespace(
        selected_tracks=[("song.mp3", 1.25, 6.5)],
        music_vol_slider=DummySpinBox(77),
        video_vol_slider=DummySpinBox(88),
    )
    MusicMixin._continue_wizard_return(host, QDialog.Accepted, 2000, 8500, [], 1.0, wizard)
    assert host._wizard_tracks == [("song.mp3", 1.25, 6.5)]
    assert host.music_timeline_start_ms == 2000
    assert host.music_timeline_end_ms == 8500
    assert host.positionSlider.music_start_ms == 2000
    assert host.positionSlider.music_end_ms == 8500
    assert "MUSIC ADDED" in host.music_button.text()
    assert commands and commands[0][0][:2] == ("loadfile", "song.mp3")

def assert_directory_persistence(tmp_path) -> None:
    from ui.parts.music_mixin import MusicMixin
    custom = tmp_path / "music"
    custom.mkdir()
    host = types.SimpleNamespace(
        base_dir=str(tmp_path),
        config_manager=DummyConfigManager({"custom_mp3_dir": str(custom)}),
    )
    assert MusicMixin._mp3_dir(host) == str(custom)

def assert_trim_out_of_bounds_rescue() -> None:
    from ui.parts.trim_mixin import TrimMixin

    class Host(TrimMixin):
        def __init__(self):
            self.original_duration_ms = 5000
            self.trim_start_ms = 1000
            self.trim_end_ms = 4000
            self.positionSlider = DummySlider(7000)
            self.MIN_TRIM_GAP = 1000

        def _update_trim_widgets_from_trim_times(self): pass
    host = Host()
    host.set_end_time()
    assert host.trim_end_ms == 5000
    assert host.trim_end_ms - host.trim_start_ms >= host.MIN_TRIM_GAP

def assert_duplicate_file_protection(tmp_path) -> None:
    from utilities.workers import FastFileLoaderWorker
    a = tmp_path / "a.mp4"
    b = tmp_path / "b.mp4"
    payload = b"x" * (1024 * 1024 + 1)
    a.write_bytes(payload)
    b.write_bytes(payload)
    worker = FastFileLoaderWorker([], [], set(), 10, "ffprobe")
    assert worker._calculate_partial_hash(str(a)) == worker._calculate_partial_hash(str(b))

def assert_unicode_video_loading_contract() -> None:
    assert_source_contract(
        "utilities/workers.py",
        ["encoding='utf-8'", "errors='replace'", "json.loads(r.stdout)"],
    )

def assert_audio_ducking_preview_contract() -> None:
    from utilities.merger_utils import build_audio_ducking_filters
    filters = build_audio_ducking_filters("[0:a]", "[mus]", music_volume=0.7, video_has_audio=True, duration=5.0)
    joined = ";".join(filters)
    assert "sidechaincompress" in joined
    assert "[a_out]" in joined

def assert_intro_overlay_toggle_contract() -> None:
    assert_source_contract(
        "ui/parts/ffmpeg_mixin.py",
        ["intro_still_sec=0.1", "intro_abs_time_ms", "selected_intro_abs_time"],
    )

def assert_mobile_view_switch_contract() -> None:
    assert_source_contract(
        "ui/parts/ui_builder_mixin.py",
        ["def _on_mobile_toggled", "setVisible(checked)", "_update_quality_label()"],
    )

def assert_hw_scan_timeout_contract() -> None:
    assert_source_contract(
        "processing/media_utils.py",
        ["timeout=5.0", 'hardware_scan_details["timed_out"].append(encoder_name)', "return False"],
    )

def assert_empty_state_safety_contract() -> None:
    assert_source_contract(
        "ui/parts/ui_builder_mixin.py",
        ["def _set_video_controls_enabled", "w.setEnabled(enabled)", "self.quality_slider", "self.process_button.setEnabled(False)"],
    )

def assert_main_config_self_heal(tmp_path) -> None:
    from system.config import ConfigManager
    cfg = tmp_path / "main_app.conf"
    cfg.write_text("{bad json", encoding="utf-8")
    manager = ConfigManager(str(cfg))
    assert isinstance(manager.config, dict)
    assert cfg.exists()

def assert_wizard_cancel_cleanup_contract() -> None:
    assert_source_contract(
        "ui/widgets/music_wizard.py",
        ["def reject(self):", "self.stop_previews(); self._release_player(); super().reject()", "def stop_previews(self):"],
    )

def assert_mpv_missing_recovery_contract() -> None:
    assert_source_contract(
        "ui/main_window.py",
        ["mpv_ready=True", "Preview disabled:", "_mpv_error_hint"],
    )

def assert_bitrate_clamping() -> None:
    from processing.media_utils import calculate_video_bitrate
    low = calculate_video_bitrate("x.mp4", duration=60, audio_kbps=320, target_mb=1, keep_highest_res=False)
    high = calculate_video_bitrate("x.mp4", duration=1, audio_kbps=128, target_mb=500, keep_highest_res=False)
    assert low == 300
    assert high == 50000

def assert_handoff_seek_spam_contract() -> None:
    assert_source_contract(
        "ui/parts/player_mixin.py",
        ["_pending_seek_ms", "_seek_timer", "if not self._seek_timer.isActive()"],
    )

def assert_merger_mixed_audio_contract() -> None:
    assert_source_contract(
        "utilities/merger_window.py",
        ["audio_mixed = has_audio_input and (not all_have_audio)", "anullsrc=channel_layout=stereo", "concat=n={len(video_files)}:v=0:a=1[a_serial]"],
    )

def assert_merger_no_music_loop_contract() -> None:
    assert_source_contract(
        "utilities/merger_window.py",
        ["music_unique_total < total_video", "If you continue, the rest of the video will be quiet.", "atrim=duration={max(0.1, float(total_duration))}[mus]"],
    )

def assert_merger_disk_space_contract() -> None:
    assert_source_contract(
        "utilities/merger_window.py",
        ["get_disk_free_space", "_estimate_required_output_bytes", "Critically Low Disk Space"],
    )

def assert_merger_rapid_drag_drop_contract() -> None:
    assert_source_contract(
        "utilities/workers.py",
        ["class FastFileLoaderWorker", "if added >= room", "self.finished.emit(added, duplicates)"],
    )

def assert_merger_cancel_mid_merge_contract() -> None:
    assert_source_contract(
        "utilities/merger_window.py",
        ["def cancel_processing", "self.engine.cancel()", "_ensure_cancel_cleanup"],
    )

def assert_merger_multi_track_crossfade_contract() -> None:
    assert_source_contract(
        "utilities/merger_window.py",
        ["crossfade_sec = 3.0", "acrossfade=d={crossfade_sec}", "music_inputs"],
    )

def assert_merger_high_dpi_geometry_contract() -> None:
    assert_source_contract(
        "utilities/merger_window_logic.py",
        ["restoreGeometry(QByteArray.fromBase64", "saveGeometry().toBase64()", "qt_geometry"],
    )

def assert_merger_unicode_paths_contract() -> None:
    from utilities.merger_utils import escape_ffmpeg_path
    escaped = escape_ffmpeg_path(r"C:\clips\weird ' name\שלום.mp4")
    assert "/clips/" in escaped
    assert r"C:\clips" not in escaped
    assert "שלום.mp4" in escaped
    assert "'\\''" in escaped

def assert_merger_audio_ducking_contract() -> None:
    assert_source_contract(
        "utilities/merger_window.py",
        ["build_audio_ducking_filters(", "music_volume=1.0", "video_has_audio=has_audio_input"],
    )

def assert_merger_batch_remove_loading_contract() -> None:
    assert_source_contract(
        "utilities/merger_window.py",
        ['getattr(self.event_handler, "_loading_lock", False)', "Still loading files", "set_processing_state(False)"],
    )

def assert_merger_vfr_sync_contract() -> None:
    assert_source_contract(
        "utilities/workers.py",
        ["r_frame_rate", "video_fps", "pix_fmt"],
    )

def assert_merger_output_conflict_contract() -> None:
    assert_source_contract(
        "utilities/merger_window.py",
        ["def _get_next_output_path", "Merged-Videos", "exists()"],
    )

def assert_merger_taskbar_progress_contract() -> None:
    assert_source_contract(
        "utilities/merger_window.py",
        ["def _update_progress", "self._overlay_progress_bar.setValue(p)", "Merging: {percent}%"],
    )

def assert_merger_empty_list_state_contract() -> None:
    assert_source_contract(
        "utilities/merger_window.py",
        ["if n < 1:", "Please add at least 1 video to merge.", "self.set_processing_state(False)"],
    )

def assert_merger_config_corruption_recovery(tmp_path) -> None:
    from utilities.merger_config import MergerConfigManager
    cfg = tmp_path / "video_merger.conf"
    cfg.write_text("{bad json", encoding="utf-8")
    manager = MergerConfigManager(str(cfg))
    assert manager.config == {}

def assert_merger_ultra_long_duration_contract() -> None:
    assert_source_contract(
        "utilities/merger_engine.py",
        ["self.total_duration = max(1.0, float(total_duration_sec))", "_last_time_str", "out_time_us"],
    )

def assert_merger_hardware_encoder_stress_contract() -> None:
    from utilities.merger_engine import MergerEngine
    engine = MergerEngine("ffmpeg", [], "out.mp4", use_gpu=True, target_v_bitrate=250_000_000, quality_level=4)
    args = engine._video_bitrate_args(1.0)
    assert args[args.index("-b:v") + 1] == "50000000"
    assert args[args.index("-maxrate:v") + 1] == "50000000"

def assert_merger_wizard_seek_spam_contract() -> None:
    assert_source_contract(
        "utilities/merger_music_wizard_timeline.py",
        ["_pending_step3_seek", "_step3_seek_timer", "_flush_pending_step3_seek"],
    )

def assert_merger_quality_file_sizes_contract() -> None:
    from utilities.merger_engine import MergerEngine
    low = MergerEngine("ffmpeg", [], "out.mp4", use_gpu=True, target_v_bitrate=50_000_000, quality_level=0)
    high = MergerEngine("ffmpeg", [], "out.mp4", use_gpu=True, target_v_bitrate=50_000_000, quality_level=4)
    assert low._video_bitrate_args(0.20)[1] == "10000000"
    assert high._video_bitrate_args(1.0)[1] == "50000000"
