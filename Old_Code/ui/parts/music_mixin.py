import os
import sys
import subprocess
import tempfile
import time
import threading
from PyQt5.QtCore import Qt, QTimer, QRect, pyqtSignal, QObject
from PyQt5.QtGui import QPixmap, QPainter, QColor
from PyQt5.QtWidgets import (QStyleOptionSlider, QStyle, QDialog, QVBoxLayout,
                            QLabel, QHBoxLayout, QPushButton, QWidget, QSlider, QApplication, QMessageBox)

from ui.widgets.trimmed_slider import TrimmedSlider
from ui.styles import UIStyles
from system.utils import MPVSafetyManager, MediaProber

def _active_speed_segments(host):
    raw_segments = list(getattr(host, "speed_segments", []) or [])
    segments = []
    for seg in raw_segments:
        if not isinstance(seg, dict):
            continue
        try:
            start = int(seg.get("start", seg.get("start_ms", 0)))
            end = int(seg.get("end", seg.get("end_ms", 0)))
            speed = float(seg.get("speed", getattr(host, "playback_rate", 1.0)))
        except Exception:
            continue
        if end > start:
            segments.append({"start": start, "end": end, "start_ms": start, "end_ms": end, "speed": speed})
    if not segments:
        return []
    segments.sort(key=lambda item: (item["start"], item["end"]))
    return segments

