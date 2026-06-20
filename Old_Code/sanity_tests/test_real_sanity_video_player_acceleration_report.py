from __future__ import annotations
from dataclasses import dataclass
from typing import TYPE_CHECKING
from sanity_tests._ai_sanity_helpers import read_source
if TYPE_CHECKING:
    import pytest
@dataclass
class PlayerAccelerationResult:
    name: str
    gpu_accel_enabled: bool
    cpu_fallback_present: bool
    details: str

def _final_verdict(r: PlayerAccelerationResult) -> str:
    if r.gpu_accel_enabled and r.cpu_fallback_present:
        return "GPU HEAVY LIFTING AVAILABLE (CPU FALLBACK ALSO AVAILABLE)"
    if r.gpu_accel_enabled and not r.cpu_fallback_present:
        return "GPU HEAVY LIFTING AVAILABLE (CPU FALLBACK NOT EXPLICIT HERE)"
    return "CPU-BASED PATH"

def _has_any(text: str, needles: list[str]) -> bool:
    return any(n in text for n in needles)

def _assess_mpv_player_contract(name: str, src: str) -> PlayerAccelerationResult:
    gpu_accel_enabled = (
        _has_any(src, ["--avcodec-hw=any", "hwdec='auto'", "'hwdec': 'auto'", "target_hwdec", "mpv.MPV", "MPVSafetyManager.create_safe_mpv"])
        and _has_any(src, ["hr_seek", "keep_open", "ytdl=False", "vo='gpu'", "'vo': 'gpu'"])
    )
    cpu_fallback_present = _has_any(
        src,
        [
            "VIDEO_FORCE_CPU",
            "fallback_args",
            "if not self.player:",
            "CPU",
        ],
    )
    if gpu_accel_enabled and cpu_fallback_present:
        details = "GPU acceleration requested; CPU fallback path also exists."
    elif gpu_accel_enabled:
        details = "GPU acceleration requested; no explicit CPU fallback marker in this file."
    else:
        details = "No explicit GPU acceleration contract found; expected mostly CPU load."
    return PlayerAccelerationResult(
        name=name,
        gpu_accel_enabled=gpu_accel_enabled,
        cpu_fallback_present=cpu_fallback_present,
        details=details,
    )

def test_real_sanity_video_player_acceleration_report_end_user_readable(
    capsys: "pytest.CaptureFixture[str]",
) -> None:
    app_boot_src = read_source("app.py")
    main_preview_src = read_source("ui/parts/main_window_core_b.py")
    granular_src = read_source("ui/widgets/granular_speed_editor.py")
    main_wizard_src = read_source("ui/widgets/music_wizard.py")
    crop_src = read_source("developer_tools/media_processor.py")
    merger_wizard_src = read_source("utilities/merger_music_wizard.py")
    app_level_cpu_control = (
        'os.environ["VIDEO_FORCE_CPU"] = "1"' in app_boot_src
        and 'for mode, encoder in (("NVIDIA", "h264_nvenc"), ("AMD", "h264_amf"), ("INTEL", "h264_qsv")):' in app_boot_src
        and "if check_encoder_capability(self.ffmpeg_path, encoder):" in app_boot_src
    )
    results = [
        _assess_mpv_player_contract("Main App - Preview Player", main_preview_src),
        _assess_mpv_player_contract("Main App - Granular Speed Editor Player", granular_src),
        _assess_mpv_player_contract("Main App - Music Wizard Step 3 Video Player", main_wizard_src),
        _assess_mpv_player_contract("Crop Tool - Media Processor Player", crop_src),
        _assess_mpv_player_contract("Video Merger - Music Wizard Step 3 Video Player", merger_wizard_src),
    ]
    print("\n=== VIDEO PLAYER ACCELERATION REPORT ===")
    print(
        "[APP BOOTSTRAP] "
        + (
            "CPU fallback + GPU probing contract detected (NVENC/AMF/QSV + VIDEO_FORCE_CPU)."
            if app_level_cpu_control
            else "CPU/GPU bootstrap contract missing in app.py."
        )
    )
    print("\nCLEAR VERDICT PER PLAYER:")
    for r in results:
        print(f"- {r.name}: {_final_verdict(r)}")
        print(f"  GPU HEAVY LIFTING PATH: {'YES' if r.gpu_accel_enabled else 'NO'}")
        print(f"  CPU MODE/FALLBACK PATH: {'YES' if r.cpu_fallback_present else 'NO/NOT EXPLICIT'}")
        print(f"  Details: {r.details}")
    out = capsys.readouterr().out
    assert "VIDEO PLAYER ACCELERATION REPORT" in out
    assert "Main App - Preview Player" in out
    assert "Main App - Granular Speed Editor Player" in out
    assert "Main App - Music Wizard Step 3 Video Player" in out
    assert "Crop Tool - Media Processor Player" in out
    assert "CLEAR VERDICT PER PLAYER" in out
    assert "GPU HEAVY LIFTING PATH:" in out
    assert "CPU MODE/FALLBACK PATH:" in out
    assert app_level_cpu_control, "app.py must expose GPU probe + CPU fallback hardware strategy contract"
    for r in results:
        assert r.gpu_accel_enabled or r.cpu_fallback_present, f"Missing acceleration visibility for: {r.name}"
