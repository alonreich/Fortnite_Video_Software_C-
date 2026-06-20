from sanity_tests._ai_sanity_helpers import assert_all_present, read_source

def test_challenge_06_worker_race_dryrun() -> None:
    src = read_source("ui/widgets/music_wizard_workers.py")
    assert_all_present(
        src,
        [
            "def _kill_process_tree(proc: Any | None) -> None:",
            "[\"taskkill\", \"/PID\", str(proc.pid), \"/T\", \"/F\"]",
            "def stop(self):",
            "_kill_process_tree(self._proc)",
        ],
    )
