from __future__ import annotations
from collections import deque
import types
from sanity_tests._ai_sanity_helpers import assert_all_present, read_source
from sanity_tests._real_sanity_harness import install_qt_mpv_stubs
install_qt_mpv_stubs()

def test_main_phase_overlay_gpu_worker_restarts_after_hidden_startup_state() -> None:
    src = read_source("ui/parts/phase_overlay_mixin.py")
    assert_all_present(
        src,
        [
            "def _find_nvidia_smi():",
            'shutil.which("nvidia-smi")',
            r'C:\Windows\System32\nvidia-smi.exe',
            "def start_polling(self):",
            "self._running = True",
            "self._gpu_worker.start_polling()",
            'if hasattr(self, "_gpu_worker"):',
            'if hasattr(self, "_gpu_worker") and self._gpu_worker.isRunning():',
        ],
    )

def test_main_phase_overlay_gpu_samples_feed_graph_history() -> None:
    from ui.parts.phase_overlay_mixin import PhaseOverlayMixin
    host = types.SimpleNamespace(
        _cpu_hist=deque(maxlen=10),
        _gpu_hist=deque(maxlen=10),
        _mem_hist=deque(maxlen=10),
        _iops_hist=deque(maxlen=10),
        _overlay=None,
    )
    PhaseOverlayMixin._on_gpu_update(host, 73)
    PhaseOverlayMixin._sample_perf_counters_safe(host)
    assert host._last_gpu_val == 73
    assert list(host._gpu_hist)[-1] == 73
