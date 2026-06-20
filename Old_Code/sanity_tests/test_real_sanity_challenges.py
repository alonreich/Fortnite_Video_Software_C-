from __future__ import annotations
import tempfile
import types
from pathlib import Path
from sanity_tests._real_sanity_harness import (
    DummyCheckBox,
    DummyConfigManager,
    DummyMediaPlayer,
    DummySpinBox,
    install_qt_mpv_stubs,
)
install_qt_mpv_stubs()

from processing.filter_builder import FilterBuilder
from processing.media_utils import MediaProber, calculate_video_bitrate
from system.config import ConfigManager
from ui.parts.music_mixin import MusicMixin
from ui.parts.player_mixin import PlayerMixin
from ui.widgets import music_wizard_workers as workers

def test_challenge_01_granular_speed_wall_clock_math() -> None:
    host = types.SimpleNamespace()

    import threading
    host._mpv_lock = threading.RLock()
    host._safe_mpv_get = lambda p, d=None, **k: d
    host._safe_mpv_set = lambda p, v, target_player=None, **k: setattr(target_player or host.player, p, v) if hasattr(target_player or host.player, p) else True
    segments = [
        {"start": 0, "end": 1000, "speed": 0.5},
        {"start": 1000, "end": 2000, "speed": 2.0},
        {"start": 2000, "end": 3000, "speed": 1.1},
    ]
    wall = PlayerMixin._calculate_wall_clock_time(host, 2500, segments, 1.0)
    assert abs(wall - 2954.54) < 3.0

def test_challenge_02_impossible_fade_tiny_clip_safe_chain() -> None:
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
    joined = "\n".join(str(c) for c in chain)
    assert "duration=0.100" in joined or "duration=0.1" in joined
    assert "afade=t=in" not in joined

def test_challenge_03_dj_scrubbing_stress_keeps_throttle(monkeypatch) -> None:
    host = types.SimpleNamespace()

    import threading
    host._mpv_lock = threading.RLock()
    host._safe_mpv_get = lambda p, d=None, **k: d
    host._safe_mpv_set = lambda p, v, target_player=None, **k: setattr(target_player or host.player, p, v) if hasattr(target_player or host.player, p) else True
    host.player = DummyMediaPlayer(playing=True, current_ms=0, rate=2.0)
    host.mpv_music_player = DummyMediaPlayer(playing=True, current_ms=0, rate=1.0)
    host.music_timeline_start_ms = 0
    host.music_timeline_end_ms = 10_000
    host.wants_to_play = True
    host.speed_spinbox = DummySpinBox(2.0)
    host.granular_checkbox = DummyCheckBox(False)
    host.speed_segments = []
    host._wizard_tracks = [("song.mp3", 0.0, 10.0)]
    host._get_music_offset_ms = lambda: 0
    host.logger = types.SimpleNamespace(error=lambda *a, **k: None)
    now = {"v": 0.0}
    monkeypatch.setattr("time.time", lambda: now["v"])
    for i in range(15):
        now["v"] = i * 0.02
        PlayerMixin.set_player_position(host, i * 100, sync_only=True)
    assert len(host.mpv_music_player.set_time_calls) < 15

def test_challenge_04_network_disconnect_fallback_to_local_mp3(tmp_path: Path) -> None:
    missing = tmp_path / "missing_network_drive"
    host = types.SimpleNamespace(base_dir=str(tmp_path), config_manager=DummyConfigManager(config={"custom_mp3_dir": str(missing)}))
    chosen = MusicMixin._mp3_dir(host)
    assert chosen.endswith("mp3")
    assert Path(chosen).exists()

def test_challenge_06_worker_race_stop_kills_process_tree(monkeypatch) -> None:
    calls: list[list[str]] = []

    class Proc:
        pid = 999

        def poll(self):
            return None

        def kill(self):
            calls.append(["kill"]) 
    monkeypatch.setattr(workers.subprocess, "run", lambda cmd, **kwargs: calls.append(cmd))
    w = workers.SingleWaveformWorker("x.mp3", tempfile.gettempdir())
    w._proc = Proc()
    w.stop()
    assert any("taskkill" in c[0] for c in calls if c)

