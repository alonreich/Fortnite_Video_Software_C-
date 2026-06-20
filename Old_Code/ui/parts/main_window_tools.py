import os, sys, subprocess
from PyQt5.QtCore import *
from PyQt5.QtGui import *
from PyQt5.QtWidgets import *

class MainWindowToolsMixin:
    def _watch_child_tool(self, proc, title):
        if proc.poll() is None:
            QTimer.singleShot(1000, lambda: self._watch_child_tool(proc, title))
            return
        code = proc.returncode
        if code == 0:
            self.close()
            return
        self.show()
        QMessageBox.critical(self, "Launch Failed", f"{title} closed unexpectedly (Code: {code}).")

    def launch_crop_tool(self):
        try:
            self.hide()
            root_dir = os.path.abspath(self.base_dir); script_path = os.path.join(root_dir, 'developer_tools', 'crop_tools.py')
            if not os.path.exists(script_path): raise FileNotFoundError(f"Crop Tool script not found: {script_path}")
            state = {"input_file": self.input_file_path, "source_file": getattr(self, "source_file_path", None), "trim_start": self.trim_start_ms, "trim_end": self.trim_end_ms, "speed_segments": self.speed_segments, "granular_checked": bool(getattr(self, "granular_checkbox", None) and self.granular_checkbox.isChecked()), "hardware_mode": getattr(self, "hardware_strategy", "CPU"), "resolution": getattr(self, "original_resolution", None)}

            from system.state_transfer import StateTransfer
            StateTransfer.save_state(state)
            if self.player: self.player.stop()
            env = os.environ.copy(); env["PYTHONPATH"] = os.pathsep.join(filter(None, [os.path.join(root_dir, 'developer_tools'), root_dir, env.get("PYTHONPATH", "")]))
            cmd = [sys.executable, "-B", script_path]
            if self.input_file_path: cmd.append(self.input_file_path)
            creation_flags = 0
            if sys.platform == "win32": creation_flags = 0x00000008 | 0x00000200
            self._preserve_staged_input_on_close = True
            proc = subprocess.Popen(cmd, cwd=os.path.join(root_dir, 'developer_tools'), env=env, creationflags=creation_flags, close_fds=True, shell=False)
            QTimer.singleShot(800, lambda: self._watch_child_tool(proc, "Crop Tool"))
        except Exception as e:
            self.show(); QMessageBox.critical(self, "Launch Failed", f"Could not launch Crop Tool: {e}")

    def launch_advanced_editor(self):
        script_path = os.path.join(self.base_dir, 'advanced', 'advanced_video_editor.py')
        if not os.path.exists(script_path):
            QMessageBox.information(
                self,
                "Advanced Editor Unavailable",
                "The Advanced Video Editor is not bundled with this build.\n\n"
                f"Expected at: {script_path}",
            )
            return
        try:
            from system.state_transfer import StateTransfer
            state = {"input_file": self.input_file_path, "source_file": getattr(self, "source_file_path", None), "hardware_mode": getattr(self, "hardware_strategy", "CPU")}; StateTransfer.save_state(state)
            command = [sys.executable, script_path]
            if self.input_file_path: command.append(self.input_file_path)
            if self.player: self.player.stop()
            self.hide()
            self._preserve_staged_input_on_close = True
            proc = subprocess.Popen(command, cwd=os.path.dirname(script_path), close_fds=True, shell=False)
            QTimer.singleShot(800, lambda: self._watch_child_tool(proc, "Advanced Editor"))
        except Exception as e:
            self.show(); QMessageBox.critical(self, "Launch Failed", f"Could not launch Advanced Editor: {e}")
