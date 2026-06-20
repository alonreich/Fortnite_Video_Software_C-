from sanity_tests._ai_sanity_helpers import assert_all_present, read_source

def test_challenge_10_multi_instance_config_dryrun() -> None:
    src_config = read_source("system/config.py")
    assert_all_present(
        src_config,
        [
            "self.config = self.load_config()",
            "def load_config(self) -> Dict[str, Any]:",
        ],
    )
    src_music = read_source("ui/parts/music_mixin.py")
    assert_all_present(
        src_music,
        [
            "cfg = dict(self.config_manager.config)",
            "cfg['custom_mp3_dir'] = folder",
            "self.config_manager.save_config(cfg)",
        ],
    )
