from __future__ import annotations
from sanity_tests._scenario_contracts import assert_main_config_self_heal

def test_main_config_self_heal(tmp_path):
    assert_main_config_self_heal(tmp_path)
