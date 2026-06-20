from __future__ import annotations
from sanity_tests._scenario_contracts import assert_hw_scan_timeout_contract

def test_hw_scan_timeout_fallback():
    assert_hw_scan_timeout_contract()
