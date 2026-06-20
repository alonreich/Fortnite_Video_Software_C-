import os, sys, pytest, tempfile, shutil
sys.dont_write_bytecode = True

from sanity_tests._real_sanity_harness import install_qt_mpv_stubs, DummyMediaPlayer, DummySpinBox, DummySlider, DummyButton, DummyLogger
install_qt_mpv_stubs()

from processing.media_utils import MediaProber

def test_corrupted_input_file():
    """
    Test if the MediaProber correctly handles corrupted or non-existent files.
    Success: Prober methods return safe default values (0.0 duration, 48000Hz, None bitrate).
    """
    logger = DummyLogger()
    prober = MediaProber(bin_dir="C:/Fortnite_Video_Software/binaries", input_path="non_existent.mp4")
    prober._run_command = lambda args: None
    assert prober.get_duration() == 0.0
    assert prober.get_sample_rate() == 48000
    assert prober.get_audio_bitrate() == None
    assert prober.get_resolution() == None
    assert prober.get_video_fps_expr() == "60000/1001"
    prober._run_command = lambda args: "INVALID_DATA"
    assert prober.get_duration() == 0.0
    assert prober.get_sample_rate() == 48000
    assert prober.get_audio_bitrate() == None