def test_challenge_07_bitrate_exhaustion_clamps_to_safe_minimum(tmp_path: Path) -> None:
    tiny = tmp_path / "tiny.mp4"
    tiny.write_bytes(b"1234")
    kbps = calculate_video_bitrate(str(tiny), duration=1.0, audio_kbps=320, target_mb=0.01, keep_highest_res=False)
    assert kbps == 300

def test_challenge_08_ultra_wide_scaling_mobile_filter_has_center_crop_pad() -> None:
    fb = FilterBuilder(logger=types.SimpleNamespace(info=lambda *a, **k: None))
    cfg = {
        "crops_1080p": {"normal_hp": [100, 30, 10, 10], "loot": [80, 20, 20, 20], "stats": [60, 20, 30, 30], "spectating": [70, 20, 40, 40]},
        "scales": {"normal_hp": 1.0, "loot": 1.0, "stats": 1.0, "spectating": 1.0},
        "overlays": {"normal_hp": {"x": 10, "y": 200}, "loot": {"x": 20, "y": 260}, "stats": {"x": 30, "y": 320}, "spectating": {"x": 40, "y": 380}},
        "z_orders": {"normal_hp": 10, "loot": 20, "stats": 30, "spectating": 40},
    }
    cmd = fb.build_mobile_filter(cfg, "2560x1080", is_boss_hp=False, show_teammates=False, use_nvidia=False)
    assert "force_original_aspect_ratio=increase" in cmd
    assert "crop=1280:1920" in cmd
    assert "pad=1080:1920" in cmd

def test_challenge_09_constant_pitch_music_rate_stays_1x_even_at_3_1x() -> None:
    import threading
    host = types.SimpleNamespace()

    import threading
    host._mpv_lock = threading.RLock()
    host._safe_mpv_get = lambda p, d=None, **k: d
    host._safe_mpv_set = lambda p, v, target_player=None, **k: setattr(target_player or host.player, p, v) if hasattr(target_player or host.player, p) else True
    host.player = DummyMediaPlayer(playing=True, current_ms=0, rate=3.1)
    host.mpv_music_player = DummyMediaPlayer(playing=True, current_ms=0, rate=0.5)
    host._music_preview_player = host.mpv_music_player
    host.music_timeline_start_ms = 0
    host.music_timeline_end_ms = 5000
    host.wants_to_play = True
    host.speed_spinbox = DummySpinBox(3.1)
    host.granular_checkbox = DummyCheckBox(False)
    host.speed_segments = []
    host._wizard_tracks = [("song.mp3", 0.0, 5.0)]
    host._get_music_offset_ms = lambda: 0

    from sanity_tests._real_sanity_harness import DummyLogger
    host.logger = DummyLogger()
    host._music_eff = lambda: 80
    host._music_preview_player = host.mpv_music_player
    host._last_scrub_ts = 0.0
    host._scrub_lock = threading.RLock()
    PlayerMixin.set_player_position(host, 2000, sync_only=True)
    assert 1.0 in host.mpv_music_player.set_rate_calls

def test_challenge_10_multi_instance_config_refresh_without_restart(tmp_path: Path) -> None:
    conf = tmp_path / "main_app.conf"
    app_a = ConfigManager(str(conf))
    app_b = ConfigManager(str(conf))
    app_a.save_config({"custom_mp3_dir": "D:/music_a"})
    assert app_b.load_config().get("custom_mp3_dir") == "D:/music_a"
    app_b.save_config({"custom_mp3_dir": "E:/music_b"})
    assert app_a.load_config().get("custom_mp3_dir") == "E:/music_b"

def test_challenge_11_probe_does_not_count_full_video_before_render(monkeypatch, tmp_path: Path) -> None:
    calls = []

    class Result:
        stdout = '{"streams":[{"avg_frame_rate":"120/1","r_frame_rate":"120/1","nb_frames":"10224","duration":"85.2"}],"format":{"duration":"85.2"}}'

    def fake_run(cmd, **kwargs):
        calls.append((cmd, kwargs))
        return Result()
    monkeypatch.setattr("processing.media_utils.subprocess.run", fake_run)
    prober = MediaProber(str(tmp_path), "video.mp4")
    info = prober.get_video_timing_info()
    assert info["observed_fps"] == 120.0
    assert calls
    assert all("-count_frames" not in cmd for cmd, _ in calls)
    assert all(kwargs.get("timeout") == prober.probe_timeout for _, kwargs in calls)
