from sanity_tests._ai_sanity_helpers import assert_all_present, read_source

def test_core_12_main_and_crop_native_logging_contract_dryrun() -> None:
    src = read_source("system/utils.py")
    assert_all_present(
        src,
        [
            "python_debug_log_path = diagnostic_runtime.get_python_debug_log_path()",
            "mpv_trace_log_path = diagnostic_runtime.get_mpv_trace_log_path()",
            "ReopenableTextStream",
            "faulthandler.enable()",
            "STABLE LOGGING ACTIVE - PIPE BROKER DISABLED",
            "maxBytes=5*1024*1024",
            '"Main_App": "main_app"',
        ],
    )

def test_core_12_merger_native_logging_should_share_mpv_log_dryrun() -> None:
    src = read_source("utilities/merger_system.py")
    assert_all_present(
        src,
        [
            'mpv.log_path = os.path.join(log_dir, "mpv.log")',
            'raw_log_path = os.path.join(log_dir, f"mpv_{source_tag}.raw.log")',
            "touch_unlocked(raw_log_path)",
            "UNLOCKED REALTIME MODE",
            "source_tag = \"video_merger\"",
            "maxBytes=5*1024*1024",
        ],
    )
