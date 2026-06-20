from __future__ import annotations
from dataclasses import dataclass
import types
import sys
from sanity_tests._real_sanity_harness import install_qt_mpv_stubs
install_qt_mpv_stubs()
qtcore = sys.modules.get("PyQt5.QtCore")
if qtcore is not None and not hasattr(qtcore, "QByteArray"):
    class _QByteArray:
        @staticmethod
        def fromBase64(_raw: bytes):
            return b""
    qtcore.QByteArray = _QByteArray
if qtcore is not None and not hasattr(qtcore, "QEasingCurve"):
    qtcore.QEasingCurve = type("QEasingCurve", (), {"InOutQuad": 0})

from system.config import ConfigManager
from developer_tools.utils import PersistentWindowMixin
import developer_tools.utils as _dev_utils
from ui.widgets.music_wizard_misc import MergerMusicWizardMiscMixin
from ui.widgets.granular_speed_editor import GranularSpeedEditor
from utilities.merger_window_logic import MergerWindowLogic
@dataclass
class GeometryCaseResult:
    name: str
    passed: bool
    details: str

def _log() -> object:
    return types.SimpleNamespace(info=lambda *a, **k: None, warning=lambda *a, **k: None, error=lambda *a, **k: None, debug=lambda *a, **k: None)

class _Point:
    def __init__(self, x: int, y: int) -> None:
        self._x = int(x)
        self._y = int(y)

    def x(self) -> int:
        return self._x

    def y(self) -> int:
        return self._y

def _safe_persistent_save(host: object) -> None:
    orig_qapp = _dev_utils.QApplication

    class _QApp:
        @staticmethod
        def instance():
            return None
    _dev_utils.QApplication = _QApp
    try:
        PersistentWindowMixin.save_geometry(host)
    finally:
        _dev_utils.QApplication = orig_qapp

def _main_app_case(tmp_path) -> GeometryCaseResult:
    conf = tmp_path / "main_app.conf"
    cm = ConfigManager(str(conf))
    host = types.SimpleNamespace()

    import threading
    host._mpv_lock = threading.RLock()
    host._safe_mpv_get = lambda p, d=None, **k: d
    host._safe_mpv_set = lambda p, v, target_player=None, **k: setattr(target_player or host.player, p, v) if hasattr(target_player or host.player, p) else True
    host.width = lambda: 1234
    host.height = lambda: 777
    host.pos = lambda: _Point(111, 222)
    host.frameGeometry = lambda: types.SimpleNamespace(center=lambda: _Point(111, 222))
    host.last_dir = str(tmp_path)
    host.setWindowTitle = lambda *_: None
    host.title_info_provider = lambda: "Main"
    host.external_config_manager = cm
    host.settings_key = "window_geometry"
    host.extra_data_provider = None
    host._loading_persistence = False
    _safe_persistent_save(host)
    saved = cm.load_config().get("window_geometry", {})
    ok = saved.get("x") == 111 and saved.get("y") == 222 and saved.get("w") == 1234 and saved.get("h") == 777
    return GeometryCaseResult("Main App", ok, f"saved={saved}")

def _granular_editor_case(tmp_path) -> GeometryCaseResult:
    conf = tmp_path / "main_app.conf"
    cm = ConfigManager(str(conf))
    parent = types.SimpleNamespace(config_manager=cm)
    editor = GranularSpeedEditor.__new__(GranularSpeedEditor)
    editor.parent_app = parent
    editor.geometry = lambda: types.SimpleNamespace(x=lambda: 21, y=lambda: 31, width=lambda: 1410, height=lambda: 810)
    GranularSpeedEditor.save_geometry(editor)
    saved = cm.load_config().get("granular_editor_geometry", {})
    ok = saved.get("x") == 21 and saved.get("y") == 31 and saved.get("w") == 1410 and saved.get("h") == 810
    return GeometryCaseResult("Granular Speed Editor", ok, f"saved={saved}")

