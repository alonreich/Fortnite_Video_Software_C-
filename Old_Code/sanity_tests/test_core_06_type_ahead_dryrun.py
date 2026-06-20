from sanity_tests._ai_sanity_helpers import assert_all_present, read_source

def test_core_06_type_ahead_dryrun() -> None:
    src = read_source("ui/widgets/music_wizard_widgets.py")
    assert_all_present(
        src,
        [
            "self._buffer_timer.setInterval(1500)",
            "self._search_buffer += text.lower()",
            "if clean_text.startswith(self._search_buffer):",
        ],
    )
