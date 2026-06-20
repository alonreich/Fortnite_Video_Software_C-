from __future__ import annotations
from types import SimpleNamespace
import pytest
from sanity_tests._real_sanity_harness import install_qt_mpv_stubs
install_qt_mpv_stubs()

from utilities import merger_engine
from utilities.merger_engine import MergerEngine

def test_merger_encoder_policy_prefers_nvenc_before_other_hardware(monkeypatch) -> None:
    monkeypatch.setattr(
        merger_engine.subprocess,
        "run",
        lambda *args, **kwargs: SimpleNamespace(
            stdout=(
                " V....D h264_qsv Intel QSV H.264 encoder\n"
                " V....D h264_amf AMD AMF H.264 encoder\n"
                " V....D h264_nvenc NVIDIA NVENC H.264 encoder\n"
            )
        ),
    )
    engine = MergerEngine("ffmpeg", [], "out.mp4", use_gpu=True, target_v_bitrate=0, quality_level=4)
    flags = engine._detect_gpu_encoder()
    assert flags[:2] == ["-c:v", "h264_nvenc"]
    assert "-preset" in flags and flags[flags.index("-preset") + 1] == "p4"
    assert "libx264" not in flags

def test_merger_encoder_policy_uses_qsv_when_it_is_only_hardware(monkeypatch) -> None:
    monkeypatch.setattr(
        merger_engine.subprocess,
        "run",
        lambda *args, **kwargs: SimpleNamespace(stdout=" V....D h264_qsv Intel QSV H.264 encoder\n"),
    )
    engine = MergerEngine("ffmpeg", [], "out.mp4", use_gpu=True, target_v_bitrate=0, quality_level=3)
    flags = engine._detect_gpu_encoder()
    assert flags[:2] == ["-c:v", "h264_qsv"]
    assert "-global_quality" in flags
    assert "libx264" not in flags

def test_merger_run_gpu_request_fails_before_cpu_fallback_or_popen(monkeypatch) -> None:
    monkeypatch.setattr(
        merger_engine.subprocess,
        "run",
        lambda *args, **kwargs: SimpleNamespace(stdout=" V....D libx264 libx264 H.264 encoder\n"),
    )
    monkeypatch.setattr(
        merger_engine.subprocess,
        "Popen",
        lambda *args, **kwargs: pytest.fail("FFmpeg must not start after GPU policy failure"),
    )
    engine = MergerEngine("ffmpeg", ["-i", "a.mp4"], "out.mp4", use_gpu=True)
    finished = []
    engine.finished.connect(lambda success, msg: finished.append((success, msg)))
    engine.run()
    assert finished
    assert finished[-1][0] is False
    assert "CPU fallback is disabled" in finished[-1][1]
