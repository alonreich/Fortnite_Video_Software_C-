import os, sys, pytest, time, threading
sys.dont_write_bytecode = True

from sanity_tests._real_sanity_harness import install_qt_mpv_stubs, DummyMediaPlayer, DummySpinBox, DummySlider
install_qt_mpv_stubs()

import types
from ui.parts.player_mixin import PlayerMixin

def test_rapid_seek_throttling_logic(monkeypatch):
    """
    Test if the seek throttle (50ms) correctly prevents excessive MPV calls.
    Success: Multiple calls within 50ms only trigger ONE seek.
    """

    class Host(PlayerMixin):
        def __init__(self):
            self._mpv_lock = threading.RLock()
            self.player = DummyMediaPlayer()
            self.timer = types.SimpleNamespace(isActive=lambda: False, start=lambda *a: None, stop=lambda: None)
            self._last_scrub_ts = 0.0
            self.speed_spinbox = DummySpinBox(1.0)
            self.trim_start_ms = 0
            self.trim_end_ms = 10000
            self.positionSlider = DummySlider()
            self._is_seeking_active = False
    host = Host()
    t = {"v": 100.0}
    monkeypatch.setattr("time.time", lambda: t["v"])
    PlayerMixin.set_player_position(host, 1000, sync_only=True)
    assert len(host.player.set_time_calls) == 1
    assert host.player.get_time() == 1000
    t["v"] = 100.01
    PlayerMixin.set_player_position(host, 2000, sync_only=True)
    assert len(host.player.set_time_calls) == 1
    assert host.player.get_time() == 1000
    t["v"] = 100.07
    PlayerMixin.set_player_position(host, 3000, sync_only=True)
    assert len(host.player.set_time_calls) == 2
    assert host.player.get_time() == 3000
