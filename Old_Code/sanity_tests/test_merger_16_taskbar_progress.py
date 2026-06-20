from __future__ import annotations
from sanity_tests._scenario_contracts import assert_merger_taskbar_progress_contract

def test_taskbar_progress_consistency():
    assert_merger_taskbar_progress_contract()
