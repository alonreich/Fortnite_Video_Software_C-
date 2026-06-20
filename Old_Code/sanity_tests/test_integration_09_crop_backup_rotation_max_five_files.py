from __future__ import annotations
from sanity_tests._ai_sanity_helpers import assert_all_present, read_source

def test_integration_09_crop_backup_rotation_max_five_files() -> None:
    """Crop save worker should rotate five numbered backups beside the config file."""
    src = read_source("developer_tools/crop_tools.py")
    assert_all_present(
        src,
        [
            "def _create_rotation_backup(self):",
            "for i in range(4, 0, -1):",
            'old_b = f"{conf_path}.bak{i}"',
            'new_b = f"{conf_path}.bak{i+1}"',
            'shutil.copy2(conf_path, f"{conf_path}.bak1")',
        ],
    )
