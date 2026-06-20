from __future__ import annotations
from pathlib import Path
import types
from sanity_tests._real_sanity_harness import install_qt_mpv_stubs
install_qt_mpv_stubs()

from processing.filter_builder import FilterBuilder
from processing.worker import ProcessThread

class _Sig:
    def emit(self, *args, **kwargs) -> None:
        return None

def _logger() -> object:
    return types.SimpleNamespace(
        info=lambda *a, **k: None,
        warning=lambda *a, **k: None,
        error=lambda *a, **k: None,
        exception=lambda *a, **k: None,
        critical=lambda *a, **k: None,
    )

def test_break_scenario_overlapping_unsorted_segments_are_stable() -> None:
    fb = FilterBuilder(logger=_logger())
    chain, _v, _a, final_dur, tmap = fb.build_granular_speed_chain(
        video_path="dummy.mp4",
        duration_ms=10_000,
        speed_segments=[
            {"start": 4000, "end": 7000, "speed": 0.6},
            {"start": 1000, "end": 4000, "speed": 2.0},
            {"start": 7000, "end": 9000, "speed": 0.6},
            {"start": 2000, "end": 3000, "speed": 2.0},
        ],
        base_speed=1.3,
        source_cut_start_ms=0,
    )
    assert "concat=n=" in chain
    assert "setpts='PTS/" in chain
    assert "fps=" not in chain
    assert final_dur > 0.0
    assert tmap(0.0) >= 0.0
    assert tmap(3.0) >= tmap(1.0)
    assert tmap(8.0) >= tmap(3.0)

def test_break_scenario_trim_near_zero_never_generates_negative_seek(monkeypatch, tmp_path: Path) -> None:
    captured_cmds: list[list[str]] = []

    class _Proc:
        pid = 777
        returncode = 0

        def wait(self, timeout=None):
            return 0

    def _fake_create_subprocess(cmd, _logger=None):
        captured_cmds.append(list(cmd))
        Path(cmd[-1]).write_bytes(b"ok")
        return _Proc()
    monkeypatch.setattr("processing.worker.create_subprocess", _fake_create_subprocess)
    monkeypatch.setattr("processing.worker.monitor_ffmpeg_progress", lambda *a, **k: None)
    monkeypatch.setattr("processing.worker.check_disk_space", lambda *a, **k: True)
    monkeypatch.setattr("processing.worker.calculate_video_bitrate", lambda *a, **k: 1200)
    monkeypatch.setattr("processing.worker.MediaProber.get_audio_bitrate", lambda self: 128)
    monkeypatch.setattr("processing.worker.MediaProber.get_sample_rate", lambda self: 48000)
    monkeypatch.setattr("processing.worker.MediaProber.get_video_timing_info", lambda self: {"is_vfr": False, "observed_fps": 60.0})
    monkeypatch.setattr("processing.worker.MediaProber.get_video_fps_expr", lambda self, fallback="60": "60")
    monkeypatch.setattr("processing.worker.ProcessThread._validate_render_output", lambda *a, **k: (True, "OK"))
    monkeypatch.setattr("processing.worker.ProcessThread._target_size_bounds", lambda self: None)
    out_file = tmp_path / "ok.mp4"
    out_file.write_bytes(b"ok")
    thr = ProcessThread(
        input_path=str(out_file),
        start_time_ms=200,
        end_time_ms=2800,
        original_resolution="1920x1080",
        is_mobile_format=False,
        speed_factor=1.1,
        script_dir=str(tmp_path),
        progress_update_signal=_Sig(),
        status_update_signal=_Sig(),
        finished_signal=_Sig(),
        logger=_logger(),
        disable_fades=False,
        original_total_duration_ms=12_000,
        intro_still_sec=0.0,
    )
    thr.run()
    assert captured_cmds
    cmd = captured_cmds[0]
    ss_idx = cmd.index("-ss")
    assert cmd[ss_idx + 1] == "0.200" or cmd[ss_idx + 1] == "0.000"
    assert not cmd[ss_idx + 1].startswith("-")
