import os
import sys
import tempfile
import shutil
from typing import Dict, Any, Optional, List
from PyQt5.QtCore import QObject, pyqtSignal

os.environ['PYTHONDONTWRITEBYTECODE'] = '1'
os.environ['PYTHONPYCACHEPREFIX'] = os.path.join(tempfile.gettempdir(), 'pycache_disabled')
sys.dont_write_bytecode = True

def cleanup_pycache():
    current_dir = os.path.dirname(os.path.abspath(__file__))
    for root, dirs, files in os.walk(current_dir):
        if '__pycache__' in dirs:
            try: shutil.rmtree(os.path.join(root, '__pycache__'))
            except Exception: pass
        for file in files:
            if file.endswith('.pyc') or file.endswith('.pyo'):
                try: os.remove(os.path.join(root, file))
                except Exception: pass

cleanup_pycache()

from .config_data import VideoConfig; from .media_utils import MediaProber; from .filter_builder import FilterBuilder
from .encoders import EncoderManager; from .text_ops import TextWrapper, fix_hebrew_text, apply_bidi_formatting
from .system_utils import create_subprocess, monitor_ffmpeg_progress, kill_process_tree
from .worker import ProcessThread

__all__ = [
    'VideoConfig',
    'MediaProber',
    'FilterBuilder',
    'EncoderManager',
    'TextWrapper',
    'ProcessThread',
]
