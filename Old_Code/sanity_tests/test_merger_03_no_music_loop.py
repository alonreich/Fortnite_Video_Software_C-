from __future__ import annotations
from sanity_tests._scenario_contracts import assert_merger_no_music_loop_contract

def test_no_music_loop_on_short_track():
    assert_merger_no_music_loop_contract()
