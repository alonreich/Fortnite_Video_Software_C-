from __future__ import annotations
from sanity_tests._scenario_contracts import assert_merger_audio_ducking_contract

def test_audio_ducking_stress():
    assert_merger_audio_ducking_contract()
