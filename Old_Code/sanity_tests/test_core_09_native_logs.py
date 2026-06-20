from sanity_tests.test_real_sanity_core import test_core_09_native_logs_faulthandler_pipeline

def test_core_09_native_logs(monkeypatch, tmp_path) -> None:
    test_core_09_native_logs_faulthandler_pipeline(monkeypatch, tmp_path)
