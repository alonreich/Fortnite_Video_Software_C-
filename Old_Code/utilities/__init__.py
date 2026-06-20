import sys
import os
import shutil

# Enforce no-cache policy for the utilities package
sys.dont_write_bytecode = True
os.environ['PYTHONDONTWRITEBYTECODE'] = '1'

def _purge_local_pycache(root: str):
    try:
        for base, dirs, _ in os.walk(root):
            if "__pycache__" in dirs:
                target = os.path.join(base, "__pycache__")
                shutil.rmtree(target, ignore_errors=True)
    except Exception:
        pass

_purge_local_pycache(os.path.dirname(__file__))
