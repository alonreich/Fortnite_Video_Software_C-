from __future__ import annotations
from sanity_tests._scenario_contracts import assert_directory_persistence

def test_directory_persistence(tmp_path):
    assert_directory_persistence(tmp_path)
