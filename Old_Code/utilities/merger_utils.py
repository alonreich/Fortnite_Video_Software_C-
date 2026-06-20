import json
from pathlib import Path
import sys
import os
sys.dont_write_bytecode = True
os.environ['PYTHONDONTWRITEBYTECODE'] = '1'

import traceback
import psutil
import time
import subprocess
import shutil
from typing import Optional, Callable, Any

def _proj_root() -> Path:
    return Path(__file__).resolve().parents[1]

def _conf_path() -> Path:
    return _proj_root() / "config" / "video_merger.conf"
SESSION_ONLY_CONFIG_KEYS = {"music_widget"}

def sanitize_persistent_config(cfg: dict) -> dict:
    """Drop state that should never survive a clean Video Merger launch."""
    if not isinstance(cfg, dict):
        return {}
    clean = dict(cfg)
    for key in SESSION_ONLY_CONFIG_KEYS:
        clean.pop(key, None)
    return clean

def _human(n_bytes: int) -> str:
    """Returns human-readable size."""
    if n_bytes is None: return "0 B"
    units = ["B","KB","MB","GB","TB"]
    s = 0
    n = float(n_bytes)
    while n >= 1024.0 and s < len(units)-1:
        n /= 1024.0; s += 1
    return f"{n:.2f} {units[s]}"

def _get_logger():
    """Gets or sets up the logger safely."""

    from utilities.merger_system import MergerLogManager
    project_root = str(Path(__file__).resolve().parents[1])
    return MergerLogManager.setup_logger(project_root, "Video_Merger.log", "Video_Merger")

def _load_conf() -> dict:
    p = _conf_path()
    logger = _get_logger()
    try:
        if p.exists():
            content = p.read_text(encoding="utf-8")
            cfg = json.loads(content)
            return sanitize_persistent_config(cfg)
        return {}
    except Exception as e:
        logger.error(f"Failed to load config from {p}: {e}")
        return {}

def _save_conf(cfg: dict) -> None:
    """Atomic save configuration."""
    p = _conf_path()
    logger = _get_logger()
    try:
        cfg = sanitize_persistent_config(cfg)
        p.parent.mkdir(parents=True, exist_ok=True)

        import tempfile
        with tempfile.NamedTemporaryFile(mode='w', encoding='utf-8', dir=str(p.parent), prefix=f"{p.name}.tmp.", delete=False) as tmp_file:
            tmp_path = tmp_file.name
            json.dump(cfg, tmp_file, indent=2)
            tmp_file.flush()
            os.fsync(tmp_file.fileno())
        if os.name == 'nt':
            if os.path.exists(p):
                os.replace(tmp_path, str(p))
            else:
                os.rename(tmp_path, str(p))
        else:
            os.replace(tmp_path, str(p))
    except Exception as e:
        logger.error(f"Failed to save config to {p}: {e}")
        if 'tmp_path' in locals() and os.path.exists(tmp_path):
            try: os.unlink(tmp_path)
            except: pass

def _ffprobe(ffmpeg_path) -> str:
    """Robustly find ffprobe."""
    try:
        ffmpeg_dir = Path(ffmpeg_path).parent
        for name in ("ffprobe", "ffprobe.exe"):
            p = ffmpeg_dir / name
            if p.exists():
                return str(p)
    except OSError:
        pass
    return "ffprobe"

def get_disk_free_space(path_str):
    """Returns free space in bytes for the drive containing path."""
    try:
        return shutil.disk_usage(os.path.dirname(os.path.abspath(path_str))).free
    except Exception:
        return 10**12

def escape_ffmpeg_path(path: str) -> str:
    """
    Robustly escapes paths for FFmpeg concat demuxer.
    Single quotes are the main enemy. 
    ' -> '\'' 
    """
    p = path.replace('\\', '/')
    return p.replace("'", "'\\''")

def kill_process_tree(pid: int) -> None:
    """Kills a process tree safely."""
    try:
        if not pid or pid <= 0: return
        parent = psutil.Process(pid)
        for child in parent.children(recursive=True):
            try: child.kill()
            except: pass
        parent.kill()
    except: pass

def build_audio_ducking_filters(video_audio_stream: str, music_stream: str, music_volume: float = 1.0, sample_rate: int = 48000, video_has_audio: bool = True, duration: float = 0.0) -> list[str]:
    """
    Returns FFmpeg filter strings for ducking music based on video audio.
    Protects video dialogue by ducking music when video audio is present.
    Args:
        video_audio_stream: Input video audio stream label (e.g., '[0:a]')
        music_stream: Input music stream label (e.g., '[mus]')
        music_volume: Music volume multiplier (0.0 to 1.0)
        sample_rate: Output sample rate
        video_has_audio: Whether the video stream actually contains audio.
        duration: Total duration of the clip to prevent infinite streams (Fix #1).
    Returns:
        List of filter strings to be joined with ';'
    """
    filters = []
    filters.append(f"{music_stream}asplit=2[mus_base][mus_to_filter]")
    filters.append("[mus_base]lowpass=f=150[mus_low]")
    filters.append("[mus_to_filter]highpass=f=150[mus_high]")
    trigger_source = video_audio_stream
    if not video_has_audio:
        dur_str = f":d={duration}" if duration > 0 else ""
        filters.append(f"anullsrc=channel_layout=stereo:sample_rate={sample_rate}{dur_str}[video_silence]")
        trigger_source = "[video_silence]"
    filters.append(f"{trigger_source}asplit=2[game_out_pre][game_trig]")
    filters.append("[game_trig]highpass=f=200,lowpass=f=3500,agate=threshold=0.05:attack=5:release=100[trig_cleaned]")
    filters.append("[trig_cleaned]equalizer=f=1000:t=q:w=2:g=10[trig_final]")
    duck_params = "threshold=0.15:ratio=2.5:attack=1:release=400:detection=rms"
    filters.append(f"[mus_high][trig_final]sidechaincompress={duck_params}[mus_high_ducked]")
    filters.append("[mus_low][mus_high_ducked]amix=inputs=2:weights='1 1':normalize=0[a_music_reconstructed]")
    if music_volume != 1.0:
        filters.append(f"[a_music_reconstructed]volume={music_volume:.4f}[a_music_vol]")
        music_reconstructed = "[a_music_vol]"
    else:
        music_reconstructed = "[a_music_reconstructed]"
    filters.append(
        f"[game_out_pre]{music_reconstructed}"
        "amix=inputs=2:duration=first:dropout_transition=3:weights='1 1':normalize=0,"
        f"alimiter=limit=0.95:attack=5:release=50,aresample={sample_rate}[a_out]"
    )
    return filters
