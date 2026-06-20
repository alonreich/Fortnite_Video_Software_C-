from __future__ import annotations
import atexit
import argparse
import difflib
import hashlib
import json
import os
import shutil
import sys
import tempfile
import threading
import time
from pathlib import Path
from typing import Any, Iterator, Optional, Sequence, cast
import psutil
from system.live_logging import append_text_unlocked, touch_unlocked
PROJECT_ROOT = Path(__file__).resolve().parent.parent
LOGS_DIR = PROJECT_ROOT / "logs"
MASTER_BACKUP_DIR = Path(r"C:\Users\alon\.gemini\Backups")
STATE_MAP_PATH = PROJECT_ROOT / "state_map.json"
MAIN_APP_CONFIG_PATH = PROJECT_ROOT / "config" / "main_app" / "main_app.conf"
MPV_TRACE_LOG_PATH = LOGS_DIR / "mpv_trace.log"
PYTHON_DEBUG_LOG_PATH = LOGS_DIR / "python_debug.log"
DIFF_TEXT_PATH = LOGS_DIR / "master_state_diff.txt"
DIFF_HTML_PATH = LOGS_DIR / "master_state_diff.html"
ROLLBACK_LOG_PATH = LOGS_DIR / "rollback_protocol.log"
ALARM_MESSAGE = (
    "ISOLATION ACTIVE. MASTER BACKUP SECURED AT "
    r"C:\Users\alon\.gemini\Backups. PROCEED TO TEST."
)
DEFAULT_RUNTIME_PROFILE: dict[str, Any] = {
    "mode": "gpu_reintegration",
    "diagnostic_profile": {
        "hwdec": "no",
        "vo": "null",
        "video_vo": "null",
        "audio_vo": "null",
        "msg_level": "all=trace",
        "log_file": str(MPV_TRACE_LOG_PATH),
    },
    "gpu_profile": {
        "hwdec": "auto-copy",
        "vo": "gpu",
        "gpu_api": "d3d11",
        "gpu_context": "d3d11",
    },
}
TEXT_SUFFIXES = {
    ".cmd", ".conf", ".css", ".html", ".ini", ".json", ".log", ".md",
    ".ps1", ".py", ".qss", ".rst", ".toml", ".txt", ".xml", ".yaml", ".yml",
}
_alarm_emitted = False
_runtime_lock = threading.RLock()
_runtime_dirs_ready = False
_last_debug_emit_monotonic: dict[str, float] = {}

def _clone_default_profile() -> dict[str, Any]:
    return cast(dict[str, Any], json.loads(json.dumps(DEFAULT_RUNTIME_PROFILE)))

def ensure_runtime_directories() -> None:
    global _runtime_dirs_ready
    if _runtime_dirs_ready:
        return
    with _runtime_lock:
        if _runtime_dirs_ready:
            return
        LOGS_DIR.mkdir(parents=True, exist_ok=True)
        MASTER_BACKUP_DIR.mkdir(parents=True, exist_ok=True)
        _runtime_dirs_ready = True

def _close_python_debug_handle() -> None:
    return None
atexit.register(_close_python_debug_handle)

def append_python_debug(message: str) -> None:
    stamp = time.strftime("%Y-%m-%d %H:%M:%S")
    with _runtime_lock:
        append_text_unlocked(PYTHON_DEBUG_LOG_PATH, f"{stamp} | {message}\n")

def append_python_debug_throttled(key: str, message: str, min_interval_sec: float = 0.20) -> bool:
    now = time.monotonic()
    with _runtime_lock:
        last = float(_last_debug_emit_monotonic.get(key, 0.0) or 0.0)
        if (now - last) < float(min_interval_sec):
            return False
        _last_debug_emit_monotonic[key] = now
    append_python_debug(message)
    return True

def get_python_debug_log_path() -> str:
    ensure_runtime_directories()
    touch_unlocked(PYTHON_DEBUG_LOG_PATH)
    return str(PYTHON_DEBUG_LOG_PATH)

def get_mpv_trace_log_path() -> str:
    ensure_runtime_directories()
    touch_unlocked(MPV_TRACE_LOG_PATH)
    return str(MPV_TRACE_LOG_PATH)

def append_mpv_trace(level: str, prefix: str, text: str) -> None:
    stamp = time.strftime("%Y-%m-%d %H:%M:%S")
    clean = str(text or "").rstrip()
    if not clean:
        return
    with _runtime_lock:
        for line in clean.splitlines():
            append_text_unlocked(MPV_TRACE_LOG_PATH, f"{stamp} | {level} | {prefix} | {line}\n")

def _load_config() -> dict[str, Any]:
    if not MAIN_APP_CONFIG_PATH.exists():
        return {}
    try:
        with open(MAIN_APP_CONFIG_PATH, "r", encoding="utf-8") as handle:
            data = json.load(handle)
        return data if isinstance(data, dict) else {}
    except Exception:
        return {}

