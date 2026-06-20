import sys
from types import SimpleNamespace
import pytest
sys.dont_write_bytecode = True

from sanity_tests._real_sanity_harness import install_qt_mpv_stubs
install_qt_mpv_stubs()

from utilities import merger_engine
from utilities.merger_engine import MergerEngine

def test_merger_nvenc_bitrate_obeys_level_42(monkeypatch):
    monkeypatch.setattr(
        merger_engine.subprocess,
        "run",
        lambda *args, **kwargs: SimpleNamespace(stdout=" V....D h264_nvenc NVIDIA NVENC H.264 encoder\n"),
    )
    engine = MergerEngine(
        "ffmpeg",
        [],
        "out.mp4",
        use_gpu=True,
        target_v_bitrate=50_048_536,
        quality_level=4,
    )
    flags = engine._detect_gpu_encoder()
    assert flags[1] == "h264_nvenc"
    assert "libx264" not in flags
    assert flags[flags.index("-b:v") + 1] == "50000000"
    assert flags[flags.index("-maxrate:v") + 1] == "50000000"
    assert flags[flags.index("-bufsize:v") + 1] == "50000000"

def test_merger_gpu_request_without_hardware_encoder_does_not_choose_cpu(monkeypatch):
    monkeypatch.setattr(
        merger_engine.subprocess,
        "run",
        lambda *args, **kwargs: SimpleNamespace(stdout=" V....D libx264 libx264 H.264 encoder\n"),
    )
    engine = MergerEngine("ffmpeg", [], "out.mp4", use_gpu=True, target_v_bitrate=5_000_000)
    with pytest.raises(RuntimeError, match="no H.264 hardware encoder"):
        engine._detect_gpu_encoder()
