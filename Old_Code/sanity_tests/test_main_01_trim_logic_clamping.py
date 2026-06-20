import os, sys, pytest, tempfile, shutil
sys.dont_write_bytecode = True

from sanity_tests._real_sanity_harness import install_qt_mpv_stubs, DummyMediaPlayer, DummySpinBox, DummySlider, DummyButton
install_qt_mpv_stubs()

import types
from ui.parts.trim_mixin import TrimMixin

def test_trim_logic_clamping():
    """
    Test if the trim handles correctly clamp when Start is dragged past End.
    Success: Start position is never greater than End position, and MIN_TRIM_GAP is respected.
    """

    class Host(TrimMixin):
        def __init__(self):
            self.original_duration_ms = 10000
            self.trim_start_ms = 0
            self.trim_end_ms = 10000
            self.positionSlider = DummySlider()
            self.player = DummyMediaPlayer()
            self.MIN_TRIM_GAP = 1000
        
        def _update_trim_widgets_from_trim_times(self): pass

        def style(self): 
            return types.SimpleNamespace(standardIcon=lambda *_: None)
    host = Host()
    host.positionSlider.setValue(9500)
    host.set_start_time()
    assert host.trim_start_ms == 9000
    assert host.trim_end_ms == 10000
    assert host.trim_end_ms - host.trim_start_ms >= host.MIN_TRIM_GAP
    host.positionSlider.setValue(500)
    host.set_end_time()
    assert host.trim_start_ms == 0
    assert host.trim_end_ms == 1000
    assert host.trim_end_ms - host.trim_start_ms >= host.MIN_TRIM_GAP
