from __future__ import annotations
from sanity_tests._ai_sanity_helpers import assert_all_present, read_source

def test_integration_10_crop_config_atomic_write_contract() -> None:
    """Config manager should save via temp file + replace + bounded backup pruning."""
    src = read_source("developer_tools/config_manager.py")
    assert_all_present(
        src,
        [
            "temp_fd, temp_path = tempfile.mkstemp(",
            "shutil.copy2(self.config_path, backup_path)",
            "os.replace(temp_path, self.config_path)",
            "self._prune_backup_files(max_backups=5)",
        ],
    )