def _atomic_write_json(path: Path, payload: dict[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    fd, temp_path = tempfile.mkstemp(dir=str(path.parent), suffix=".tmp")
    try:
        with os.fdopen(fd, "w", encoding="utf-8") as handle:
            json.dump(payload, handle, indent=4)
            handle.flush()
            os.fsync(handle.fileno())
        os.replace(temp_path, path)
    except Exception:
        if os.path.exists(temp_path):
            os.remove(temp_path)
        raise

def get_runtime_profile() -> dict[str, Any]:
    profile: dict[str, Any] = _clone_default_profile()
    config = _load_config()
    user_profile = config.get("mpv_runtime_profile", {})
    if isinstance(user_profile, dict):
        typed_user_profile = cast(dict[str, Any], user_profile)
        for key, value in typed_user_profile.items():
            if isinstance(value, dict) and isinstance(profile.get(key), dict):
                cast(dict[str, Any], profile[key]).update(cast(dict[str, Any], value))
            else:
                profile[key] = value
    return profile

def write_runtime_profile(mode: str) -> dict[str, Any]:
    config = _load_config()
    profile = get_runtime_profile()
    profile["mode"] = str(mode or "gpu_reintegration")
    config["mpv_runtime_profile"] = profile
    _atomic_write_json(MAIN_APP_CONFIG_PATH, config)
    append_python_debug(f"RUNTIME PROFILE UPDATED | mode={profile['mode']}")
    return profile

def is_isolation_active() -> bool:
    return str(get_runtime_profile().get("mode", "gpu_reintegration")).lower() == "diagnostic_isolation"

def apply_mpv_runtime_overrides(kwargs: dict[str, Any]) -> dict[str, Any]:
    runtime = get_runtime_profile()
    result = dict(kwargs)
    result.setdefault("msg_level", runtime["diagnostic_profile"].get("msg_level", "all=info"))
    result.setdefault("loglevel", "info")
    result.pop("log_file", None)
    result.pop("log-file", None)
    embedded = result.get("wid") is not None
    audio_only = str(result.get("vid", "")).lower() == "no" or str(result.get("vo", "")).lower() == "null"
    if is_isolation_active():
        for key in (
            "gpu_api",
            "gpu_context",
            "gpu-context",
            "gpu-async-compute",
            "d3d11_exclusive_fs",
            "d3d11-exclusive-fs",
        ):
            result.pop(key, None)
        result["hwdec"] = runtime["diagnostic_profile"].get("hwdec", "no")
        if audio_only and not embedded:
            result["vo"] = runtime["diagnostic_profile"].get("audio_vo", "null")
        else:
            requested_vo = runtime["diagnostic_profile"].get("video_vo", "gpu")
            if str(requested_vo).lower() == "null" and embedded:
                requested_vo = "gpu"
            result["vo"] = requested_vo
        return result
    gpu = runtime.get("gpu_profile", {})
    if not audio_only:
        result["hwdec"] = gpu.get("hwdec", "auto-copy")
        result["vo"] = gpu.get("vo", "gpu")
    result["gpu_api"] = gpu.get("gpu_api", "d3d11")
    result["gpu_context"] = gpu.get("gpu_context", "d3d11")
    return result

def log_isolation_alarm(logger: Optional[Any] = None) -> None:
    global _alarm_emitted
    if _alarm_emitted or not is_isolation_active():
        return
    append_python_debug(ALARM_MESSAGE)
    if logger is not None:
        try:
            logger.warning(ALARM_MESSAGE)
        except Exception:
            pass
    _alarm_emitted = True

def _iter_files(base_dir: Path, ignore_logs: bool = False) -> Iterator[tuple[str, Path]]:
    for root, dirs, files in os.walk(base_dir):
        dirs[:] = [d for d in dirs if d not in {".git", "__pycache__"}]
        for name in files:
            path = Path(root) / name
            rel = path.relative_to(base_dir).as_posix()
            if ignore_logs and rel.startswith("logs/"):
                continue
            yield rel, path

def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with open(path, "rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().lower()

def generate_master_diff(ignore_logs: bool = True) -> dict[str, Any]:
    ensure_runtime_directories()
    active = {rel: path for rel, path in _iter_files(PROJECT_ROOT, ignore_logs=ignore_logs)}
    backup = {rel: path for rel, path in _iter_files(MASTER_BACKUP_DIR, ignore_logs=ignore_logs)}
    html: list[str] = ["<html><head><meta charset='utf-8'><title>Master State Diff</title></head><body>", "<h1>Master State Diff</h1>"]
    text: list[str] = []
    changed: list[str] = []
    table_builder = difflib.HtmlDiff(wrapcolumn=120)
    for rel in sorted(set(active) | set(backup)):
        active_path = active.get(rel)
        backup_path = backup.get(rel)
        if active_path and backup_path and _sha256(active_path) == _sha256(backup_path):
            continue
        changed.append(rel)
        text.append(f"=== {rel} ===")
        if active_path is None:
            text.append("REMOVED FROM ACTIVE PROJECT")
            html.append(f"<h2>{rel}</h2><p>Removed from active project.</p>")
            continue
        if backup_path is None:
            text.append("ADDED IN ACTIVE PROJECT")
            html.append(f"<h2>{rel}</h2><p>Added in active project.</p>")
            continue
        if active_path.suffix.lower() in TEXT_SUFFIXES and backup_path.suffix.lower() in TEXT_SUFFIXES:
            with open(backup_path, "r", encoding="utf-8", errors="replace") as handle:
                backup_lines = handle.readlines()
            with open(active_path, "r", encoding="utf-8", errors="replace") as handle:
                active_lines = handle.readlines()
            text.extend(difflib.unified_diff(backup_lines, active_lines, fromfile=f"master/{rel}", tofile=f"active/{rel}", lineterm=""))
            html.append(f"<h2>{rel}</h2>")
            html.append(table_builder.make_table(backup_lines, active_lines, f"master/{rel}", f"active/{rel}", context=True, numlines=3))
        else:
            text.append("BINARY CHANGE DETECTED")
            html.append(f"<h2>{rel}</h2><p>Binary change detected.</p>")
        text.append("")
    if not changed:
        text.append("NO DRIFT DETECTED AGAINST MASTER STATE")
        html.append("<p>No drift detected against master state.</p>")
    html.append("</body></html>")
    DIFF_TEXT_PATH.write_text("\n".join(text), encoding="utf-8")
    DIFF_HTML_PATH.write_text("\n".join(html), encoding="utf-8")
    append_python_debug(f"MASTER DIFF GENERATED | changed_files={len(changed)} | text={DIFF_TEXT_PATH} | html={DIFF_HTML_PATH}")
    return {"changed_files": changed, "text_report": str(DIFF_TEXT_PATH), "html_report": str(DIFF_HTML_PATH)}

def _stop_project_processes() -> None:
    root_text = str(PROJECT_ROOT).lower()
    current_pid = os.getpid()
    targeted: list[psutil.Process] = []
    for proc in psutil.process_iter(["pid", "name", "exe", "cmdline"]):
        try:
            if proc.info["pid"] == current_pid:
                continue
            cmdline = " ".join(proc.info.get("cmdline") or []).lower()
            exe = (proc.info.get("exe") or "").lower()
            name = (proc.info.get("name") or "").lower()
            if root_text in cmdline or root_text in exe or name in {"mpv.exe", "ffmpeg.exe", "ffprobe.exe", "ffplay.exe"}:
                proc.terminate()
                targeted.append(proc)
        except Exception:
            continue
    _, alive = psutil.wait_procs(targeted, timeout=1)
    for proc in alive:
        try:
            cmdline = " ".join(proc.cmdline()).lower()
            exe = (proc.exe() or "").lower()
            if root_text in cmdline or root_text in exe:
                proc.kill()
        except Exception:
            continue

def rollback_to_master() -> str:
    ensure_runtime_directories()
    _stop_project_processes()
    backup_files = {rel: path for rel, path in _iter_files(MASTER_BACKUP_DIR)}
    active_files = {rel: path for rel, path in _iter_files(PROJECT_ROOT)}
    for rel in sorted(set(active_files) - set(backup_files), reverse=True):
        try:
            active_files[rel].unlink()
        except Exception:
            pass
    for rel, path in backup_files.items():
        target = PROJECT_ROOT / rel
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(path, target)
    mismatches: list[str] = []
    if STATE_MAP_PATH.exists():
        with open(STATE_MAP_PATH, "r", encoding="utf-8") as handle:
            state_map = json.load(handle)
        for item in state_map.get("files", []):
            rel = cast(Optional[str], item.get("path"))
            expected = item.get("sha256")
            if not rel:
                continue
            target = PROJECT_ROOT / rel
            if not target.exists() or _sha256(target) != expected:
                mismatches.append(rel)
    message = "ROLLBACK COMPLETE. SYSTEM RETURNED TO 100% ORIGINAL STATE."
    details = [message, f"mismatches={len(mismatches)}"]
    if mismatches:
        details.extend(mismatches)
    ROLLBACK_LOG_PATH.write_text("\n".join(details), encoding="utf-8")
    append_python_debug(message)
    if mismatches:
        raise RuntimeError(f"Rollback verification failed: {mismatches}")
    return message

def main(argv: Optional[Sequence[str]] = None) -> int:
    parser = argparse.ArgumentParser(description="Diagnostic runtime helper")
    parser.add_argument(
        "command",
        choices=["alarm", "diff", "rollback", "enable-isolation", "enable-gpu"],
    )
    args = parser.parse_args(argv)
    if args.command == "alarm":
        log_isolation_alarm()
        print(ALARM_MESSAGE)
        return 0
    if args.command == "diff":
        result = generate_master_diff()
        print(json.dumps(result, indent=2))
        return 0
    if args.command == "enable-isolation":
        print(json.dumps(write_runtime_profile("diagnostic_isolation"), indent=2))
        return 0
    if args.command == "enable-gpu":
        print(json.dumps(write_runtime_profile("gpu_reintegration"), indent=2))
        return 0
    print(rollback_to_master())
    return 0
if __name__ == "__main__":
    sys.exit(main())
