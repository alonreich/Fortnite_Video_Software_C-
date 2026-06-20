from __future__ import annotations
from sanity_tests._ai_sanity_helpers import assert_all_present, read_source

def test_integration_19_hardware_scan_timeout_forces_cpu() -> None:
    """Hardware watchdog timeout path should force CPU mode contract."""
    src = read_source("app.py")
    assert_all_present(
        src,
        [
            "class HardwareWorker(QObject):",
            "self.watchdog_timer = threading.Timer(15.0, watchdog)",
            "self.stop_requested = True",
            'os.environ["VIDEO_FORCE_CPU"] = "1"',
            'for mode, encoder in (("NVIDIA", "h264_nvenc"), ("AMD", "h264_amf"), ("INTEL", "h264_qsv")):',
            "if check_encoder_capability(self.ffmpeg_path, encoder):",
        ],
    )
