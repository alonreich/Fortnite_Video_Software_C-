from sanity_tests._ai_sanity_helpers import assert_all_present, read_source

def test_core_09_native_logs_dryrun() -> None:
    src = read_source("system/utils.py")
    assert_all_present(
        src,
        [
            "ReopenableTextStream",
            "faulthandler.enable()",
            "STABLE LOGGING ACTIVE - PIPE BROKER DISABLED",
        ],
    )
