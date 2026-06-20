import os
import hashlib
import json
import subprocess
import math
import logging
import time
import signal
import psutil
from pathlib import Path
from PyQt5.QtCore import QThread, pyqtSignal, QMutex, QMutexLocker
from utilities.merger_utils import _ffprobe, _get_logger, kill_process_tree

def _safe_subprocess_run(cmd, timeout_seconds, logger, description="subprocess"):
    """
    Safely run a subprocess with timeout and guaranteed cleanup.
    Returns (success, stdout, stderr, error_message)
    """
    process = None
    start_time = time.time()
    try:
        flags = subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0
        process = subprocess.Popen(
            cmd,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            stdin=subprocess.DEVNULL,
            text=True,
            encoding='utf-8',
            errors='replace',
            creationflags=flags
        )
        try:
            stdout, stderr = process.communicate(timeout=timeout_seconds)
            elapsed = time.time() - start_time
            logger.debug(f"{description} completed in {elapsed:.1f}s, returncode={process.returncode}")
            if process.returncode == 0:
                return True, stdout, stderr, ""
            else:
                error_msg = f"Process failed with code {process.returncode}"
                if stderr:
                    error_msg += f": {stderr[:200]}"
                return False, stdout, stderr, error_msg
        except subprocess.TimeoutExpired:
            logger.warning(f"{description} timed out after {timeout_seconds}s, killing process tree")
            kill_process_tree(process.pid)
            try:
                process.wait(timeout=2)
            except subprocess.TimeoutExpired:
                logger.error(f"Process tree still alive after kill attempt")
            return False, "", "", f"Timeout after {timeout_seconds} seconds"
    except FileNotFoundError as e:
        return False, "", "", f"Command not found: {cmd[0]}"
    except Exception as e:
        logger.error(f"{description} unexpected error: {e}")
        return False, "", "", str(e)
    finally:
        if process and process.poll() is None:
            try:
                process.terminate()
                process.wait(timeout=1)
            except:
                pass

class FolderScanWorker(QThread):
    finished = pyqtSignal(list, str)

    def __init__(self, folder: str, exts: set[str]):
        super().__init__()
        self.folder = folder
        self.exts = {e.lower() for e in (exts or set())}
        self._cancelled = False
        self._mutex = QMutex()
        self.logger = _get_logger()

    def cancel(self):
        with QMutexLocker(self._mutex):
            self._cancelled = True

    def run(self):
        files = []
        exclude_dirs = {'.git', 'node_modules', '__pycache__', 'venv', '.env'}
        try:
            def _scan(path, current_depth, max_depth=4):
                if current_depth > max_depth: return
                with QMutexLocker(self._mutex):
                    if self._cancelled: return
                try:
                    with os.scandir(path) as it:
                        for entry in it:
                            if entry.is_file():
                                if os.path.splitext(entry.name)[1].lower() in self.exts:
                                    files.append(entry.path)
                            elif entry.is_dir():
                                if entry.name.lower() not in exclude_dirs:
                                    _scan(entry.path, current_depth + 1, max_depth)
                except OSError:
                    pass
            _scan(self.folder, 0, max_depth=5)
            self.finished.emit(files, "")
        except Exception as e:
            self.finished.emit([], str(e))

