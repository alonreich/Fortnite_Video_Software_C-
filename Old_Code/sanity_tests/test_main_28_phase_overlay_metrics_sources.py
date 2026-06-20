from __future__ import annotations
from collections import deque
from types import SimpleNamespace
from sanity_tests._real_sanity_harness import install_qt_mpv_stubs
install_qt_mpv_stubs()

from ui.parts import phase_overlay_mixin
from ui.parts.phase_overlay_mixin import PhaseOverlayMixin

def _metrics_host():
    return SimpleNamespace(
        _cpu_hist=deque(maxlen=10),
        _gpu_hist=deque(maxlen=10),
        _mem_hist=deque(maxlen=10),
        _iops_hist=deque(maxlen=10),
        _last_gpu_val=0,
        _overlay=None,
    )

def test_phase_overlay_gpu_update_clamps_sampler_values() -> None:
    host = _metrics_host()
    PhaseOverlayMixin._on_gpu_update(host, 142)
    assert host._last_gpu_val == 100
    PhaseOverlayMixin._on_gpu_update(host, -18)
    assert host._last_gpu_val == 0
    PhaseOverlayMixin._on_gpu_update(host, "bad")
    assert host._last_gpu_val == 0

def test_phase_overlay_samples_cpu_gpu_mem_and_iops_from_live_sources(monkeypatch) -> None:
    host = _metrics_host()
    PhaseOverlayMixin._on_gpu_update(host, 73)
    disk_samples = iter([
        SimpleNamespace(read_count=100, write_count=50),
        SimpleNamespace(read_count=150, write_count=80),
    ])
    time_samples = iter([100.0, 101.0])
    monkeypatch.setattr(phase_overlay_mixin.psutil, "cpu_percent", lambda interval=None: 34)
    monkeypatch.setattr(phase_overlay_mixin.psutil, "virtual_memory", lambda: SimpleNamespace(percent=61))
    monkeypatch.setattr(phase_overlay_mixin.psutil, "disk_io_counters", lambda: next(disk_samples))
    monkeypatch.setattr(phase_overlay_mixin.time, "time", lambda: next(time_samples))
    PhaseOverlayMixin._sample_perf_counters_safe(host)
    PhaseOverlayMixin._sample_perf_counters_safe(host)
    assert list(host._cpu_hist) == [34, 34]
    assert list(host._gpu_hist) == [73, 73]
    assert list(host._mem_hist) == [61, 61]
    assert list(host._iops_hist) == [0, 80]

def test_phase_overlay_metrics_fall_back_to_zero_when_sources_fail(monkeypatch) -> None:
    host = _metrics_host()

    def fail(*args, **kwargs):
        raise RuntimeError("source unavailable")
    monkeypatch.setattr(phase_overlay_mixin.psutil, "cpu_percent", fail)
    monkeypatch.setattr(phase_overlay_mixin.psutil, "virtual_memory", fail)
    monkeypatch.setattr(phase_overlay_mixin.psutil, "disk_io_counters", fail)
    PhaseOverlayMixin._sample_perf_counters_safe(host)
    assert host._cpu_hist[-1] == 0
    assert host._gpu_hist[-1] == 0
    assert host._mem_hist[-1] == 0
    assert host._iops_hist[-1] == 0
