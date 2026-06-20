from __future__ import annotations
import re
from sanity_tests._ai_sanity_helpers import read_source, assert_all_present

def _def_block(src: str, fn_name: str) -> str:
    m = re.search(
        rf"^\s{{4}}def\s+{re.escape(fn_name)}\([^\)]*\):([\s\S]*?)(?=\n\s{{4}}def\s+|\Z)",
        src,
        re.MULTILINE,
    )
    assert m, f"Function not found: {fn_name}"
    return m.group(1)

def test_core_13_music_handle_controls_music_only_in_wizard_players_dryrun() -> None:
    main_playback_src = read_source("ui/widgets/music_wizard_playback.py")
    merger_playback_src = read_source("utilities/merger_music_wizard_playback.py")
    main_block = _def_block(main_playback_src, "_on_music_vol_changed")
    merger_block = _def_block(merger_playback_src, "_on_music_vol_changed")
    assert 'music_player = getattr(self, "_music_player", None)' in main_block
    assert 'self._safe_mpv_set(music_player, "volume", eff)' in main_block
    assert 'self._safe_mpv_set(music_player, "mute", False)' in main_block
    assert "audio_set_volume" not in main_block
    assert "self.player" not in main_block.replace('self._safe_mpv_set(music_player, "volume", eff)', "").replace('self._safe_mpv_set(music_player, "mute", False)', "")
    assert 'music_player = getattr(self, "_music_player", None)' in merger_block
    assert 'self._safe_mpv_set(music_player, "volume", eff)' in merger_block
    assert 'self._safe_mpv_set(music_player, "mute", False)' in merger_block
    assert "audio_set_volume" not in merger_block

def test_core_13_music_handle_stays_separate_from_video_export_mix_dryrun() -> None:
    main_mix_src = read_source("ui/parts/ffmpeg_mixin.py")
    merger_mix_src = read_source("utilities/merger_window.py")
    assert_all_present(
        main_mix_src,
        [
            "linear_video_vol = self._get_master_eff() / 100.0",
            "music_vol_linear = self._music_eff() / 100.0 if music_path else 0.0",
            "'main_vol': linear_video_vol",
            "'music_vol': music_vol_linear if music_path else 1.0",
        ],
    )
    assert_all_present(
        merger_mix_src,
        [
            "music_vol = self.unified_music_widget.get_volume()",
            "video_vol = self.unified_music_widget.get_video_volume()",
            "volume={video_vol/100.0}",
            "volume={music_vol/100.0}",
        ],
    )

def test_core_13_merger_step3_sync_does_not_force_music_slider_every_tick_dryrun() -> None:
    """
    Regression guard for real bug:
    In Step 3 merger preview, per-tick sync must not repeatedly apply
    `_player.audio_set_volume(self.music_vol_slider.value())` inside the
    steady-state branch, because that can bleed into combined output behavior.
    """
    src = read_source("utilities/merger_music_wizard_timeline.py")
    sync_block = _def_block(src, "_sync_music_only_to_time")
    assert "else:" in sync_block
    else_part = sync_block.split("else:", 1)[1]
    assert "self._player.audio_set_volume(self.music_vol_slider.value())" not in else_part

def test_core_13_merger_step2_uses_music_instance_for_music_player_media_dryrun() -> None:
    """Music preview must bind _player media via mpv_m instance, not self.mpv alias."""
    src = read_source("utilities/merger_music_wizard_playback.py")
    step2_block = _def_block(src, "toggle_video_preview")
    assert "m = self.mpv_m.media_new(preview_path)" in step2_block
    assert "m = self.mpv.media_new(preview_path)" not in step2_block