class FastFileLoaderWorker(QThread):
    """
    Loads files, calculates partial hash for dupes, and probes basic metadata.
    Optimized for speed.
    """
    file_loaded = pyqtSignal(str, int, dict, str)
    progress = pyqtSignal(int, int)
    finished = pyqtSignal(int, int)

    def __init__(self, files, existing_files, existing_hashes, max_limit, ffmpeg_path):
        super().__init__()
        self.files = files
        self.existing_files = set(existing_files)
        self.existing_hashes = set(existing_hashes)
        self.max_limit = max_limit
        self.ffprobe = _ffprobe(ffmpeg_path)
        self._cancelled = False
        self._mutex = QMutex()
        self.existing_file_sizes = {}

    def _calculate_partial_hash(self, filepath):
        """Hashes first 256KB + middle 256KB + last 256KB + file size for robust duplicate detection (Issue #7)."""
        try:
            h = hashlib.sha256()
            p = Path(filepath)
            stat = p.stat()
            size = stat.st_size
            h.update(str(size).encode('utf-8'))
            with open(filepath, "rb") as f:
                h.update(f.read(256 * 1024))
                if size > 1024 * 1024:
                    f.seek(size // 2)
                    h.update(f.read(256 * 1024))
                if size > 512 * 1024:
                    f.seek(-256 * 1024, 2)
                    h.update(f.read(256 * 1024))
            return h.hexdigest()
        except (OSError, IOError, MemoryError):
            return None

    def run(self):
        added = 0
        duplicates = 0
        room = self.max_limit - len(self.existing_files)
        total = max(1, len(self.files))
        for idx, f in enumerate(self.files, start=1):
            with QMutexLocker(self._mutex):
                if self._cancelled: break
            if added >= room: 
                break
            if f in self.existing_files:
                duplicates += 1
                continue
            f_hash = self._calculate_partial_hash(f) or ""
            if f_hash and f_hash in self.existing_hashes:
                duplicates += 1
                continue
            try:
                sz = os.path.getsize(f)
            except OSError:
                sz = 0
            probe_data = {}
            try:
                cmd = [self.ffprobe, "-v", "error", "-show_entries", 
                       "format=duration:stream=width,height,codec_name,sample_rate,channels", 
                       "-of", "json", f]
                flags = subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0
                r = subprocess.run(cmd, capture_output=True, text=True, encoding='utf-8', errors='replace', creationflags=flags, timeout=5)
                if r.returncode == 0 and r.stdout.strip():
                    probe_data = json.loads(r.stdout)
            except (subprocess.SubprocessError, json.JSONDecodeError, OSError, ValueError):
                probe_data = {}
            self.file_loaded.emit(f, sz, probe_data, f_hash)
            self.existing_files.add(f)
            if f_hash:
                self.existing_hashes.add(f_hash)
            added += 1
            self.progress.emit(idx, total)
        self.progress.emit(total, total)
        self.finished.emit(added, duplicates)

    def cancel(self):
        with QMutexLocker(self._mutex):
            self._cancelled = True

class ProbeWorker(QThread):
    """
    Dedicated worker for re-probing or deep probing if necessary.
    Usually we try to use cached data, but this is for the 'Merge' step
    verification if needed.
    """
    finished = pyqtSignal(list, float)
    error = pyqtSignal(str)

    def __init__(self, video_files, ffmpeg_path):
        super().__init__()
        self.video_files = video_files
        self.ffprobe = _ffprobe(ffmpeg_path)
        self._cancelled = False
        self._mutex = QMutex()
        self._cancel_msg = "Cancelled by user."
        self._is_aborted = False

    def abort(self):
        with QMutexLocker(self._mutex):
            self._is_aborted = True
            self._cancelled = True

    def run(self):
        results = []
        total = 0.0
        logger = _get_logger()
        try:
            for path in self.video_files:
                with QMutexLocker(self._mutex):
                    if self._cancelled or self._is_aborted:
                        if not self._is_aborted:
                            self.error.emit(self._cancel_msg)
                        return
                duration = 0.0
                resolution = None
                has_audio = False
                try:
                    timeout_s = 6
                    try:
                        size_mb = os.path.getsize(path) / (1024.0 * 1024.0)
                        timeout_s = int(max(6, min(20, 4 + math.ceil(size_mb / 250.0))))
                    except Exception:
                        timeout_s = 6
                    cmd = [
                        self.ffprobe,
                        "-v", "error",
                        "-show_entries", "format=duration,bit_rate:stream=codec_type,width,height,codec_name,pix_fmt,r_frame_rate,sample_rate,channels,bit_rate",
                        "-of", "json",
                        path,
                    ]
                    success, stdout, stderr, error_msg = _safe_subprocess_run(
                        cmd, timeout_s, logger, description=f"ffprobe {os.path.basename(path)}"
                    )
                    if success and stdout:
                        payload = json.loads(stdout)
                        duration = float(payload.get("format", {}).get("duration") or 0.0)
                        format_bitrate = int(payload.get("format", {}).get("bit_rate") or 0)
                        streams = payload.get("streams", []) or []
                        v_codec = ""
                        v_pix_fmt = ""
                        v_fps = 0.0
                        v_bitrate = 0
                        a_codec = ""
                        a_rate = 0
                        a_channels = 0
                        a_bitrate = 0
                        for s in streams:
                            if s.get("codec_type") == "video" and s.get("width") and s.get("height"):
                                resolution = (int(s.get("width")), int(s.get("height")))
                                v_codec = str(s.get("codec_name") or "")
                                v_pix_fmt = str(s.get("pix_fmt") or "")
                                v_bitrate = int(s.get("bit_rate") or 0)
                                fr = str(s.get("r_frame_rate") or "0/1")
                                try:
                                    if "/" in fr:
                                        a, b = fr.split("/", 1)
                                        b_f = float(b or 1.0)
                                        v_fps = float(a or 0.0) / (b_f if b_f else 1.0)
                                    else:
                                        v_fps = float(fr)
                                except Exception:
                                    v_fps = 0.0
                                break
                        for s in streams:
                            if s.get("codec_type") == "audio":
                                a_codec = str(s.get("codec_name") or "")
                                a_bitrate = int(s.get("bit_rate") or 0)
                                try:
                                    a_rate = int(float(s.get("sample_rate") or 0))
                                except Exception:
                                    a_rate = 0
                                try:
                                    a_channels = int(s.get("channels") or 0)
                                except Exception:
                                    a_channels = 0
                                break
                        if v_bitrate == 0 and format_bitrate > 0:
                            v_bitrate = max(0, format_bitrate - a_bitrate)
                        if v_bitrate == 0 and duration > 0:
                            try:
                                size_bytes = os.path.getsize(path)
                                v_bitrate = int((size_bytes * 8) / duration) - a_bitrate
                                v_bitrate = max(0, v_bitrate)
                            except: pass
                        has_audio = any(s.get("codec_type") == "audio" for s in streams)
                    else:
                        logger.warning(f"ffprobe failed for {path}: {error_msg}")
                except Exception:
                    duration = 0.0
                    resolution = None
                    has_audio = False
                    v_codec = ""
                    v_pix_fmt = ""
                    v_fps = 0.0
                    v_bitrate = 0
                    a_codec = ""
                    a_rate = 0
                    a_channels = 0
                    a_bitrate = 0
                results.append({
                    "path": path,
                    "duration": duration,
                    "resolution": resolution,
                    "has_audio": has_audio,
                    "video_codec": v_codec,
                    "video_pix_fmt": v_pix_fmt,
                    "video_fps": v_fps,
                    "video_bitrate": v_bitrate,
                    "audio_codec": a_codec,
                    "audio_rate": a_rate,
                    "audio_channels": a_channels,
                    "audio_bitrate": a_bitrate,
                })
                total += duration
            with QMutexLocker(self._mutex):
                if self._cancelled or self._is_aborted:
                    if not self._is_aborted:
                        self.error.emit(self._cancel_msg)
                    return
            self.finished.emit(results, total)
        except Exception as e:
            if not self._is_aborted:
                self.error.emit(str(e))

    def cancel(self):
        with QMutexLocker(self._mutex):
            self._cancelled = True
