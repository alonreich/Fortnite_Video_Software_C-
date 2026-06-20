import os
import json
import logging
from utilities.merger_utils import sanitize_persistent_config
logger = logging.getLogger("Video_Merger")

class MergerConfigManager:
    def __init__(self, config_path):
        self.config_path = config_path
        self.config = self.load_config()

    def load_config(self):
        if os.path.exists(self.config_path):
            try:
                with open(self.config_path, 'r', encoding='utf-8') as f:
                    data = json.load(f)
                    if isinstance(data, dict):
                        return sanitize_persistent_config(data)
            except (json.JSONDecodeError, PermissionError, OSError):
                pass
        return {}

    def save_config(self, config=None):
        if config is not None:
            self.config = sanitize_persistent_config(config)
        else:
            self.config = sanitize_persistent_config(self.config)
        os.makedirs(os.path.dirname(self.config_path), exist_ok=True)
        try:
            with open(self.config_path, 'w', encoding='utf-8') as f:
                json.dump(self.config, f, indent=4)
        except Exception as ex:
            logger.error("CONFIG: Failed saving config to %s | Error: %s", self.config_path, ex)
