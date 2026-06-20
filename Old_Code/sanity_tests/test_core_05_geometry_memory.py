from sanity_tests.test_real_sanity_geometry_persistence_report import test_geometry_persistence_report_end_user_readable

def test_core_05_geometry_memory(tmp_path, capsys) -> None:
    test_geometry_persistence_report_end_user_readable(tmp_path, capsys)
