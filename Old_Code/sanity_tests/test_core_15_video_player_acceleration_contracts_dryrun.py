from __future__ import annotations
from sanity_tests._ai_sanity_helpers import assert_all_present, read_source

def test_core_15_main_app_player_acceleration_contracts_dryrun() -> None:
    app_boot_src = read_source("app.py")
    main_preview_src = read_source("ui/parts/main_window_core_b.py")
    granular_src = read_source("ui/widgets/granular_speed_editor.py")
    main_wizard_src = read_source("ui/widgets/music_wizard.py")
    assert_all_present(
        app_boot_src,
        [
            'os.environ["VIDEO_FORCE_CPU"] = "1"',
            'for mode, encoder in (("NVIDIA", "h264_nvenc"), ("AMD", "h264_amf"), ("INTEL", "h264_qsv")):',
            "if check_encoder_capability(self.ffmpeg_path, encoder):",
        ],
    )
    assert_all_present(
        main_preview_src,
        [
            "target_hwdec = \"auto\"",
            "self.player = MPVSafetyManager.create_safe_mpv(",
            "hwdec=target_hwdec",
            "vo='gpu' if sys.platform == 'win32' else 'gpu'",
        ],
    )
    assert_all_present(
        granular_src,
        [
            "self.player = MPVSafetyManager.create_safe_mpv(",
            "hwdec='auto'",
            "vo='gpu' if sys.platform == 'win32' else 'gpu'",
        ],
    )
    assert_all_present(
        main_wizard_src,
        [
            "kwargs = {'osc': False, 'hr_seek': 'yes', 'hwdec': 'auto'",
            "kwargs['vo'] = 'gpu'; kwargs['gpu-context'] = 'd3d11'",
            "self.mpv_instance = mpv.MPV(**kwargs)",
            "self._wizard_music_player = MPVSafetyManager.create_safe_mpv",
        ],
    )

def test_core_15_crop_and_merger_step3_acceleration_contracts_dryrun() -> None:
    crop_src = read_source("developer_tools/media_processor.py")
    merger_wizard_src = read_source("utilities/merger_music_wizard.py")
    assert_all_present(
        crop_src,
        [
            "'hwdec': 'no'",
            "'vo': 'null' if wid is None else 'gpu,direct3d,d3d11,null'",
            "self.player.hwdec = 'auto'",
            "self.player.vo = 'gpu,direct3d,d3d11,null'",
        ],
    )
    assert_all_present(
        merger_wizard_src,
        [
            "'hwdec': 'auto'",
            "kwargs['vo'] = 'gpu'",
            "kwargs['gpu-context'] = 'd3d11'",
            "self._video_player = self._wizard_video_player",
        ],
    )
