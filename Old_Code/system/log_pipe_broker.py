from __future__ import annotations
import argparse
import json
import sys
import threading
import time
from pathlib import Path
if __package__ in (None, ""):
    sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from system.live_logging import append_text_unlocked, touch_unlocked

def _touch_loop(paths: list[str], stop_event: threading.Event) -> None:
    while not stop_event.wait(0.5):
        for path in paths:
            try:
                touch_unlocked(path)
            except Exception:
                pass

def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--python-log", required=True)
    parser.add_argument("--touch-json", default="[]")
    args = parser.parse_args()
    try:
        touch_paths = json.loads(args.touch_json)
    except Exception:
        touch_paths = []
    touch_paths = [str(p) for p in touch_paths if p]
    if args.python_log not in touch_paths:
        touch_paths.insert(0, args.python_log)
    for path in touch_paths:
        try:
            touch_unlocked(path)
        except Exception:
            pass
    stop_event = threading.Event()
    worker = threading.Thread(target=_touch_loop, args=(touch_paths, stop_event), daemon=True)
    worker.start()
    try:
        while True:
            chunk = sys.stdin.buffer.readline()
            if not chunk:
                break
            text = chunk.decode("utf-8", errors="replace")
            if text.strip():
                stamp = time.strftime("%Y-%m-%d %H:%M:%S")
                append_text_unlocked(args.python_log, f"{stamp} | NATIVE | {text.rstrip()}\n")
    finally:
        stop_event.set()
        for path in touch_paths:
            try:
                touch_unlocked(path)
            except Exception:
                pass
    return 0
if __name__ == "__main__":
    raise SystemExit(main())
