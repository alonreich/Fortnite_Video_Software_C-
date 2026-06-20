import os
import json
from .hud_config import DEFAULT_HUD_CONFIG, sanitize_hud_config

class VideoConfig:
    def __init__(self, base_dir):
        self.base_dir = base_dir
        self.bin_dir = os.path.join(self.base_dir, 'binaries')
        self.default_main_width = 1280
        self.default_main_height = 1920
        self.mobile_main_width = 1080
        self.mobile_main_height = 1920
        self.fade_duration = 1.0
        self.wrap_at_px = 1100
        self.safe_max_px = 1200
        self.base_font_size = 110
        self.min_font_size = 36
        self.line_spacing = -10
        self.measure_fudge = 1.12
        self.shadow_pad_px = 5

    def _rotate_backups(self, conf_path):
        try:
            for i in range(4, 0, -1):
                old_b = f"{conf_path}.bak{i}"
                new_b = f"{conf_path}.bak{i+1}"
                if os.path.exists(old_b):
                    try:
                        if os.path.exists(new_b): os.remove(new_b)
                        os.rename(old_b, new_b)
                    except OSError as e:
                        if hasattr(self, "logger"):
                            self.logger.warning(f"Backup rotation skipped for {old_b}: {e}")
            if os.path.exists(conf_path):
                try:
                    target = f"{conf_path}.bak1"
                    if os.path.exists(target): os.remove(target)
                    os.rename(conf_path, target)
                except OSError as e:
                    if hasattr(self, "logger"):
                        self.logger.warning(f"Primary config backup rotation skipped: {e}")
        except OSError as e:
            if hasattr(self, "logger"):
                self.logger.warning(f"Backup rotation failed for {conf_path}: {e}")

    def get_mobile_coordinates(self, logger=None):
        conf_dir = os.path.join(self.base_dir, 'processing')
        conf_path = os.path.join(conf_dir, 'crops_coordinations.conf')
        default_conf_data = DEFAULT_HUD_CONFIG

        def _try_load(path):
            try:
                with open(path, 'r', encoding='utf-8') as f:
                    loaded_data = json.load(f)
                if not isinstance(loaded_data, dict):
                    return None
                return sanitize_hud_config(loaded_data)
            except (json.JSONDecodeError, OSError, TypeError):
                return None
        if not os.path.exists(conf_path):
            for i in range(1, 6):
                bak = f"{conf_path}.bak{i}"
                if os.path.exists(bak):
                    data = _try_load(bak)
                    if data:
                        if logger: logger.info(f"Recovered config from backup {bak}")
                        with open(conf_path, 'w', encoding='utf-8') as f:
                            json.dump(data, f, indent=4)
                        return data
            if logger: logger.info(f"Config missing at {conf_path}, creating with defaults.")
            try:
                os.makedirs(conf_dir, exist_ok=True)
                with open(conf_path, 'w', encoding='utf-8') as f:
                    json.dump(default_conf_data, f, indent=4)
            except Exception as e:
                if logger: logger.error(f"Failed to create default config: {e}")
                return default_conf_data
        for _ in range(3):
            loaded_data = _try_load(conf_path)
            if loaded_data:
                return loaded_data

            import time
            time.sleep(0.1)
        if logger: logger.warning(f"Config validation failed at {conf_path}. Overwriting with defaults.")
        self._rotate_backups(conf_path)
        with open(conf_path, 'w', encoding='utf-8') as f:
            json.dump(default_conf_data, f, indent=4)
        return default_conf_data

    def get_quality_settings(self, quality_level, target_mb_override=None):
        try:
            q = int(quality_level)
        except Exception:
            q = 2
        keep_highest_res = False
        target_mb = None
        if q >= 20:
            keep_highest_res = True
            if target_mb_override is not None:
                target_mb = float(target_mb_override)
        else:
            keep_highest_res = False
            if target_mb_override is not None:
                target_mb = float(target_mb_override)
            else:
                q = max(0, min(19, q))
                target_mb = float(5 + q * 5)
        return keep_highest_res, target_mb, q
