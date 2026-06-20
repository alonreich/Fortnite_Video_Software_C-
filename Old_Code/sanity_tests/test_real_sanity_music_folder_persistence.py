from __future__ import annotations
from pathlib import Path
import types
from sanity_tests._real_sanity_harness import install_qt_mpv_stubs
install_qt_mpv_stubs()

from system.config import ConfigManager
from ui.parts.music_mixin import MusicMixin

def test_music_wizard_folder_change_persists_to_config_file(monkeypatch, tmp_path: Path) -> None:
    cfg_path = tmp_path / "main_app.conf"
    custom_music = tmp_path / "my_music"
    custom_music.mkdir(parents=True, exist_ok=True)

    import PyQt5.QtWidgets as qtw

    class _FD:
        @staticmethod
        def getExistingDirectory(*_a, **_k):
            return str(custom_music)
    monkeypatch.setattr(qtw, "QFileDialog", _FD, raising=False)
    host = types.SimpleNamespace()

    import threading
    host._mpv_lock = threading.RLock()
    host._safe_mpv_get = lambda p, d=None, **k: d
    host._safe_mpv_set = lambda p, v, target_player=None, **k: setattr(target_player or host.player, p, v) if hasattr(target_player or host.player, p) else True
    host.base_dir = str(tmp_path)
    host.config_manager = ConfigManager(str(cfg_path))
    host.logger = types.SimpleNamespace(info=lambda *a, **k: None)
    host._mp3_dir = types.MethodType(MusicMixin._mp3_dir, host)
    loaded_folders: list[str] = []
    wizard = types.SimpleNamespace(
        mp3_dir=str(tmp_path / "mp3"),
        load_tracks=lambda folder: loaded_folders.append(folder),
    )
    MusicMixin._on_select_music_folder(host, wizard)
    assert wizard.mp3_dir == str(custom_music)
    assert loaded_folders == [str(custom_music)]
    cfg_reloaded = ConfigManager(str(cfg_path))
    assert cfg_reloaded.config.get("custom_mp3_dir") == str(custom_music)
