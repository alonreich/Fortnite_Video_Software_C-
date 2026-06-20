import os
import sys
import time
import hashlib
import shutil
import tempfile
import subprocess
import logging
import atexit
from pathlib import Path
from typing import Any, Sequence
from concurrent.futures import ThreadPoolExecutor, as_completed
from PyQt5 import QtCore
from PyQt5.QtCore import pyqtSignal
from PyQt5.QtGui import QPixmap

class ProcessRegistry:
    _processes = set()
    @classmethod
    def register(cls, proc):
        if proc:
            cls._processes.add(proc)
    @classmethod
    def unregister(cls, proc):
        if proc in cls._processes:
            cls._processes.discard(proc)
    @classmethod
    def kill_all(cls):
        """Aggressively terminates all registered processes."""
        for p in list(cls._processes):
            _kill_process_tree(p)
        cls._processes.clear()
atexit.register(ProcessRegistry.kill_all)

def _kill_process_tree(proc: Any | None) -> None:
    if proc is None:
        return
    ProcessRegistry.unregister(proc)
    if proc.poll() is not None:
        return
    try:
        if sys.platform == "win32":
            flags = getattr(subprocess, "CREATE_NO_WINDOW", 0)
            subprocess.run(
                ["taskkill", "/PID", str(proc.pid), "/T", "/F"],
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
                creationflags=flags,
            )
        else:
            proc.kill()
    except Exception:
        try:
            proc.kill()
        except Exception:
            pass
_CACHE_ROOT = Path(tempfile.gettempdir()) / "fvs_timeline_cache"

def _read_file_bytes(path: str) -> bytes | None:
    try:
        with open(path, "rb") as f:
            data = f.read()
        return data if data else None
    except Exception:
        return None

def _safe_media_signature(path: str) -> tuple[int, int]:
    try:
        st = os.stat(path)
        return int(getattr(st, "st_mtime_ns", int(st.st_mtime * 1_000_000_000))), int(st.st_size)
    except Exception:
        return 0, 0

def _hash_key(parts: Sequence[Any]) -> str:
    raw = "||".join(str(p) for p in parts)
    return hashlib.sha1(raw.encode("utf-8", errors="ignore")).hexdigest()

def _prune_cache_dir(cache_dir: Path, *, max_entries: int = 500, max_age_seconds: int = 7 * 24 * 3600) -> None:
    try:
        if not cache_dir.exists():
            return
        now = time.time()
        entries = []
        for p in cache_dir.iterdir():
            try:
                mtime = p.stat().st_mtime
                if now - mtime > max_age_seconds:
                    if p.is_dir():
                        shutil.rmtree(p, ignore_errors=True)
                    else:
                        p.unlink(missing_ok=True)
                    continue
                entries.append((mtime, p))
            except Exception:
                continue
        if len(entries) <= max_entries:
            return
        entries.sort(key=lambda t: t[0])
        for _, p in entries[: max(0, len(entries) - max_entries)]:
            try:
                if p.is_dir():
                    shutil.rmtree(p, ignore_errors=True)
                else:
                    p.unlink(missing_ok=True)
            except Exception:
                pass
    except Exception:
        pass

