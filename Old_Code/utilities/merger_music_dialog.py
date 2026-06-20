import os
from PyQt5.QtWidgets import QDialog, QApplication, QMessageBox
from PyQt5.QtCore import Qt
from utilities.merger_music_offset_dialog import MergerMusicOffsetDialog
try:
    import mpv
except Exception:
    mpv = None

class MusicDialogHandler:
    def __init__(self, parent):
        self.parent = parent
        self.logger = parent.logger

    def _ensure_mpv_instance(self) -> bool:
        if getattr(self.parent, "player", None) is not None:
            return True
        if mpv is None:
            return False

        from system.utils import MPVSafetyManager
        try:
            bin_dir = getattr(self.parent, "bin_dir", "") or ""
            if bin_dir and os.path.isdir(bin_dir):
                os.environ["PATH"] += os.pathsep + os.path.abspath(bin_dir)
            self.parent.player = MPVSafetyManager.create_safe_mpv(hr_seek='yes', hwdec='auto', keep_open='yes')
            return self.parent.player is not None
        except Exception:
            self.parent.player = None
            return False

    def _on_select_music_folder(self, wizard):
        from PyQt5.QtWidgets import QFileDialog
        curr_dir = getattr(wizard, "mp3_dir", os.path.join(self.parent.base_dir, "mp3"))
        folder = QFileDialog.getExistingDirectory(wizard, "Select Music Folder", curr_dir)
        if folder:
            self.logger.info(f"WIZARD: User changed music folder to: {folder}")
            try:
                cfg = dict(self.parent.config_manager.config)
                cfg['custom_mp3_dir'] = folder
                self.parent.config_manager.save_config(cfg)
            except: pass
            wizard.mp3_dir = folder
            wizard.load_tracks(folder)

    def open_music_wizard(self):
        from utilities.merger_music_wizard import MergerMusicWizard
        if not self._ensure_mpv_instance():
            self.logger.warning("WIZARD: MPV initialization failed. Preview will be unavailable.")
        total_sec = self.parent.estimate_total_duration_seconds()
        if total_sec <= 0:
            QMessageBox.warning(self.parent, "No Videos", "Please add at least one video first!")
            return
        mp3_dir = self.parent.config_manager.config.get('custom_mp3_dir')
        if not mp3_dir or not os.path.isdir(mp3_dir):
            mp3_dir = os.path.join(self.parent.base_dir, "mp3")
        wizard = MergerMusicWizard(
            self.parent, self.parent.player, self.parent.bin_dir, mp3_dir, 
            total_sec, speed_factor=1.0, initial_project_sec=0.0
        )
        if hasattr(self.parent, "unified_music_widget"):
            wizard.video_vol_slider.blockSignals(True)
            wizard.video_vol_slider.setValue(self.parent.unified_music_widget.get_video_volume())
            wizard.video_vol_slider.blockSignals(False)
            wizard.music_vol_slider.blockSignals(True)
            wizard.music_vol_slider.setValue(self.parent.unified_music_widget.get_volume())
            wizard.music_vol_slider.blockSignals(False)
            wizard.selected_tracks = list(self.parent.unified_music_widget.get_wizard_tracks())
        if wizard.exec_() == QDialog.Accepted:
            if hasattr(self.parent, "unified_music_widget"):
                m_vol = wizard.music_vol_slider.value()
                v_vol = wizard.video_vol_slider.value()
                self.parent.unified_music_widget.set_wizard_tracks(wizard.selected_tracks, music_vol=m_vol, video_vol=v_vol)
                self.parent.set_status_message("✅ Music selection applied!", "color: #43b581; font-weight: bold;", 3000, force=True)
        if hasattr(wizard, "stop_previews"):
            wizard.stop_previews()
        if hasattr(wizard, "_release_player"):
            wizard._release_player()

    def show_music_offset_dialog(self, path):
        if not self._ensure_mpv_instance():
            QMessageBox.warning(self.parent, "Preview unavailable", "Could not initialize MPV audio preview engine.")
        initial_offset = self.parent.unified_music_widget.get_offset()
        dlg = MergerMusicOffsetDialog(self.parent, self.parent.player, path, initial_offset, self.parent.bin_dir)
        dlg.setWindowModality(Qt.ApplicationModal)
        saved_geo = self.parent.config_manager.config.get("music_dialog_geometry")
        if saved_geo and len(saved_geo) == 4:
            try: dlg.setGeometry(*saved_geo)
            except Exception: pass
        if dlg.exec_() == QDialog.Accepted:
            try:
                self.parent.unified_music_widget.set_primary_offset(float(dlg.selected_offset))
            except Exception as ex:
                self.logger.error(f"Failed applying selected offset: {ex}")
        g = dlg.geometry()
        cfg = dict(self.parent.config_manager.config)
        cfg["music_dialog_geometry"] = [g.x(), g.y(), g.width(), g.height()]
        self.parent.config_manager.save_config(cfg)
