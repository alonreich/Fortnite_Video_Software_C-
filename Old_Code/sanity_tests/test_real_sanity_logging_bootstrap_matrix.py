from __future__ import annotations
from dataclasses import dataclass
import re
from system.config import ConfigManager
from utilities.merger_config import MergerConfigManager
from sanity_tests._ai_sanity_helpers import read_source
@dataclass
class LoggingCaseResult:
    name: str
    passed: bool
    details: str

def test_logging_bootstrap_matrix(tmp_path, capsys) -> None:
    results: list[LoggingCaseResult] = []
    main_cfg_path = tmp_path / "config" / "main_app.conf"
    cm = ConfigManager(str(main_cfg_path))
    results.append(
        LoggingCaseResult(
            "Main Config bootstrap",
            main_cfg_path.exists() and isinstance(cm.config, dict),
            f"exists={main_cfg_path.exists()} keys={list(cm.config.keys())}",
        )
    )
    merger_cfg_path = tmp_path / "config" / "video_merger.conf"
    mm = MergerConfigManager(str(merger_cfg_path))
    mm.save_config({"geometry": {"x": 1, "y": 2, "w": 3, "h": 4}})
    results.append(
        LoggingCaseResult(
            "Merger Config bootstrap",
            merger_cfg_path.exists(),
            f"exists={merger_cfg_path.exists()}",
        )
    )
    startup_sources = {
        "main": read_source("app.py"),
        "merger": read_source("utilities/video_merger.py"),
        "crop": read_source("developer_tools/crop_tools.py"),
        "advanced": read_source("advanced/advanced_video_editor.py"),
    }
    pat = re.compile(r'initialize\([^\)]*"([^"]+\.log)"')
    log_names: list[str] = []
    for txt in startup_sources.values():
        log_names.extend(pat.findall(txt))
    unique_ok = len(log_names) == len(set(log_names))
    expected = {"main_app.log", "video_merger.log", "crop_tools.log", "advanced_editor.log"}
    results.append(
        LoggingCaseResult(
            "Per-app log file isolation",
            unique_ok and set(log_names) == expected,
            f"names={sorted(log_names)}",
        )
    )
    merger_native_src = read_source("utilities/merger_system.py")
    shared_native_ok = 'mpv.log_path = os.path.join(log_dir, "mpv.log")' in merger_native_src
    results.append(
        LoggingCaseResult(
            "Shared native C++ log target (main/crop/merger)",
            shared_native_ok,
            "merger uses shared logs/mpv.log" if shared_native_ok else "merger does not target logs/mpv.log",
        )
    )
    print("\n=== LOGGING BOOTSTRAP MATRIX REPORT ===")
    for r in results:
        print(f"[{ 'PASS' if r.passed else 'FAIL' }] {r.name}: {r.details}")
    out = capsys.readouterr().out
    assert "LOGGING BOOTSTRAP MATRIX REPORT" in out
    failed = [r for r in results if not r.passed]
    assert not failed, " ; ".join(f"{r.name}: {r.details}" for r in failed)
