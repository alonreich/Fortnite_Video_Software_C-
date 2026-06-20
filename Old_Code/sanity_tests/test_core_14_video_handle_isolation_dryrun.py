from __future__ import annotations
import re
from sanity_tests._ai_sanity_helpers import read_source, assert_all_present

def _def_block(src: str, fn_name: str) -> str:
    m = re.search(rf"def\s+{re.escape(fn_name)}\([^\)]*\):([\s\S]*?)(?:\n\s*def\s+|\Z)", src)
    assert m, f"Function not found: {fn_name}"
    return m.group(1)

def test_core_14_video_handle_controls_video_only_in_wizard_players_dryrun() -> None:
    main_playback_src = read_source("ui/widgets/music_wizard_playback.py")
    merger_playback_src = read_source("utilities/merger_music_wizard_playback.py")
    main_block = _def_block(main_playback_src, "_on_video_vol_changed")
    merger_block = _def_block(merger_playback_src, "_on_video_vol_changed")
    assert "if not self.player:" in main_block
    assert 'self._safe_mpv_set(self.player, "volume", eff)' in main_block
    assert 'self._safe_mpv_set(self.player, "mute", False)' in main_block
    assert "audio_set_volume" not in main_block
    assert "_music_player" not in main_block
    assert "if not self.player:" in merger_block
    assert 'self._safe_mpv_set(self.player, "volume", eff)' in merger_block
    assert 'self._safe_mpv_set(self.player, "mute", False)' in merger_block
    assert "audio_set_volume" not in merger_block
    assert "_music_player" not in merger_block

def test_core_14_video_handle_stays_separate_from_music_export_mix_dryrun() -> None:
    main_mix_src = read_source("ui/parts/ffmpeg_mixin.py")
    merger_mix_src = read_source("utilities/merger_window.py")
    assert_all_present(
        main_mix_src,
        [
            "linear_video_vol = self._get_master_eff() / 100.0",
            "music_vol_linear = self._music_eff() / 100.0 if music_path else 0.0",
            "bg_music_volume=music_vol_linear",
            "'main_vol': linear_video_vol",
        ],
    )
    assert_all_present(
        merger_mix_src,
        [
            "music_vol = self.unified_music_widget.get_volume()",
            "video_vol = self.unified_music_widget.get_video_volume()",
            "build_audio_ducking_filters(",
            "music_volume=1.0",
            "volume={video_vol/100.0}",
        ],
    )
