import os
import sys

def get_project_root():
    return os.path.abspath(os.path.join(os.path.dirname(__file__), '..'))
PROJECT_ROOT = get_project_root()

class SharedPaths:
    ROOT = PROJECT_ROOT
    BINARIES = os.path.join(ROOT, 'binaries')
    CONFIG = os.path.join(ROOT, 'config')
    PROCESSING = os.path.join(ROOT, 'processing')
    DEVELOPER_TOOLS = os.path.join(ROOT, 'developer_tools')
    LOGS = os.path.join(ROOT, 'logs')
    ICONS = os.path.join(ROOT, 'icons')

    import tempfile
    TEMP = os.path.join(tempfile.gettempdir(), 'FVS_Temp')
    @staticmethod
    def get_icon_path():
        preferred = os.path.join(SharedPaths.ICONS, "Video_Icon_File.ico")
        fallback = os.path.join(SharedPaths.ICONS, "app_icon.ico")
        return preferred if os.path.exists(preferred) else fallback
    @staticmethod
    def get_config_path(filename):
        return os.path.join(SharedPaths.CONFIG, filename)
    @staticmethod
    def get_processing_path(filename):
        return os.path.join(SharedPaths.PROCESSING, filename)
