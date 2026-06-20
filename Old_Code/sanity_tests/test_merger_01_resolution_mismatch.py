import os, sys, pytest, tempfile, shutil
sys.dont_write_bytecode = True

from sanity_tests._real_sanity_harness import install_qt_mpv_stubs, DummyMediaPlayer, DummySpinBox, DummySlider, DummyButton
install_qt_mpv_stubs()

from processing.filter_builder import FilterBuilder

def test_resolution_mismatch_normalization():
    """
    Test if the filter builder correctly handles input videos with different resolutions
    by applying proper scaling/padding to a target resolution (e.g., 1080x1920 for mobile).
    Success: Filter chain contains 'scale' and 'pad' or 'crop' filters.
    """
    builder = FilterBuilder()
    mock_coords = {
        "crops_1080p": {
            "normal_hp": [100, 50, 200, 300],
            "loot": [150, 80, 500, 600]
        },
        "scales": {"normal_hp": 1.0, "loot": 1.0},
        "overlays": {"normal_hp": {"x": 10, "y": 20}, "loot": {"x": 30, "y": 40}},
        "z_orders": {"normal_hp": 10, "loot": 20}
    }
    filter_chain, v_out = builder.build_mobile_filter_chain(
        input_pad="[v_src]",
        mobile_coords=mock_coords,
        is_boss_hp=False,
        show_teammates=True,
        original_resolution="1920x1080"
    )
    assert "scale=" in filter_chain
    assert "crop=" in filter_chain
    assert "pad=" in filter_chain
    assert v_out == "[v_final]"
    filter_chain_2, _ = builder.build_mobile_filter_chain(
        input_pad="[v_src]",
        mobile_coords=mock_coords,
        is_boss_hp=False,
        show_teammates=False,
        original_resolution="1280x720"
    )
    assert "scale=" in filter_chain_2
    assert "pad=" in filter_chain_2
