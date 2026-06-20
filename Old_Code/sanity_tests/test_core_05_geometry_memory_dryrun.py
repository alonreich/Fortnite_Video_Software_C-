from sanity_tests._ai_sanity_helpers import assert_all_present, read_source

def test_core_05_geometry_memory_dryrun() -> None:
    src = read_source("ui/widgets/music_wizard_misc.py")
    assert_all_present(
        src,
        [
            "def moveEvent(self, event):",
            "def resizeEvent(self, event):",
            "self._save_step_geometry()",
            "cfg[\"music_wizard_custom_geo\"][f\"step_{step_idx}\"] = {",
            "'x': geom.x(), 'y': geom.y(), 'w': geom.width(), 'h': geom.height()",
        ],
    )
