from PyQt5.QtCore import QTimer, QPoint, Qt
from PyQt5.QtWidgets import QStyleOptionSlider, QStyle
import threading

class VolumeMixin:
    def _vol_eff(self, raw: int | None = None) -> int:
        if not hasattr(self, "volume_slider"):
            return 100
        v = int(self.volume_slider.value() if raw is None else raw)
        if self.volume_slider.invertedAppearance():
            return max(0, min(100, self.volume_slider.maximum() + self.volume_slider.minimum() - v))
        return max(0, min(100, v))

    def _music_eff(self) -> int:
        if not hasattr(self, "_music_volume_pct"):
            try:
                self._music_volume_pct = int(self.config_manager.config.get('music_mix_volume', 80))
            except:
                self._music_volume_pct = 80
        return int(self._music_volume_pct)

    def _sync_all_volumes(self):
        if bool(getattr(self, "_suspend_volume_sync", False)) or getattr(self, "_in_transition", False):
            return
        v_eff = self._vol_eff()
        m_eff = self._music_eff()
        if hasattr(self, "_safe_mpv_set"):
            self._safe_mpv_set("volume", v_eff)
            self._safe_mpv_set("mute", False)
        else:
            player = getattr(self, "player", None)
            if player:
                try:
                    player.volume = v_eff
                    player.mute = False
                except Exception:
                    pass
        music_player = getattr(self, "_music_preview_player", None)
        if music_player:
            try:
                music_player.volume = m_eff
                music_player.mute = False
                if hasattr(self, "logger"):
                    self.logger.info(f"HARDWARE_SET: [MUSIC_PREVIEW] Volume -> {m_eff}%")
            except Exception:
                pass

    def _schedule_volume_reinforce(self, delay_ms: int = 350):
        if bool(getattr(self, "_suspend_volume_sync", False)):
            return
        try:
            timer = getattr(self, "_volume_reinforce_timer", None)
            if timer is None:
                timer = QTimer(self)
                timer.setSingleShot(True)
                timer.timeout.connect(self._sync_all_volumes)
                self._volume_reinforce_timer = timer
            timer.start(max(60, int(delay_ms)))
        except Exception:
            pass

    def _update_volume_badge(self):
        if not hasattr(self, "volume_badge") or not hasattr(self, "volume_slider"):
            return
        try:
            raw = int(self.volume_slider.value())
            eff = self._vol_eff(raw)
            self.volume_badge.setText(f"{eff}%")
            self.volume_badge.adjustSize()
            self.volume_badge.show()
        except Exception as e:
            if hasattr(self, "logger"):
                self.logger.error(f"Volume Badge Error: {e}")

    def apply_master_volume(self):
        self._sync_all_volumes()
        self._update_volume_badge()
        self._schedule_volume_reinforce(350)

    def _on_master_volume_changed(self, v: int):
        self._sync_all_volumes()
        eff_pct = self._vol_eff(v)
        if hasattr(self, "config_manager"):
            try:
                cfg = dict(self.config_manager.config)
                cfg['video_mix_volume'] = eff_pct
                cfg['last_volume'] = eff_pct
                self.config_manager.save_config(cfg)
            except: pass
        self._update_volume_badge()
        self._schedule_volume_reinforce(250)
        if hasattr(self, "_save_recovery_state"):
            self._save_recovery_state()
