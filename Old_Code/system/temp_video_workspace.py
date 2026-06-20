import os
import shutil
import tempfile
import uuid
WORKSPACE_NAME = "Fortnite_Video_Software"

def workspace_dir() -> str:
    return os.path.join(tempfile.gettempdir(), WORKSPACE_NAME)

def is_workspace_path(path: str) -> bool:
    try:
        root = os.path.abspath(workspace_dir())
        target = os.path.abspath(path or "")
        return target == root or target.startswith(root + os.sep)
    except Exception:
        return False

def cleanup_workspace(logger=None) -> None:
    path = workspace_dir()
    try:
        if os.path.isdir(path):
            shutil.rmtree(path, ignore_errors=True)
        if logger:
            logger.info("TEMP_WORKSPACE: cleaned %s", path)
    except Exception as exc:
        if logger:
            logger.warning("TEMP_WORKSPACE: cleanup failed for %s: %s", path, exc)

def stage_video_file(source_path: str, logger=None) -> str:
    source = os.path.abspath(str(source_path or ""))
    if not os.path.isfile(source):
        raise FileNotFoundError(source)
    os.makedirs(workspace_dir(), exist_ok=True)
    if is_workspace_path(source):
        return source
    base, ext = os.path.splitext(os.path.basename(source))
    safe_base = "".join(ch if ch.isalnum() or ch in (" ", "-", "_", ".") else "_" for ch in base).strip() or "video"
    target = os.path.join(workspace_dir(), f"{safe_base}_{uuid.uuid4().hex[:10]}{ext or '.mp4'}")
    shutil.copy2(source, target)
    if logger:
        logger.info("TEMP_WORKSPACE: staged source video %s -> %s", source, target)
    return target
