import os, sys, pytest, tempfile, shutil
sys.dont_write_bytecode = True

from sanity_tests._real_sanity_harness import install_qt_mpv_stubs, DummyMediaPlayer, DummySpinBox, DummySlider, DummyButton, DummyListItem
install_qt_mpv_stubs()

import types
from PyQt5.QtCore import Qt
from ui.widgets.music_wizard_step_pages import MergerMusicWizardStepPagesMixin

def test_wizard_song_switch_memory():
    """
    Test if picking a new song in Step 1 correctly clears the current track state.
    Success: current_track_path is updated and track_list is populated.
    """

    class Host(MergerMusicWizardStepPagesMixin):
        def __init__(self):
            self.track_list = types.SimpleNamespace(
                clear=lambda: None,
                addItem=lambda x: None,
                setItemWidget=lambda x, y: None,
                count=lambda: len(self._items),
                item=lambda i: self._items[i]
            )
            self._items = []
            self.track_list.clear = lambda: self._items.clear()

            def add_item(it): self._items.append(it)
            self.track_list.addItem = add_item
            self.coverage_progress = types.SimpleNamespace(
                setRange=lambda x, y: None,
                setFormat=lambda x: None
            )
            self.logger = types.SimpleNamespace(
                warning=lambda x: None,
                info=lambda x: None,
                error=lambda x: None,
                debug=lambda x: None
            )
            self.current_track_path = None
            self._track_scanner = None
            
        def _stop_track_scanner(self): pass

        def _on_scanning_started(self): pass
    host = Host()
    files = [("song1.mp3", "/path/song1.mp3"), ("song2.mp3", "/path/song2.mp3")]
    host._on_scanning_finished(files)
    assert len(host._items) == 2
    assert host._items[0].data(Qt.UserRole) == "/path/song1.mp3"
    assert host._items[1].data(Qt.UserRole) == "/path/song2.mp3"
    host.current_track_path = host._items[0].data(Qt.UserRole)
    assert host.current_track_path == "/path/song1.mp3"
    host.current_track_path = host._items[1].data(Qt.UserRole)
    assert host.current_track_path == "/path/song2.mp3"