def _music_wizard_case(tmp_path) -> GeometryCaseResult:
    conf = tmp_path / "main_app.conf"
    cm = ConfigManager(str(conf))
    cfg = cm.load_config()
    cfg["music_wizard_custom_geo"] = {"step_2": {'x': 300, 'y': 140, 'w': 1600, 'h': 850}}
    cm.save_config(cfg)
    wiz = types.SimpleNamespace(
        _startup_complete=True,
        parent_window=types.SimpleNamespace(config_manager=cm),
        stack=types.SimpleNamespace(currentIndex=lambda: 2),
        geometry=lambda: types.SimpleNamespace(x=lambda: 300, y=lambda: 140, width=lambda: 1600, height=lambda: 850),
        _do_save_step_geometry=lambda: None
    )
    geo = cm.load_config().get("music_wizard_custom_geo", {}).get("step_2", {})
    ok = geo.get("x") == 300 and geo.get("y") == 140 and geo.get("w") == 1600 and geo.get("h") == 850
    return GeometryCaseResult("Music Wizard (Step Geometry)", ok, f"saved={geo}")

def _video_merger_case() -> GeometryCaseResult:
    w = types.SimpleNamespace(
        _cfg={},
        x=lambda: 88,
        y=lambda: 66,
        width=lambda: 1020,
        height=lambda: 730,
        saveGeometry=lambda: types.SimpleNamespace(toBase64=lambda: b"R0VPTQ=="),
        _last_dir="C:/",
        _last_out_dir="D:/",
        unified_music_widget=types.SimpleNamespace(export_state=lambda: {"ok": True}),
        logger=_log(),
    )
    logic = MergerWindowLogic(w)
    called = {"cfg": None}

    import utilities.merger_window_logic as mwl
    orig = mwl._save_conf
    mwl._save_conf = lambda cfg: called.update(cfg=cfg)
    try:
        logic.save_config()
    finally:
        mwl._save_conf = orig
    saved = (called.get("cfg") or {}).get("geometry", {})
    ok = saved.get("x") == 88 and saved.get("y") == 66 and saved.get("w") == 1020 and saved.get("h") == 730
    return GeometryCaseResult("Video Merger", ok, f"saved={saved}")

def _crop_tool_case(tmp_path) -> GeometryCaseResult:
    conf = tmp_path / "crop_tools.conf"
    cm = ConfigManager(str(conf))
    host = types.SimpleNamespace()

    import threading
    host._mpv_lock = threading.RLock()
    host._safe_mpv_get = lambda p, d=None, **k: d
    host._safe_mpv_set = lambda p, v, target_player=None, **k: setattr(target_player or host.player, p, v) if hasattr(target_player or host.player, p) else True
    host.width = lambda: 1800
    host.height = lambda: 930
    host.pos = lambda: _Point(45, 55)
    host.frameGeometry = lambda: types.SimpleNamespace(center=lambda: _Point(45, 55))
    host.last_dir = str(tmp_path)
    host.setWindowTitle = lambda *_: None
    host.title_info_provider = lambda: "Crop"
    host.external_config_manager = cm
    host.settings_key = "window_geometry"
    host.extra_data_provider = None
    host._loading_persistence = False
    _safe_persistent_save(host)
    saved = cm.load_config().get("window_geometry", {})
    ok = saved.get("x") == 45 and saved.get("y") == 55 and saved.get("w") == 1800 and saved.get("h") == 930
    return GeometryCaseResult("Crop Tool", ok, f"saved={saved}")

def test_geometry_persistence_report_end_user_readable(tmp_path, capsys) -> None:
    results = [
        _main_app_case(tmp_path),
        _granular_editor_case(tmp_path),
        _music_wizard_case(tmp_path),
        _video_merger_case(),
        _crop_tool_case(tmp_path),
    ]
    print("\n=== GEOMETRY PERSISTENCE REPORT ===")
    for r in results:
        print(f"[{ 'PASS' if r.passed else 'FAIL' }] {r.name}: {r.details}")
    out = capsys.readouterr().out
    assert "GEOMETRY PERSISTENCE REPORT" in out
    assert "Main App" in out
    assert "Granular Speed Editor" in out
    assert "Music Wizard" in out
    assert "Video Merger" in out
    assert "Crop Tool" in out
    failed = [r for r in results if not r.passed]
    assert not failed, " ; ".join(f"{r.name}: {r.details}" for r in failed)
