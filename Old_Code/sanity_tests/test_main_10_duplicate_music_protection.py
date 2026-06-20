from __future__ import annotations
from sanity_tests._scenario_contracts import assert_duplicate_file_protection

def test_duplicate_music_protection(tmp_path):
    assert_duplicate_file_protection(tmp_path)
