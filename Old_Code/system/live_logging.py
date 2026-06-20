from __future__ import annotations
import logging
import json
import os
import subprocess
import sys
import threading
import time
from pathlib import Path

def _rotate_unlocked(path: str, incoming_bytes: int, max_bytes: int, backup_count: int) -> None:
    if max_bytes <= 0 or backup_count <= 0:
        return
    try:
        if not os.path.exists(path):
            return
        if os.path.getsize(path) + max(0, int(incoming_bytes)) <= max_bytes:
            return
        oldest = f"{path}.{backup_count}"
        if os.path.exists(oldest):
            os.remove(oldest)
        for idx in range(backup_count - 1, 0, -1):
            src = f"{path}.{idx}"
            dst = f"{path}.{idx + 1}"
            if os.path.exists(src):
                os.replace(src, dst)
        os.replace(path, f"{path}.1")
    except OSError:
        pass

def append_text_unlocked(path: str | os.PathLike[str], text: str, *, encoding: str = "utf-8",
                         max_bytes: int = 0, backup_count: int = 0) -> None:
    target = os.fspath(path)
    Path(target).parent.mkdir(parents=True, exist_ok=True)
    payload = str(text or "")
    incoming = len(payload.encode(encoding, errors="replace"))
    _rotate_unlocked(target, incoming, int(max_bytes or 0), int(backup_count or 0))
    with open(target, "a", encoding=encoding, errors="replace", buffering=1) as handle:
        handle.write(payload)
        handle.flush()

def touch_unlocked(path: str | os.PathLike[str]) -> None:
    append_text_unlocked(path, "")

class ReopenableFileHandler(logging.Handler):
    terminator = "\n"

    def __init__(self, filename: str, *, maxBytes: int = 0, backupCount: int = 0,
                 encoding: str = "utf-8") -> None:
        super().__init__()
        self.baseFilename = os.path.abspath(filename)
        self.maxBytes = int(maxBytes or 0)
        self.backupCount = int(backupCount or 0)
        self.encoding = encoding

    def emit(self, record: logging.LogRecord) -> None:
        try:
            msg = self.format(record)
            append_text_unlocked(
                self.baseFilename,
                msg + self.terminator,
                encoding=self.encoding,
                max_bytes=self.maxBytes,
                backup_count=self.backupCount,
            )
        except Exception:
            self.handleError(record)

    def flush(self) -> None:
        return None

    def close(self) -> None:
        super().close()

class ReopenableTextStream:
    def __init__(self, log_path: str, label: str, original_stream=None) -> None:
        self.log_path = os.path.abspath(log_path)
        self.label = str(label or "STREAM").upper()
        self.original_stream = original_stream
        self._buffer = ""

    def write(self, message: str) -> int:
        text = str(message or "")
        if not text:
            return 0
        if self.original_stream is not None:
            try:
                self.original_stream.write(text)
                self.original_stream.flush()
            except Exception:
                pass
        self._buffer += text
        while "\n" in self._buffer:
            line, self._buffer = self._buffer.split("\n", 1)
            self._write_line(line)
        return len(text)

    def flush(self) -> None:
        if self.original_stream is not None:
            try:
                self.original_stream.flush()
            except Exception:
                pass
        if self._buffer.strip():
            self._write_line(self._buffer)
        self._buffer = ""

    def _write_line(self, line: str) -> None:
        clean = str(line or "").rstrip()
        if not clean:
            return
        stamp = time.strftime("%Y-%m-%d %H:%M:%S")
        append_text_unlocked(self.log_path, f"{stamp} | {self.label} | {clean}\n")

    def isatty(self) -> bool:
        return False

    def fileno(self) -> int:
        if self.original_stream is not None and hasattr(self.original_stream, "fileno"):
            return self.original_stream.fileno()
        raise OSError("stream has no file descriptor")
    @property
    def encoding(self) -> str:
        return getattr(self.original_stream, "encoding", "utf-8")

def restoreable_original_stdout():
    return getattr(sys, "__stdout__", None) or sys.stdout

def restoreable_original_stderr():
    return getattr(sys, "__stderr__", None) or sys.stderr

def start_log_pipe_broker(python_log_path: str | os.PathLike[str],
                          touch_paths: list[str | os.PathLike[str]] | tuple[str | os.PathLike[str], ...]):
    script = Path(__file__).with_name("log_pipe_broker.py")
    paths = [os.path.abspath(os.fspath(path)) for path in touch_paths if path]
    if os.fspath(python_log_path) not in paths:
        paths.insert(0, os.path.abspath(os.fspath(python_log_path)))
    flags = subprocess.CREATE_NO_WINDOW if sys.platform == "win32" else 0
    return subprocess.Popen(
        [
            sys.executable,
            "-u",
            str(script),
            "--python-log",
            os.path.abspath(os.fspath(python_log_path)),
            "--touch-json",
            json.dumps(paths),
        ],
        stdin=subprocess.PIPE,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
        creationflags=flags,
    )

def start_touch_heartbeat(touch_paths: list[str | os.PathLike[str]] | tuple[str | os.PathLike[str], ...],
                          interval_sec: float = 0.5):
    stop_event = threading.Event()
    paths = [os.path.abspath(os.fspath(path)) for path in touch_paths if path]

    def _run() -> None:
        while not stop_event.wait(max(0.1, float(interval_sec))):
            for path in paths:
                try:
                    touch_unlocked(path)
                except Exception:
                    pass
    thread = threading.Thread(target=_run, name="FVSLogTouchHeartbeat", daemon=True)
    thread.start()
    return stop_event
