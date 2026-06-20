from __future__ import annotations
import os
import shutil
import subprocess
import sys
import time
from system import diagnostic_runtime
from system.live_logging import ReopenableFileHandler, start_log_pipe_broker
from system.utils import LogManager

def test_main_log_handler_does_not_hold_file_and_recreates_after_delete(tmp_path) -> None:
    logger_name = "LiveLoggingRecreateTest"
    logger = LogManager.setup_logger(str(tmp_path), "main_app.log", logger_name)
    log_path = tmp_path / "logs" / "main_app.log"
    logger.info("first live line")
    assert log_path.exists(), "First log write should create the log file immediately."
    assert "first live line" in log_path.read_text(encoding="utf-8")
    assert any(isinstance(h, ReopenableFileHandler) for h in logger.handlers)
    os.remove(log_path)
    assert not log_path.exists(), "The app logger must not keep the log file locked."
    logger.info("second live line")
    assert log_path.exists(), "Next log write should recreate a deleted log file."
    text = log_path.read_text(encoding="utf-8")
    assert "second live line" in text
    assert "first live line" not in text

def test_python_and_mpv_debug_logs_recreate_after_delete(tmp_path, monkeypatch) -> None:
    logs_dir = tmp_path / "logs"
    py_log = logs_dir / "python_debug.log"
    mpv_log = logs_dir / "mpv_trace.log"
    monkeypatch.setattr(diagnostic_runtime, "LOGS_DIR", logs_dir)
    monkeypatch.setattr(diagnostic_runtime, "PYTHON_DEBUG_LOG_PATH", py_log)
    monkeypatch.setattr(diagnostic_runtime, "MPV_TRACE_LOG_PATH", mpv_log)
    monkeypatch.setattr(diagnostic_runtime, "_runtime_dirs_ready", False)
    diagnostic_runtime.append_python_debug("python first")
    diagnostic_runtime.append_mpv_trace("info", "mpv", "mpv first")
    assert py_log.exists() and mpv_log.exists()
    os.remove(py_log)
    os.remove(mpv_log)
    diagnostic_runtime.append_python_debug("python second")
    diagnostic_runtime.append_mpv_trace("warn", "mpv", "mpv second")
    assert "python second" in py_log.read_text(encoding="utf-8")
    assert "mpv second" in mpv_log.read_text(encoding="utf-8")

def test_log_pipe_broker_recreates_deleted_logs_and_captures_native_pipe(tmp_path) -> None:
    logs_dir = tmp_path / "logs"
    py_log = logs_dir / "python_debug.log"
    main_log = logs_dir / "main_app.log"
    mpv_log = logs_dir / "mpv_trace.log"
    proc = start_log_pipe_broker(py_log, [py_log, main_log, mpv_log])
    try:
        assert proc.stdin is not None
        deadline = time.time() + 5.0
        while time.time() < deadline and not (py_log.exists() and main_log.exists() and mpv_log.exists()):
            time.sleep(0.05)
        assert py_log.exists() and main_log.exists() and mpv_log.exists()
        shutil.rmtree(logs_dir)
        proc.stdin.write(b"fatal native line before process death\n")
        proc.stdin.flush()
        deadline = time.time() + 5.0
        while time.time() < deadline:
            if py_log.exists() and main_log.exists() and mpv_log.exists():
                text = py_log.read_text(encoding="utf-8", errors="replace")
                if "fatal native line before process death" in text:
                    break
            time.sleep(0.05)
        assert py_log.exists(), "Broker should recreate python_debug.log after deletion."
        assert main_log.exists(), "Broker heartbeat should recreate main_app.log after deletion."
        assert mpv_log.exists(), "Broker heartbeat should recreate mpv_trace.log after deletion."
        assert "fatal native line before process death" in py_log.read_text(encoding="utf-8", errors="replace")
    finally:
        try:
            if proc.stdin:
                proc.stdin.close()
        except Exception:
            pass
        try:
            proc.wait(timeout=5)
        except Exception:
            proc.kill()

def test_console_manager_keeps_logs_alive_after_delete_until_hard_exit(tmp_path) -> None:
    script = f"""

import os, shutil, sys, time
from pathlib import Path
from system import diagnostic_runtime
base = r"{str(tmp_path)}"
logs = os.path.join(base, "logs")
diagnostic_runtime.LOGS_DIR = Path(logs)
diagnostic_runtime.PYTHON_DEBUG_LOG_PATH = Path(logs) / "python_debug.log"
diagnostic_runtime.MPV_TRACE_LOG_PATH = Path(logs) / "mpv_trace.log"
diagnostic_runtime._runtime_dirs_ready = False

from system.utils import ConsoleManager
logger = ConsoleManager.initialize(base, "main_app.log", "Main_App")
logger.info("child logger started")
paths = [
    os.path.join(logs, "main_app.log"),
    os.path.join(logs, "python_debug.log"),
    os.path.join(logs, "mpv_trace.log"),
]
time.sleep(0.8)
shutil.rmtree(logs, ignore_errors=True)
time.sleep(0.8)
logger.info("child logger after deletion")
print("stdout after deletion")
sys.stderr.write("stderr after deletion\\n")
sys.stderr.flush()
diagnostic_runtime.append_mpv_trace("info", "mpv", "mpv after deletion")
time.sleep(0.8)
os._exit(7)
"""
    result = subprocess.run(
        [sys.executable, "-c", script],
        cwd=os.getcwd(),
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
        timeout=15,
    )
    assert result.returncode == 7
    logs_dir = tmp_path / "logs"
    main_log = logs_dir / "main_app.log"
    py_log = logs_dir / "python_debug.log"
    mpv_log = logs_dir / "mpv_trace.log"
    assert main_log.exists(), "main_app.log should be recreated while app is still running."
    assert py_log.exists(), "python_debug.log should be recreated while app is still running."
    assert mpv_log.exists(), "mpv_trace.log should be recreated while app is still running."
    assert "child logger after deletion" in main_log.read_text(encoding="utf-8", errors="replace")
    py_text = py_log.read_text(encoding="utf-8", errors="replace")
    assert "stdout after deletion" in py_text
    assert "stderr after deletion" in py_text
    assert "mpv after deletion" in mpv_log.read_text(encoding="utf-8", errors="replace")
