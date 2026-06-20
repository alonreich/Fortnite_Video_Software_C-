import sys
import os
sys.dont_write_bytecode = True
os.environ['PYTHONDONTWRITEBYTECODE'] = '1'

from PyQt5.QtWidgets import QApplication, QMessageBox
from PyQt5.QtCore import QTimer
from PyQt5.QtGui import QIcon
project_root = os.path.abspath(os.path.join(os.path.dirname(__file__), '..'))
if project_root not in sys.path:
    sys.path.insert(0, project_root)

from system.utils import UIManager
from utilities.merger_system import MergerConsoleManager, MergerProcessManager as ProcessManager, MergerDependencyDoctor as DependencyDoctor
logger = MergerConsoleManager.initialize(project_root, "video_merger.log", "Video_Merger")

import traceback
import faulthandler
import logging
import subprocess
import ctypes
import shutil

def main():
    faulthandler.enable()
    SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
    BASE_DIR = os.path.abspath(os.path.join(SCRIPT_DIR, '..'))
    ProcessManager.kill_orphans()
    ProcessManager.cleanup_temp_files(min_age_seconds=300)
    if sys.platform.startswith("win") and os.environ.get("FVS_DEBUG_CONSOLE", "0") == "1":
        try:
            kernel32 = ctypes.windll.kernel32
            if kernel32.GetConsoleWindow() == 0:
                kernel32.AllocConsole()
                sys.stdout = open('CONOUT$', 'w')
                sys.stderr = open('CONOUT$', 'w')
            myappid = 'FortniteVideoTool.VideoMerger.1.0'
            ctypes.windll.shell32.SetCurrentProcessExplicitAppUserModelID(myappid)
        except Exception:
            pass
    app = QApplication(sys.argv)
    app.setStyle('Fusion')

    from system.recovery_manager import RecoveryManager
    recovery = RecoveryManager("video_merger", logger)
    if recovery.check_fault():
        msg_box = QMessageBox()
        msg_box.setIcon(QMessageBox.Question)
        msg_box.setWindowTitle("Video Merger")
        msg_box.setText("The application crashed last time. Would you like to restore your previous session?")
        msg_box.setStandardButtons(QMessageBox.Yes | QMessageBox.No)
        if msg_box.exec_() == QMessageBox.Yes:
            os.environ["FVS_RESTORE_SESSION"] = "1"
            recovery.activate_safe_mode()
        else:
            recovery.clear_state()
    recovery.acquire_lock()
    app.aboutToQuit.connect(recovery.cleanup_lock)
    logger.info("=== Video Merger Started ===")
    success, pid_handle = ProcessManager.acquire_pid_lock("fortnite_video_merger")
    if not success:
        QMessageBox.information(None, "Already Running", "Video Merger is already running.")
        sys.exit(0)
    is_valid_deps, ffmpeg_path, dep_error = DependencyDoctor.check_ffmpeg(BASE_DIR)
    if not is_valid_deps:
        msg = QMessageBox()
        msg.setIcon(QMessageBox.Critical)
        msg.setWindowTitle("Dependency Error")
        text = f"FFmpeg is missing: {dep_error}\nPlease run the Main App to diagnose."
        msg.setText(text)
        UIManager.style_and_size_msg_box(msg, text)
        msg.exec_()
        sys.exit(1)

    from utilities.merger_config import MergerConfigManager as ConfigManager
    from utilities.merger_window import VideoMergerWindow
    config_path = os.path.join(BASE_DIR, 'config', 'video_merger.conf')
    config_manager = ConfigManager(config_path)
    bin_dir = os.path.join(BASE_DIR, 'binaries')
    try:
        window = VideoMergerWindow(
            ffmpeg_path=ffmpeg_path,
            parent=None,
            mpv_instance=None,
            bin_dir=bin_dir,
            config_manager=config_manager,
            base_dir=BASE_DIR
        )
        window.show()
        window.activateWindow()
        window.raise_()

        def restart_main_app():
            main_app_path = os.path.join(BASE_DIR, 'app.py')
            try:
                flags = 16 if sys.platform == 'win32' else 0
                proc = subprocess.Popen([sys.executable, main_app_path], cwd=BASE_DIR, creationflags=flags, close_fds=True)
                window.hide()

                def _complete_main_handoff():
                    if proc.poll() is None:
                        window.close()
                        return
                    window.show()
                    QMessageBox.critical(window, "Launch Error", f"Main app closed unexpectedly (Code: {proc.returncode}).")
                QTimer.singleShot(900, _complete_main_handoff)
            except Exception as ex:
                window.show()
                QMessageBox.critical(window, "Launch Error", f"Could not launch Main App: {ex}")
        if hasattr(window, 'return_to_main'):
            window.return_to_main.connect(restart_main_app)
        exit_code = app.exec_()
        window.close()
        if pid_handle: pid_handle.close()
        sys.exit(exit_code)
    except Exception as e:
        error_details = traceback.format_exc()
        logger.critical(f"Unhandled exception in main loop: {e}", exc_info=True)
        msg = QMessageBox()
        msg.setIcon(QMessageBox.Critical)
        msg.setWindowTitle("Crash")
        msg.setText(f"An unexpected error occurred:\n{e}")
        UIManager.style_and_size_msg_box(msg, error_details)
        msg.exec_()
        if pid_handle: pid_handle.close()
        sys.exit(1)
if __name__ == "__main__":
    main()
