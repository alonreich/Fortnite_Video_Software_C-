from __future__ import annotations
from sanity_tests._real_sanity_harness import install_qt_mpv_stubs
install_qt_mpv_stubs()

from PyQt5.QtCore import Qt, QPoint
from ui.widgets.trimmed_slider import TrimmedSlider

class MockEvent:
    def __init__(self, x, y, button=Qt.LeftButton):
        self._pos = QPoint(x, y)
        self._button = button

    def pos(self): return self._pos

    def x(self): return self._pos.x()

    def button(self): return self._button

def test_core_02_music_handles_clamped_by_video_trim():
    slider = TrimmedSlider()
    slider.set_duration_ms(10000)
    slider.setRange(0, 10000)
    slider.set_trim_times(2000, 8000)
    slider.set_music_visible(True)
    slider.set_music_times(4000, 6000)
    slider._has_moved_since_press = True
    slider._dragging_music_handle = 'start'
    slider._map_pos_to_value = lambda x: 1000
    slider.mouseMoveEvent(MockEvent(0, 0))
    assert slider.music_start_ms == 2000
    slider._dragging_music_handle = 'end'
    slider._map_pos_to_value = lambda x: 9000
    slider.mouseMoveEvent(MockEvent(0, 0))
    assert slider.music_end_ms == 8000

def test_challenge_05_video_trim_pushes_music_handles():
    slider = TrimmedSlider()
    slider.set_duration_ms(10000)
    slider.setRange(0, 10000)
    slider.set_trim_times(0, 10000)
    slider.set_music_visible(True)
    slider.set_music_times(4000, 6000)
    slider._has_moved_since_press = True
    slider._dragging_handle = 'start'
    slider._map_pos_to_value = lambda x: 5000
    slider.mouseMoveEvent(MockEvent(0, 0))
    assert slider.music_start_ms == 5000
    assert slider.music_end_ms >= 5100
