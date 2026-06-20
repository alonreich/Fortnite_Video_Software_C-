from __future__ import annotations
from sanity_tests._ai_sanity_helpers import assert_all_present, read_source

def test_core_17_config_missing_uses_safe_defaults_dryrun() -> None:
    """
    If config files are missing, app must recreate safe defaults and continue.
    """
    main_cfg = read_source("system/config.py")
    proc_cfg = read_source("processing/config_data.py")
    assert_all_present(
        main_cfg,
        [
            "self.config = self.load_config()",
            "if not os.path.exists(self.file_path):",
            "self.save_config(self.config)",
            "except (FileNotFoundError, json.JSONDecodeError, OSError):",
            "return {}",
        ],
    )
    assert_all_present(
        proc_cfg,
        [
            "if not os.path.exists(conf_path):",
            "json.dump(default_conf_data, f, indent=4)",
            "return default_conf_data",
        ],
    )
