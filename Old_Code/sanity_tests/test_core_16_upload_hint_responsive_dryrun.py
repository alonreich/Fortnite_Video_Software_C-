from __future__ import annotations
from sanity_tests._ai_sanity_helpers import assert_all_present, read_source

def test_core_16_upload_hint_responsive_baseline_contracts_dryrun() -> None:
    """
    End-user UX contract:
    Keep the upload hint dormant until explicitly activated,
    then show it only while no video is loaded.
    """
    src = read_source("ui/parts/main_window_ui_helpers_b.py")
    assert_all_present(
        src,
        [
            "def _update_upload_hint_responsive(self):",
            "self._hint_debounce_timer.setSingleShot(True)",
            "self._hint_debounce_timer.timeout.connect(self._do_update_upload_hint_responsive)",
            "self._hint_debounce_timer.start(5)",
            "def _do_update_upload_hint_responsive(self):",
            "self._upload_hint_active = bool(active)",
            "show_hint = self._upload_hint_active and not self._has_uploaded_video()",
            "self.upload_hint_label.show()",
            "self._hide_upload_hint_group()",
        ],
    )
    ui_builder_src = read_source("ui/parts/ui_builder_mixin.py")
    assert_all_present(
        ui_builder_src,
        [
            "self.upload_hint_label = QLabel('Upload Video File to begin!')",
            "self.upload_hint_label.setAlignment(Qt.AlignCenter)",
            "self.upload_hint_arrow.hide()",
            "self.upload_hint_container.hide()",
            "self.hint_group_container.hide()",
        ],
    )
    blink_src = read_source("ui/parts/main_window_ui_helpers_a.py")
    assert_all_present(
        blink_src,
        [
            "if getattr(self, '_upload_hint_active', False) and (not callable(timer_active) or not timer_active()):",
        ],
    )

def test_core_16_upload_hint_reflows_on_live_resize_contracts_dryrun() -> None:
    """
    Regression contract:
    Upload hint must recompute during live resize (same app session),
    not only after close/reopen.
    """
    src = read_source("ui/parts/main_window_events.py")
    assert_all_present(
        src,
        [
            "QTimer.singleShot(0, self._update_upload_hint_responsive)",
            "if hasattr(self, \"_update_upload_hint_responsive\"):",
            "self._update_upload_hint_responsive()",
        ],
    )
    ui_builder_src = read_source("ui/parts/ui_builder_mixin.py")
    assert_all_present(
        ui_builder_src,
        [
            "if event.type() in (QEvent.Resize, QEvent.Move):",
            "if obj is self or obj is vs:",
            "self._update_upload_hint_responsive()",
        ],
    )

def test_core_16_upload_hint_small_size_anti_overlap_contracts_dryrun() -> None:
    """
    Regression contract:
    At small window sizes, the centered hint must constrain its width
    and keep the old arrow hidden.
    """
    src = read_source("ui/parts/main_window_ui_helpers_b.py")
    assert_all_present(
        src,
        [
            "if not hasattr(self, 'hint_group_container') or self.hint_group_container is None: return",
            "max_width = max(260, min(520, int(target.width() * 0.78)))",
            "label.setMaximumWidth(max_width - 36)",
            "self.hint_group_container.resize(hint_size)",
            "self.hint_group_container.move(x, y)",
            "self.upload_hint_arrow.hide()",
        ],
    )