class VideoFilmstripWorker(QtCore.QThread):
    asset_ready = pyqtSignal(int, object, str)
    finished = pyqtSignal(str)

    def __init__(
        self,
        video_segments_info: Sequence[tuple[str, float, float, float, int]],
        bin_dir: str,
        *,
        stage: str = "final",
        max_workers: int = 8,
        speed_segments: list = None,
    ):
        super().__init__(None)
        self.video_segments_info = list(video_segments_info)
        self.bin_dir = bin_dir
        self.stage = str(stage or "final").lower()
        self.max_workers = max(1, int(max_workers or 8))
        self.cache_dir = _CACHE_ROOT / "video"
        self.speed_segments = speed_segments or []
        self._running = True
        self._active_procs = []

    def stop(self):
        self._running = False
        for p in list(self._active_procs):
            _kill_process_tree(p)

    def _cache_path(self, path: str, duration: float, t_start: float, speed: float) -> Path:
        sig = _safe_media_signature(path)
        key = _hash_key(("video", path, sig[0], sig[1], round(float(duration or 0.0), 3), round(float(t_start), 3), round(float(speed), 3), str(self.speed_segments), self.stage, "vGPU_v3_chunked"))
        return self.cache_dir / key

    def _segment_settings(self, duration: float) -> tuple[float, str, str]:
        dur = max(1.0, float(duration or 0.0))
        if self.stage == "fast":
            target_thumbs = 4.0
            fps = max(0.20, min(0.70, target_thumbs / dur))
            return fps, "224:126", "16"
        if self.stage == "progressive":
            target_thumbs = 6.0
            fps = max(0.30, min(1.10, target_thumbs / dur))
            return fps, "288:162", "10"
        target_thumbs = 8.0
        fps = max(0.35, min(1.40, target_thumbs / dur))
        return fps, "320:180", "8"

    def _render_chunk(self, info: tuple) -> tuple[int, list[bytes]]:
        path, duration, t_start, speed, orig_idx = info
        logger = logging.getLogger("Video_Merger")
        if not self._running:
            return orig_idx, []
        ffmpeg_exe = os.path.join(self.bin_dir, "ffmpeg.exe")
        flags = subprocess.CREATE_NO_WINDOW if sys.platform == "win32" else 0
        tmp_pattern_dir = ""
        thumbs: list[bytes] = []
        proc = None
        try:
            fps, scale, qv = self._segment_settings(duration)
            tmp_pattern_dir = tempfile.mkdtemp(prefix="fvs_thumbs_")
            out_pattern = os.path.join(tmp_pattern_dir, "thumb_%04d.jpg")
            source_dur = max(0.25, float(duration or 0.0) * float(speed or 1.0))
            source_start = max(0.0, float(t_start or 0.0) / 1000.0)
            vf_parts = [f"fps={fps:.3f}", f"scale={scale.split(':')[0]}:-1"]
            cmd: list[str] = [
                ffmpeg_exe,
                "-y",
                "-hide_banner",
                "-loglevel", "error",
                "-hwaccel", "dxva2",
                "-ss", f"{source_start:.3f}",
                "-t", f"{source_dur:.3f}",
                "-i", path,
                "-an",
                "-vf", ",".join(vf_parts),
                "-q:v", qv,
                out_pattern,
            ]
            proc = subprocess.Popen(cmd, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, creationflags=flags)
            ProcessRegistry.register(proc)
            self._active_procs.append(proc)
            while self._running and proc.poll() is None:
                time.sleep(0.05)
            if not self._running:
                _kill_process_tree(proc)
                return orig_idx, []
            if os.path.exists(tmp_pattern_dir):
                files = sorted([f for f in os.listdir(tmp_pattern_dir) if f.endswith(".jpg")])
                for f in files:
                    src = os.path.join(tmp_pattern_dir, f)
                    blob = _read_file_bytes(src)
                    if blob:
                        thumbs.append(blob)
        except Exception as e:
            logger.error("GPU_CHUNK_WORKER[%s]: error: %s", self.stage, e)
        finally:
            if proc:
                ProcessRegistry.unregister(proc)
                try: self._active_procs.remove(proc)
                except: pass
            if tmp_pattern_dir and os.path.isdir(tmp_pattern_dir):
                try:
                    shutil.rmtree(tmp_pattern_dir, ignore_errors=True)
                except Exception:
                    pass
        return orig_idx, thumbs

    def _render_single_thumb(self, info: tuple) -> tuple[int, list[bytes]]:
        path, _, t_start, _, orig_idx = info
        ffmpeg_exe = os.path.join(self.bin_dir, "ffmpeg.exe")
        flags = subprocess.CREATE_NO_WINDOW if sys.platform == "win32" else 0
        tmp_thumb = ""
        try:
            tf = tempfile.NamedTemporaryFile(prefix="fvs_thumb_fb_", suffix=".jpg", delete=False)
            tmp_thumb = tf.name
            tf.close()
            source_start = max(0.0, float(t_start or 0.0) / 1000.0)
            scale_w = "288" if self.stage in ("fast", "progressive") else "320"
            cmd = [
                ffmpeg_exe,
                "-y",
                "-hide_banner",
                "-loglevel", "error",
                "-hwaccel", "dxva2",
                "-ss", f"{source_start:.3f}",
                "-i", path,
                "-frames:v", "1",
                "-an",
                "-vf", f"scale={scale_w}:-1",
                "-q:v", "8",
                tmp_thumb,
            ]
            subprocess.run(cmd, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, creationflags=flags, timeout=12)
            blob = _read_file_bytes(tmp_thumb)
            if blob:
                return orig_idx, [blob]
        except Exception:
            pass
        finally:
            if tmp_thumb and os.path.exists(tmp_thumb):
                try:
                    os.remove(tmp_thumb)
                except Exception:
                    pass
        return orig_idx, []

    def run(self):
        logger = logging.getLogger("Video_Merger")
        logger.info("GPU_WORKER[%s]: Initializing extraction.", self.stage)
        try:
            final_results = {}
            with ThreadPoolExecutor(max_workers=max(1, int(self.max_workers or 2))) as pool:
                future_to_info = {pool.submit(self._render_chunk, info): info for info in self.video_segments_info}
                for fut in as_completed(future_to_info):
                    if not self._running:
                        break
                    info = future_to_info[fut]
                    try:
                        orig_idx, chunk_thumbs = fut.result()
                    except Exception:
                        orig_idx, chunk_thumbs = info[4], []
                    if not chunk_thumbs:
                        try:
                            orig_idx, chunk_thumbs = self._render_single_thumb(info)
                        except Exception:
                            pass
                    if chunk_thumbs:
                        final_results.setdefault(orig_idx, []).extend(chunk_thumbs)
                        self.asset_ready.emit(orig_idx, list(chunk_thumbs), self.stage)
        finally:
            self.stop()
            self.finished.emit(self.stage)