class MusicMixin:
    def _set_music_button_state(self, has_music):
        btn = getattr(self, "music_button", None)
        if not btn:
            return
        if has_music:
            btn.setText('♪  REMOVE MUSIC  ♪')
            btn.setStyleSheet(UIStyles.BUTTON_DANGER + ' QPushButton { font-size: 10px; padding: 0px; }')
            btn.setToolTip("Click to completely remove all background music and settings")
        else:
            btn.setText('♪    ADD MUSIC    ♪')
            btn.setStyleSheet(UIStyles.BUTTON_WIZARD_BLUE + ' QPushButton { font-size: 10px; padding: 0px; }')
            btn.setToolTip("Open the background music synchronization wizard")

    def on_music_button_clicked(self):
        has_music = bool(getattr(self, "_wizard_tracks", []))
        if has_music:
            msg = QMessageBox(self)
            msg.setIcon(QMessageBox.Question)
            msg.setWindowTitle("Remove Music")
            msg.setText("Are you sure you want to remove all background music and settings?")
            msg.setStandardButtons(QMessageBox.Yes | QMessageBox.No)
            if msg.exec_() == QMessageBox.Yes:
                self._reset_music_player()
        else:
            self.open_music_wizard()

    def _mp3_dir(self):
        try:
            custom = self.config_manager.config.get('custom_mp3_dir')
            if custom and os.path.isdir(custom): return custom
        except: pass
        d = os.path.join(self.base_dir, "mp3")
        try: os.makedirs(d, exist_ok=True)
        except: pass
        return d

    def _scan_mp3_folder(self):
        try:
            d = self._mp3_dir(); files = []
            for name in os.listdir(d):
                if name.lower().endswith(".mp3"):
                    p = os.path.join(d, name)
                    try: mt = os.path.getmtime(p)
                    except: mt = 0
                    files.append((mt, name, p))
            files.sort(key=lambda x: x[0], reverse=True)
            self._music_files = [ (n, p) for _, n, p in files[:1000] ]
        except: self._music_files = []

    def _on_select_music_folder(self, wizard):
        from PyQt5.QtWidgets import QFileDialog
        folder = QFileDialog.getExistingDirectory(wizard, "Select Music Folder", self._mp3_dir())
        if folder:
            try:
                cfg = dict(self.config_manager.config); cfg['custom_mp3_dir'] = folder
                self.config_manager.save_config(cfg)
            except: pass
            wizard.mp3_dir = folder; wizard.load_tracks(folder); self._custom_mp3_dir = folder

    def _ensure_music_player_ready(self):
        try:
            if not getattr(self, "_music_preview_player", None):
                kwargs = {'vid': 'no', 'vo': 'null', 'osc': False, 'input_default_bindings': False, 'hr_seek': 'yes', 'hwdec': 'auto', 'keep_open': 'yes', 'loglevel': "info", 'ytdl': False, 'demuxer_max_bytes': '300M', 'demuxer_max_back_bytes': '60M'}
                if sys.platform == 'win32': kwargs['ao'] = 'wasapi'
                self._music_preview_player = MPVSafetyManager.create_safe_mpv(**kwargs)
            return True if self._music_preview_player else False
        except: return False

    def open_music_wizard(self):
        self._in_transition = True
        if hasattr(self, 'music_button'):
            self.music_button.setEnabled(False)
            self.music_button.setCursor(Qt.PointingHandCursor)
        if hasattr(self, 'set_overlays_force_hidden'): self.set_overlays_force_hidden(True)
        if hasattr(self, 'timeline_overlay') and self.timeline_overlay: self.timeline_overlay.hide()
        self._pre_wizard_state = {}
        if hasattr(self, "player") and self.player:
            try:
                self._pre_wizard_state['player_playing'] = not getattr(self.player, "pause", True)
                self._pre_wizard_state['player_mute'] = getattr(self.player, "mute", False)
                self.player.pause = True; self.player.mute = True
                if getattr(self, "_music_preview_player", None): self._music_preview_player.pause = True
            except: pass
        self.wants_to_play = False
        if hasattr(self, 'playPauseButton'):
            self.playPauseButton.setText("PLAY")
            self.playPauseButton.setIcon(self.style().standardIcon(QStyle.SP_MediaPlay))
        if hasattr(self, "positionSlider"):
            self.positionSlider.set_trim_times(self.trim_start_ms, self.trim_end_ms)
        if hasattr(self, 'timer') and self.timer.isActive(): self.timer.stop()
        QTimer.singleShot(150, self._delayed_wizard_launch)

    def _delayed_wizard_launch(self):
        from ui.widgets.music_wizard import MergerMusicWizard
        t_s = getattr(self, "trim_start_ms", 0); t_e = getattr(self, "trim_end_ms", 0)
        if t_e <= t_s: t_s = 0; t_e = getattr(self, "original_duration_ms", 0)
        sp = self.speed_spinbox.value() if hasattr(self, 'speed_spinbox') else 1.1
        speed_segments = _active_speed_segments(self)
        try:
            if speed_segments:
                w_s = self._calculate_wall_clock_time(t_s, speed_segments, sp)
                w_e = self._calculate_wall_clock_time(t_e, speed_segments, sp)
                t_p_s = (w_e - w_s) / 1000.0
            else: t_p_s = ((t_e - t_s) / 1000.0) / sp
        except: t_p_s = 0.0
        if t_p_s <= 0:
            QMessageBox.warning(self, "No Video", "Please load a video file first!")
            if hasattr(self, 'music_button'): self.music_button.setEnabled(True)
            if hasattr(self, 'set_overlays_force_hidden'): self.set_overlays_force_hidden(False)
            self._in_transition = False
            if hasattr(self, "_update_overlay_positions"): QTimer.singleShot(0, self._update_overlay_positions)
            self._restore_pre_wizard_state(); return
        m_d = self._mp3_dir(); c_s_ms = self.positionSlider.value() if hasattr(self, "positionSlider") else 0
        try:
            if speed_segments:
                w_s = self._calculate_wall_clock_time(t_s, speed_segments, sp)
                w_c = self._calculate_wall_clock_time(c_s_ms, speed_segments, sp)
                c_p_s = max(0.0, (w_c - w_s) / 1000.0)
            else: c_p_s = max(0.0, ((c_s_ms - t_s) / 1000.0) / sp)
        except: c_p_s = 0.0
        wizard = MergerMusicWizard(self, self.player, self.bin_dir, m_d, t_p_s, sp, trim_start_ms=t_s, trim_end_ms=t_e, speed_segments=speed_segments, initial_project_sec=c_p_s)
        curr_eff = self._get_master_eff()
        wizard.video_vol_slider.blockSignals(True); wizard.video_vol_slider.setValue(curr_eff); wizard.video_vol_slider.blockSignals(False)
        if hasattr(self, "_music_volume_pct"): 
            wizard.music_vol_slider.blockSignals(True); wizard.music_vol_slider.setValue(int(self._music_volume_pct)); wizard.music_vol_slider.blockSignals(False)
        if hasattr(self, "_wizard_tracks") and self._wizard_tracks: wizard.selected_tracks = list(self._wizard_tracks)
        res = wizard.exec_(); self._in_transition = True
        if hasattr(wizard, 'stop_previews'): wizard.stop_previews()
        QTimer.singleShot(100, lambda: self._complete_wizard_return(res, wizard, t_s, t_e, speed_segments, sp))

    def _complete_wizard_return(self, res, wizard, t_s, t_e, segs, sp):
        try:
            if hasattr(wizard, '_release_player'): wizard._release_player()
            QApplication.processEvents(); self._continue_wizard_return(res, t_s, t_e, segs, sp, wizard)
        except: pass
        finally: QTimer.singleShot(200, self._set_transition_false)

    def _continue_wizard_return(self, res, t_s, t_e, segs, sp, wizard):
        try:
            if hasattr(self, 'music_button'):
                self.music_button.setEnabled(True)
                self.music_button.setCursor(Qt.PointingHandCursor)
            if hasattr(self, 'set_overlays_force_hidden'): self.set_overlays_force_hidden(False)
            if hasattr(self, "_update_overlay_positions"): QTimer.singleShot(0, self._update_overlay_positions)
            self.wants_to_play = False; self._in_transition = False; self.raise_(); self.activateWindow()
            if hasattr(self, "video_surface"): self.video_surface.show()
            if hasattr(self, "player") and self.player:
                self._safe_mpv_set("pause", True); self._safe_mpv_set("mute", False) 
            if hasattr(self, "_bind_main_player_output"): self._bind_main_player_output()
            if res == QDialog.Accepted:
                self._wizard_tracks = list(getattr(wizard, 'selected_tracks', []))
                if self._wizard_tracks:
                    self._music_volume_pct = getattr(wizard, 'music_vol_slider', None).value() if hasattr(wizard, 'music_vol_slider') else 80
                    self._video_volume_pct = getattr(wizard, 'video_vol_slider', None).value() if hasattr(wizard, 'video_vol_slider') else 80
                    if self._ensure_music_player_ready():
                        f_t = self._wizard_tracks[0]; self._current_music_path = f_t[0]; self._current_music_offset = f_t[1]
                        self._safe_mpv_command("loadfile", self._current_music_path, "replace", target_player=self._music_preview_player)
                        self._safe_mpv_set("pause", True, target_player=self._music_preview_player)
                        self._safe_mpv_set("mute", False, target_player=self._music_preview_player)
                        self._safe_mpv_set("volume", self._music_volume_pct, target_player=self._music_preview_player)
                    if hasattr(self, "positionSlider"):
                        self.positionSlider.set_music_visible(True); self.positionSlider.set_music_times(t_s, t_e)
                        self.music_timeline_start_ms = t_s; self.music_timeline_end_ms = t_e
                    self._set_music_button_state(True)
                    QTimer.singleShot(50, self._sync_music_preview)
                    QTimer.singleShot(150, lambda: self._safe_seek_to_start(t_s))
                    if hasattr(self, 'timer'): self.timer.start(100)
                    if hasattr(self, "_save_recovery_state"): self._save_recovery_state()
                else: self._reset_music_player()
                QTimer.singleShot(500, self._final_unmute_after_wizard)
            else:
                self._restore_pre_wizard_state()
        except: pass
        finally:
            if hasattr(self, 'set_overlays_force_hidden'): self.set_overlays_force_hidden(False)
            if hasattr(self, "_update_overlay_positions"): QTimer.singleShot(0, self._update_overlay_positions)
            self._in_transition = False

    def _final_unmute_after_wizard(self):
        if hasattr(self, "_sync_all_volumes"): self._sync_all_volumes()
        elif getattr(self, "player", None):
            self._safe_mpv_set("mute", False)
            if hasattr(self, "volume_slider"): self._safe_mpv_set("volume", self.volume_slider.value())
        m_p = getattr(self, "_music_preview_player", None)
        if m_p:
            self._safe_mpv_set("mute", False, target_player=m_p)
            self._safe_mpv_set("volume", self._music_eff(), target_player=m_p)

    def _safe_mpv_set(self, prop, val, target_player=None):
        p = target_player if target_player is not None else getattr(self, "player", None)
        if p: return MPVSafetyManager.safe_mpv_set(p, prop, val)
        return False

    def _safe_mpv_command(self, cmd, *args, target_player=None):
        p = target_player if target_player is not None else getattr(self, "player", None)
        if p: return MPVSafetyManager.safe_mpv_command(p, cmd, *args)
        return False

    def _safe_seek_to_start(self, s_ms):
        try:
            if getattr(self, "player", None): self.set_player_position(int(s_ms), sync_only=False, force_pause=True)
        except: pass

    def _set_transition_false(self): self._in_transition = False

    def _restore_pre_wizard_state(self):
        if not hasattr(self, '_pre_wizard_state'): return
        try:
            if getattr(self, "player", None):
                if 'player_mute' in self._pre_wizard_state: self._safe_mpv_set("mute", self._pre_wizard_state['player_mute'])
                if 'player_playing' in self._pre_wizard_state and self._pre_wizard_state['player_playing']:
                    self._safe_mpv_set("pause", False); self.wants_to_play = True
                    if getattr(self, "_music_preview_player", None) and getattr(self, "_wizard_tracks", None):
                        self._safe_mpv_set("pause", False, target_player=self._music_preview_player)
                    if hasattr(self, 'timer') and not self.timer.isActive(): self.timer.start(100)
                    if hasattr(self, 'playPauseButton'):
                        self.playPauseButton.setText("PAUSE")
                        self.playPauseButton.setIcon(self.style().standardIcon(QStyle.SP_MediaPause))
        except: pass
        finally:
            if hasattr(self, '_pre_wizard_state'): del self._pre_wizard_state

    def _reset_music_player(self):
        self.music_timeline_start_ms = 0; self.music_timeline_end_ms = 0; self._wizard_tracks = []
        self._current_music_path = None; self._current_music_offset = 0.0
        if getattr(self, "_music_preview_player", None): self._safe_mpv_set("pause", True, target_player=self._music_preview_player)
        if hasattr(self, "positionSlider"): self.positionSlider.reset_music_times()
        self._set_music_button_state(False)
        if hasattr(self, "_save_recovery_state"): self._save_recovery_state()

    def _get_master_eff(self):
        try:
            val = self.volume_slider.value()
            if self.volume_slider.invertedAppearance(): return self.volume_slider.maximum() + self.volume_slider.minimum() - val
            return val
        except: return 100

    def _on_slider_music_trim_changed(self, s_ms, e_ms):
        try:
            self.music_timeline_start_ms = s_ms; self.music_timeline_end_ms = e_ms
            if hasattr(self, "_wizard_tracks") and len(self._wizard_tracks) == 1:
                p, o, _ = self._wizard_tracks[0]; n_d = (e_ms - s_ms) / 1000.0; self._wizard_tracks[0] = (p, o, n_d)
            if hasattr(self, "_save_recovery_state"): self._save_recovery_state()
        except: pass

    def _sync_music_preview(self):
        if getattr(self, "_in_transition", False) or getattr(self, "_is_seeking_active", False) or not hasattr(self, "_wizard_tracks") or not self._wizard_tracks or not getattr(self, "player", None): return
        if not self._mpv_lock.acquire(timeout=0.02): return
        try:
            c_v_ms = (self._safe_mpv_get("time-pos", 0) or 0) * 1000
            m_s_ms = getattr(self, "music_timeline_start_ms", getattr(self, "trim_start_ms", 0))
            m_e_ms = getattr(self, "music_timeline_end_ms", getattr(self, "trim_end_ms", 0))
            sp = self.speed_spinbox.value() if hasattr(self, 'speed_spinbox') else 1.1
            segs = _active_speed_segments(self)
            w_n = self._calculate_wall_clock_time(c_v_ms, segs, sp); w_m_s = self._calculate_wall_clock_time(m_s_ms, segs, sp)
            p_p_s = (w_n - w_m_s) / 1000.0; m_p = getattr(self, "_music_preview_player", None)
            if not m_p: 
                if not self._ensure_music_player_ready(): return
                m_p = self._music_preview_player
            if c_v_ms < m_s_ms - 25 or c_v_ms > m_e_ms + 25:
                self._safe_mpv_set("pause", True, target_player=m_p); self._safe_mpv_set("mute", True, target_player=m_p); return
            t_t, acc = None, 0.0
            for path, off, dur in self._wizard_tracks:
                if acc <= p_p_s < acc + dur:
                    t_t = (path, off, p_p_s - acc); break
                acc += dur
            if not t_t: self._safe_mpv_set("pause", True, target_player=m_p); return
            p, o, p_i_t = t_t; c_l = self._safe_mpv_get("path", "", target_player=m_p)
            if not c_l or os.path.basename(c_l).lower() != os.path.basename(p).lower():
                self._safe_mpv_command("loadfile", p, "replace", target_player=m_p)
                self._safe_mpv_set("pause", True, target_player=m_p); self._safe_mpv_set("mute", False, target_player=m_p)
                self._safe_mpv_set("volume", self._music_eff(), target_player=m_p); return
            m_pos = self._safe_mpv_get("time-pos", 0, target_player=m_p) or 0; e_m_s = o + p_i_t
            if abs(m_pos - e_m_s) > 0.05: 
                self._safe_mpv_set("speed", 1.0, target_player=m_p); self._safe_mpv_command("seek", e_m_s, "absolute", "exact", target_player=m_p)
            v_p = self._safe_mpv_get("pause", True); self._safe_mpv_set("pause", v_p, target_player=m_p)
            self._safe_mpv_set("volume", self._music_eff(), target_player=m_p); self._safe_mpv_set("mute", False, target_player=m_p)
        except: pass
        finally: self._mpv_lock.release()

    def _probe_audio_duration_async(self, path, callback):
        def _worker():
            try:
                res = MediaProber.probe_duration(self.bin_dir, path)
                callback(res)
            except: callback(0.0)
        threading.Thread(target=_worker, daemon=True).start()
