import os
import pytest
import subprocess
import json
import hashlib
from PyQt5.QtWidgets import QApplication
from processing.worker import ProcessThread
from processing.media_utils import MediaProber
from processing.config_data import VideoConfig
from system.logger import setup_logger
import logging

def generate_synthetic_video(base_dir):
    import subprocess
    import tempfile
    import uuid
    tmp = os.path.join(tempfile.gettempdir(), f"synthetic_{uuid.uuid4().hex}.mp4")
    bin_dir = os.path.join(base_dir, 'binaries')
    ffmpeg = os.path.join(bin_dir, 'ffmpeg.exe')
    if not os.path.exists(ffmpeg):
        return None
    cmd = [
        ffmpeg, "-y", "-f", "lavfi", "-i", "testsrc=duration=5:size=1920x1080:rate=60",
        "-f", "lavfi", "-i", "sine=frequency=1000:duration=5",
        "-c:v", "libx264", "-c:a", "aac", tmp
    ]
    subprocess.run(cmd, capture_output=True)
    if os.path.exists(tmp):
        return tmp
    return None

def find_test_video(base_dir=None):
    paths = [
        os.path.expandvars(r"%LOCALAPPDATA%\Temp\Highlights\Fortnite"),
        os.path.expandvars(r"%USERPROFILE%\Videos\Fortnite")
    ]
    for p in paths:
        if os.path.exists(p):
            for f in os.listdir(p):
                if f.endswith(".mp4"):
                    return os.path.join(p, f)
    if base_dir:
        return generate_synthetic_video(base_dir)
    return None

def verify_video_movement(video_path, start_ss=2):
    """Verifies that frames in the gameplay core are unique (not frozen)."""
    bin_dir = os.path.join(os.path.dirname(__file__), '..', 'binaries')
    ffmpeg = os.path.join(bin_dir, 'ffmpeg.exe')
    out_sheet = f"movement_sheet_max_{os.getpid()}.bmp"
    cmd = [
        ffmpeg, "-y", "-ss", str(start_ss), "-i", video_path, 
        "-vf", "select='not(mod(n,60))',tile=5x1", 
        "-vframes", "1", out_sheet
    ]
    subprocess.run(cmd, capture_output=True)
    if not os.path.exists(out_sheet): return False
    try:
        with open(out_sheet, "rb") as f: data = f.read()
        os.remove(out_sheet)
        if len(data) < 100: return False
        chunk_size = len(data) // 5
        hashes = set()
        for i in range(5):
            chunk = data[i*chunk_size : (i+1)*chunk_size]
            hashes.add(hashlib.md5(chunk).hexdigest())
        return len(hashes) >= 3
    except:
        if os.path.exists(out_sheet): os.remove(out_sheet)
        return False

class DummySignal:
    def emit(self, *args):
        pass
@pytest.mark.timeout(300)
def test_real_video_max_quality():
    base_dir = os.path.abspath(os.path.join(os.path.dirname(__file__), '..'))
    vid = find_test_video(base_dir)
    if not vid:
        pytest.skip("No real video found in user directories to test.")
    bin_dir = os.path.join(base_dir, 'binaries')
    logger = setup_logger(base_dir, "sanity_max.log", "SanityMAX")
    prober = MediaProber(bin_dir, vid)
    orig_res = prober.get_resolution() or "1920x1080"
    orig_dur_s = prober.get_duration() or 60.0
    orig_size_mb = os.path.getsize(vid) / (1024*1024)

    from processing.encoders import EncoderManager
    enc_mgr = EncoderManager(logger)
    hw_strategy = "CPU"
    if enc_mgr.primary_encoder == "h264_nvenc": hw_strategy = "NVIDIA"
    elif enc_mgr.primary_encoder == "h264_amf": hw_strategy = "AMD"
    elif enc_mgr.primary_encoder == "h264_qsv": hw_strategy = "INTEL"
    logger.info(f"Using Hardware Strategy: {hw_strategy} (Encoder: {enc_mgr.primary_encoder})")
    assert hw_strategy != "CPU", (
        f"MAX quality sanity requires a hardware encoder; CPU fallback is not acceptable "
        f"(primary_encoder={enc_mgr.primary_encoder!r})."
    )
    config = VideoConfig(base_dir)
    q_level = 4
    keep_highest_res, target_mb, _ = config.get_quality_settings(q_level)
    script_dir = os.path.abspath(os.path.join(os.path.dirname(__file__), '..', 'processing'))
    app = QApplication.instance() or QApplication([])
    results = []

    class FinishedSignal:
        def emit(self, success, path):
            results.append((success, path))
    start_ms = min(5000, int(orig_dur_s * 1000 * 0.1))
    end_ms = min(start_ms + 10000, int(orig_dur_s * 1000))
    trimmed_dur_s = (end_ms - start_ms) / 1000.0
    thread = ProcessThread(
        input_path=vid,
        start_time_ms=start_ms,
        end_time_ms=end_ms,
        original_resolution=orig_res,
        is_mobile_format=True,
        speed_factor=1.0,
        script_dir=script_dir,
        progress_update_signal=DummySignal(),
        status_update_signal=DummySignal(),
        finished_signal=FinishedSignal(),
        logger=logger,
        is_boss_hp=False,
        show_teammates_overlay=True,
        quality_level=q_level,
        portrait_text="MAX QUALITY SANITY",
        intro_still_sec=0.1,
        hardware_strategy=hw_strategy
    )
    thread.run()
    assert len(results) == 1
    success, out_path = results[0]
    assert success is True, f"Render failed: {out_path}"
    assert os.path.exists(out_path)
    size_mb = os.path.getsize(out_path) / (1024 * 1024)
    expected_linear_size = (orig_size_mb / max(1.0, orig_dur_s)) * (trimmed_dur_s + 0.1)
    logger.info(f"Final output size: {size_mb:.2f} MB | Linear Expected: {expected_linear_size:.2f} MB")
    assert size_mb > 5.0, "MAX quality output is suspiciously small!"
    assert verify_video_movement(out_path), "Output video appears to be frozen!"
    if os.path.exists(out_path):
        os.remove(out_path)
