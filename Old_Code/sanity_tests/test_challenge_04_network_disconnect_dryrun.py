from sanity_tests._ai_sanity_helpers import assert_all_present, read_source

def test_challenge_04_network_disconnect_dryrun() -> None:
    src = read_source("ui/parts/music_mixin.py")
    assert_all_present(
        src,
        [
            "custom = self.config_manager.config.get('custom_mp3_dir')",
            "if custom and os.path.isdir(custom):",
            "d = os.path.join(self.base_dir, \"mp3\")",
            "os.makedirs(d, exist_ok=True)",
        ],
    )
