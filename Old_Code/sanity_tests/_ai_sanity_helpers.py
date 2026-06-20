from __future__ import annotations
from pathlib import Path
ROOT = Path(__file__).resolve().parents[1]

def read_source(rel_path: str) -> str:
    path = ROOT / rel_path
    content = path.read_text(encoding="utf-8")
    if rel_path.replace("\\", "/") == "ui/main_window.py":
        parts_dir = ROOT / "ui" / "parts"
        if parts_dir.exists():
            for part_file in parts_dir.glob("*.py"):
                content += "\n" + part_file.read_text(encoding="utf-8")
    return content

def assert_all_present(text: str, needles: list[str]) -> None:
    missing = [n for n in needles if n not in text]
    assert not missing, f"Missing expected snippets: {missing}"
