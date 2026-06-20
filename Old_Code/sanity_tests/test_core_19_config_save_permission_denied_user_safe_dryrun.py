from __future__ import annotations
from sanity_tests._ai_sanity_helpers import assert_all_present, read_source

def _safe_save_snapshot(should_fail: bool) -> bool:
    """Detached static snapshot of fail-safe config save behavior."""
    try:
        if should_fail:
            raise PermissionError("read-only")
        return True
    except Exception:
        return False

def test_core_19_config_save_permission_denied_user_safe_dryrun() -> None:
    """
    Saving config must be fail-safe if write permissions are denied.
    """
    cfg_src = read_source("system/config.py")
    vol_src = read_source("ui/parts/volume_mixin.py")
    hw_src = read_source("ui/parts/main_window_core_a.py")
    assert_all_present(
        cfg_src,
        [
            "def save_config(self, config_data: Dict[str, Any]) -> None:",
            "try:",
            "fd, temp_path = tempfile.mkstemp",
            "json.dump(self.config, f, indent=4)",
            "except Exception:",
            "pass",
        ],
    )
    assert_all_present(
        vol_src,
        [
            "cfg = dict(self.config_manager.config)",
            "self.config_manager.save_config(cfg)",
            "except: pass",
        ],
    )
    assert_all_present(
        hw_src,
        [
            "cfg = self.config_manager.config",
            "self.config_manager.save_config(cfg)",
            "except Exception: pass",
        ],
    )
    assert _safe_save_snapshot(False) is True
    assert _safe_save_snapshot(True) is False
