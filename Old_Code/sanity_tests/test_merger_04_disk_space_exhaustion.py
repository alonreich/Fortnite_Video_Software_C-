from __future__ import annotations
from sanity_tests._scenario_contracts import assert_merger_disk_space_contract

def test_disk_space_exhaustion():
    assert_merger_disk_space_contract()
