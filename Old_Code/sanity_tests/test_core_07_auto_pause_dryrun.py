from sanity_tests._ai_sanity_helpers import assert_all_present, read_source

def test_core_07_auto_pause_dryrun() -> None:
    src = read_source("ui/parts/music_mixin.py")
    assert_all_present(
        src,
        [
            "def open_music_wizard(self):",
            "self.player.pause = True",
            "self.player.mute = True",
            "self.wants_to_play = False",
        ],
    )