class MusicWaveformWorker(QtCore.QThread):
    asset_ready = pyqtSignal(int, object, str)
    finished = pyqtSignal(str)

    def __init__(
        self,
        music_segments_info: Sequence[tuple[str, float, float]],
        bin_dir: str,
        *,
        stage: str = "final",
        max_workers: int = 2,
    ):
        super().__init__(None)
        self.music_segments_info = list(music_segments_info)
        self.bin_dir = bin_dir
        self.stage = str(stage or "final").lower()
        self.max_workers = max(1, int(max_workers or 1))
        self.cache_dir = _CACHE_ROOT / "wave"
        self._running = True
        self._active_procs = []

    def stop(self):
        self._running = False
        for p in list(self._active_procs):
            _kill_process_tree(p)

    def _wave_cache_path(self, path: str, offset: float, dur: float) -> Path:
        sig = _safe_media_signature(path)
        key = _hash_key(
            (
                "wave",
                path,
                sig[0],
                sig[1],
                round(float(offset or 0.0), 3),
                round(float(dur or 0.0), 3),
                self.stage,
                "v3",
            )
        )
        return self.cache_dir / f"{key}.png"

    def _wave_filter(self) -> str:
        if self.stage == "fast":
            return "aformat=channel_layouts=mono,volume=1.35,showwavespic=s=1800x180:colors=0x7DD3FC:draw=full"
        return "aformat=channel_layouts=mono,volume=1.55,compand=attacks=0:decays=0.20:points=-90/-90|-55/-28|-25/-8|0/-2,showwavespic=s=3200x200:colors=0x7DD3FC:draw=full"

    def _render_wave(self, i: int, path: str, offset: float, dur: float) -> tuple[int, bytes | None]:
        logger = logging.getLogger("Video_Merger")
        if not self._running:
            return i, None
        cache_path = self._wave_cache_path(path, offset, dur)
        try:
            if cache_path.exists():
                blob = _read_file_bytes(str(cache_path))
                if blob:
                    return i, blob
        except Exception:
            pass
        ffmpeg_exe = os.path.join(self.bin_dir, "ffmpeg.exe")
        flags = subprocess.CREATE_NO_WINDOW if sys.platform == "win32" else 0
        tmp_path = ""
        proc = None
        try:
            tf = tempfile.NamedTemporaryFile(prefix="fvs_wave_", suffix=".png", delete=False)
            tmp_path = tf.name
            tf.close()
            cmd: list[str] = [
                ffmpeg_exe,
                "-y",
                "-ss",
                f"{max(0.0, float(offset or 0.0)):.3f}",
                "-t",
                f"{max(0.25, float(dur or 0.0)):.3f}",
                "-i",
                path,
                "-filter_complex",
                self._wave_filter(),
                "-frames:v",
                "1",
                tmp_path,
            ]
            proc = subprocess.Popen(cmd, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, creationflags=flags)
            ProcessRegistry.register(proc)
            self._active_procs.append(proc)
            while self._running and proc.poll() is None:
                time.sleep(0.05)
            if not self._running:
                _kill_process_tree(proc)
                return i, None
            if os.path.exists(tmp_path):
                blob = _read_file_bytes(tmp_path)
                if blob:
                    try:
                        self.cache_dir.mkdir(parents=True, exist_ok=True)
                        shutil.copy2(tmp_path, cache_path)
                    except Exception:
                        pass
                    return i, blob
        except Exception as e:
            logger.error("CPU_WORKER[%s]: waveform %s failed: %s", self.stage, i, e)
        finally:
            if proc:
                ProcessRegistry.unregister(proc)
                try: self._active_procs.remove(proc)
                except: pass
            if tmp_path and os.path.exists(tmp_path):
                try:
                    os.remove(tmp_path)
                except Exception:
                    pass
        try:
            if cache_path.exists():
                blob = _read_file_bytes(str(cache_path))
                if blob:
                    return i, blob
        except Exception:
            pass
        return i, None

    def run(self):
        logger = logging.getLogger("Video_Merger")
        logger.info("CPU_WORKER[%s]: Initializing ordered waveform generation.", self.stage)
        self.cache_dir.mkdir(parents=True, exist_ok=True)
        _prune_cache_dir(self.cache_dir, max_entries=900)
        try:
            for i, (path, offset, dur) in enumerate(self.music_segments_info):
                if not self._running:
                    break
                try:
                    wave_idx, blob = self._render_wave(i, path, offset, dur)
                except Exception as e:
                    logger.error("CPU_WORKER[%s]: waveform %s failed: %s", self.stage, i, e)
                    continue
                if blob:
                    self.asset_ready.emit(wave_idx, blob, self.stage)
        finally:
            self.stop()
            self.finished.emit(self.stage)

