from __future__ import annotations
from sanity_tests._ai_sanity_helpers import assert_all_present, read_source

def test_preview_players_have_anti_stutter_tick_and_seek_guards_dryrun() -> None:
    """
    Validate source-level anti-stutter contracts for preview players:
    - Main app preview
    - Main wizard step-3 preview
    - Merger wizard step-3 preview
    """
    main_preview = read_source("ui/parts/player_mixin.py")
    main_wizard_step3 = read_source("ui/widgets/music_wizard_playback.py")
    merger_wizard_step3 = read_source("utilities/merger_music_wizard_playback.py")
    assert_all_present(
        main_preview,
        [
            "if last_scrub_ts and (now - last_scrub_ts) < 0.05:",
            "if slider and slider.isSliderDown():",
            "self._seek_timer.start(0 if force_pause else 50)",
        ],
    )
    assert_all_present(
        main_wizard_step3,
        [
            "do_heavy = (now - self._last_tick_ts > 0.1)",
            "if now - self._last_seek_ts < 0.5:",
            "self.timeline.set_current_time(project_time)",
            "target_m_sec = offset + (project_time - accum)",
            'self._safe_mpv_command(m_player, "seek", target_m_sec, "absolute", "exact")',
        ],
    )
    assert_all_present(
        merger_wizard_step3,
        [
            "do_heavy = (now - self._last_tick_ts > 0.1)",
            "if now - self._last_seek_ts < 0.5:",
            "self.timeline.set_current_time(project_time)",
            "self._sync_music_only_to_time(project_time)",
        ],
    )

def test_step2_click_to_seek_and_caret_sync_contracts_dryrun() -> None:
    """
    Validate click-to-seek and caret-sync contracts for Step-2 music preview
    in both the main wizard and the merger wizard.
    """
    main_waveform = read_source("ui/widgets/music_wizard_waveform.py")
    merger_waveform = read_source("utilities/merger_music_wizard_waveform.py")
    assert_all_present(
        main_waveform,
        [
            "def _on_slider_seek(self, val_ms):",
            "if self._player: self._player.set_time(val_ms)",
            "self._sync_caret()",
            "def _set_time_from_wave_x(self, x):",
            "self.offset_slider.setValue(target_ms)",
            "self._player.set_time(target_ms)",
        ],
    )
    assert_all_present(
        merger_waveform,
        [
            "def _on_slider_seek(self, val_ms):",
            "if self._player: self._player.set_time(val_ms)",
            "self._sync_caret()",
            "def _set_time_from_wave_x(self, x):",
            "self.offset_slider.setValue(target_ms)",
            "self._player.set_time(target_ms)",
        ],
    )

def test_step3_click_to_seek_timeline_contracts_dryrun() -> None:
    """
    Validate Step-3 timeline click/seek contracts for both wizards.
    """
    main_timeline = read_source("ui/widgets/music_wizard_timeline.py")
    merger_timeline = read_source("utilities/merger_music_wizard_timeline.py")
    assert_all_present(
        main_timeline,
        [
            "def _on_timeline_seek(self, pct):",
            "self.timeline.set_current_time(target_sec)",
            "self._apply_step3_seek_target(float(pending))",
            "self._sync_all_players_to_time(safe_sec, force_playing=is_currently_playing, seek_exact=True)",
            "self._sync_caret()",
        ],
    )
    assert_all_present(
        merger_timeline,
        [
            "def _on_timeline_seek(self, pct):",
            "self.timeline.set_current_time(target_sec)",
            "self._apply_step3_seek_target(float(pending))",
            "self._sync_all_players_to_time(safe_sec, force_playing=is_currently_playing, seek_exact=True)",
            "self._sync_caret()",
        ],
    )

def test_granular_editor_seek_and_timeline_sync_contracts_dryrun() -> None:
    """
    Validate granular editor click/seek contract for responsive preview updates.
    """
    granular_src = read_source("ui/widgets/granular_speed_editor.py")
    assert_all_present(
        granular_src,
        [
            "self.timeline.sliderMoved.connect(self._on_timeline_moved)",
            "def seek_video(self, pos, exact=False):",
            'self._safe_mpv_command("seek", abs_pos_ms / 1000.0, "absolute", mode)',
            "if not self.timeline.isSliderDown():",
            "t_i = int(round(curr_rel))",
            "self.timeline.setValue(t_i)",
        ],
    )