class SingleWaveformWorker(QtCore.QThread):
    ready = pyqtSignal(str, float, QPixmap, str, str)
    error = pyqtSignal(str, str)

    def __init__(self, track_path: str, bin_dir: str, timeout_sec: float = 15.0):
        super().__init__(None)
        self.track_path = track_path
        self.bin_dir = bin_dir
        self.timeout_sec = max(3.0, float(timeout_sec or 15.0))
        self._running = True
        self._proc: Any | None = None

    def stop(self):
        self._running = False
        _kill_process_tree(self._proc)

    def _probe_duration(self, path: str) -> float:
        ffprobe_exe = os.path.join(self.bin_dir, "ffprobe.exe")
        flags = getattr(subprocess, "CREATE_NO_WINDOW", 0) if sys.platform == "win32" else 0
        try:
            r = subprocess.run(
                [
                    ffprobe_exe,
                    "-v", "error",
                    "-select_streams", "a:0",
                    "-show_entries", "stream=duration",
                    "-of", "default=noprint_wrappers=1:nokey=1",
                    path,
                ],
                capture_output=True, text=True, creationflags=flags, timeout=5
            )
            val = (r.stdout or "").strip()
            if r.returncode == 0 and val and val != "N/A":
                return max(0.0, float(val))
            r = subprocess.run(
                [
                    ffprobe_exe,
                    "-v", "error",
                    "-show_entries", "format=duration",
                    "-of", "default=noprint_wrappers=1:nokey=1",
                    path,
                ],
                capture_output=True, text=True, creationflags=flags, timeout=5
            )
            val = (r.stdout or "").strip()
            if r.returncode == 0 and val:
                return max(0.0, float(val))
        except Exception:
            pass
        return 0.0

    def run(self):
        logger = logging.getLogger("Video_Merger")
        if not self._running or not self.track_path:
            return
        ffmpeg_exe = os.path.join(self.bin_dir, "ffmpeg.exe")
        flags = getattr(subprocess, "CREATE_NO_WINDOW", 0) if sys.platform == "win32" else 0
        tf_sync = tempfile.NamedTemporaryFile(prefix="fvs_sync_", suffix=".wav", delete=False)
        tmp_sync = tf_sync.name
        tf_sync.close()
        conv_cmd = [
            ffmpeg_exe, "-y", "-hide_banner", "-loglevel", "error",
            "-i", self.track_path,
            "-vn", "-ac", "2", "-ar", "44100", "-f", "wav",
            tmp_sync
        ]
        try:
            logger.info("WIZARD_STEP2: Straightening audio for perfect sync...")
            self._proc = subprocess.Popen(conv_cmd, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, creationflags=flags)
            ProcessRegistry.register(self._proc)
            while self._running:
                if self._proc.poll() is not None: break
                self.msleep(50)
            ProcessRegistry.unregister(self._proc)
            if not self._running:
                _kill_process_tree(self._proc)
                if os.path.exists(tmp_sync): os.remove(tmp_sync)
                return
        except Exception as e:
            if os.path.exists(tmp_sync): os.remove(tmp_sync)
            self.error.emit(self.track_path, f"Sync conversion failed: {e}")
            return
        duration = self._probe_duration(tmp_sync)
        tf_png = tempfile.NamedTemporaryFile(prefix="fvs_wave_", suffix=".png", delete=False)
        tmp_png = tf_png.name
        tf_png.close()
        cmd = [
            ffmpeg_exe,
            "-y",
            "-hide_banner",
            "-loglevel",
            "error",
            "-i",
            tmp_sync,
            "-frames:v",
            "1",
            "-filter_complex",
            "aformat=channel_layouts=mono,volume=1.5,showwavespic=s=4000x400:colors=0x7DD3FC:draw=full",
            tmp_png,
        ]
        logger.info("WIZARD_STEP2: Executing async waveform render for %s", os.path.basename(self.track_path))
        started = time.time()
        try:
            self._proc = subprocess.Popen(cmd, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, creationflags=flags)
            ProcessRegistry.register(self._proc)
            while self._running:
                if self._proc.poll() is not None:
                    break
                if (time.time() - started) > self.timeout_sec:
                    _kill_process_tree(self._proc)
                    self.error.emit(self.track_path, f"Waveform rendering timed out after {self.timeout_sec:.0f}s")
                    if os.path.exists(tmp_png): os.remove(tmp_png)
                    if os.path.exists(tmp_sync): os.remove(tmp_sync)
                    return
                self.msleep(40)
            ProcessRegistry.unregister(self._proc)
            if not self._running:
                _kill_process_tree(self._proc)
                if os.path.exists(tmp_png): os.remove(tmp_png)
                if os.path.exists(tmp_sync): os.remove(tmp_sync)
                return
            if not os.path.exists(tmp_png):
                self.error.emit(self.track_path, "Waveform render failed: output image missing")
                if os.path.exists(tmp_sync): os.remove(tmp_sync)
                return
            pm = QPixmap(tmp_png)
            if pm.isNull():
                self.error.emit(self.track_path, "Waveform render failed: invalid image")
                if os.path.exists(tmp_png): os.remove(tmp_png)
                if os.path.exists(tmp_sync): os.remove(tmp_sync)
                return
            self.ready.emit(self.track_path, duration, pm, tmp_png, tmp_sync)
        except Exception as e:
            _kill_process_tree(self._proc)
            if os.path.exists(tmp_png): os.remove(tmp_png)
            if os.path.exists(tmp_sync): os.remove(tmp_sync)
            self.error.emit(self.track_path, f"Waveform failed: {e}")
