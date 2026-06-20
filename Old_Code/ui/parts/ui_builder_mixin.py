import os
import sys
import subprocess
import uuid
import tempfile
from fractions import Fraction
from threading import Thread
from PyQt5.QtCore import Qt, QTimer, QSize, QEvent, QCoreApplication
from PyQt5.QtGui import QPixmap, QPainter, QFont, QIcon
from PyQt5.QtWidgets import QGridLayout, QMessageBox, QHBoxLayout, QVBoxLayout, QFrame, QSlider, QLabel, QStyle, QPushButton, QSpinBox, QDoubleSpinBox, QCheckBox, QProgressBar, QWidget, QStyleOptionSpinBox, QStackedLayout, QLineEdit, QGraphicsOpacityEffect
from ui.widgets.clickable_button import ClickableButton
from ui.widgets.trimmed_slider import TrimmedSlider
from ui.widgets.drop_area import DropAreaFrame
from ui.styles import UIStyles
from developer_tools.config import UI_COLORS, UI_LAYOUT
from processing.media_utils import calculate_video_bitrate, choose_audio_bitrate
try:
    from ui.widgets.portrait_mask_overlay import PortraitMaskOverlay
except ImportError:
    PortraitMaskOverlay = None
try:
    from ui.widgets.timeline_overlay import TimelineOverlay
except ImportError:
    TimelineOverlay = None

class ClickableSpinBox(QDoubleSpinBox):
    def __init__(self, *args, **kwargs):
        super().__init__(*args, **kwargs)
        self.setCursor(Qt.PointingHandCursor)

    def mousePressEvent(self, event):
        if event.button() == Qt.LeftButton:
            opt = QStyleOptionSpinBox()
            self.initStyleOption(opt)
            up_rect = self.style().subControlRect(QStyle.CC_SpinBox, opt, QStyle.SC_SpinBoxUp, self)
            down_rect = self.style().subControlRect(QStyle.CC_SpinBox, opt, QStyle.SC_SpinBoxDown, self)
            if up_rect.contains(event.pos()) or down_rect.contains(event.pos()):
                super().mousePressEvent(event)
                return
        super().mousePressEvent(event)

class UiBuilderMixin:
    def _pick_thumbnail_from_current_frame(self):
        try:
            if not self.input_file_path or not os.path.exists(self.input_file_path):
                QMessageBox.information(self, 'No Video', 'Please load a video first.')
                return
            request_id = str(uuid.uuid4())
            self._current_thumb_request = request_id
            pos_ms = 0
            try:
                if hasattr(self, 'positionSlider'):
                    pos_ms = int(self.positionSlider.value())
            except:
                pass
            if not pos_ms and getattr(self, 'player', None):
                pos_ms = int((getattr(self.player, 'time-pos', 0) or 0) * 1000)
            pos_s = float(pos_ms) / 1000.0
            if self.original_duration_ms > 0 and pos_ms > self.original_duration_ms:
                pos_s = self.original_duration_ms / 1000.0
            self.thumb_pick_btn.setText('⏳ EXTRACTING... ⏳')
            self.thumb_pick_btn.setEnabled(False)
            self.status_update_signal.emit('📸 Extracting thumbnail... please wait.')

            def _safety_reset():
                if getattr(self, '_current_thumb_request', None) == request_id:
                    if self.thumb_pick_btn.text() == '⏳ EXTRACTING... ⏳':
                        self.thumb_pick_btn.setEnabled(True)
                        self.thumb_pick_btn.setText('📸 SET THUMBNAIL 📸')
                        self.status_update_signal.emit('❌ Extraction timed out.')
            QTimer.singleShot(15000, _safety_reset)
            temp_thumb = os.path.normpath(os.path.join(tempfile.gettempdir(), f'fvs_thumb_{os.getpid()}_{request_id[:8]}.jpg'))
            ffmpeg_path = os.path.normpath(os.path.join(getattr(self, 'bin_dir', ''), 'ffmpeg.exe'))
            if not os.path.exists(ffmpeg_path):
                ffmpeg_path = 'ffmpeg.exe'
            cmd = [ffmpeg_path, '-y', '-ss', f'{pos_s:.3f}', '-i', os.path.normpath(self.input_file_path), '-frames:v', '1', '-an', '-f', 'image2', '-q:v', '2', temp_thumb]

            def _run_extract(rid, m, s, p_ms, p_sec, t_path):
                success = False
                err_text = ''
                try:
                    res = subprocess.run(cmd, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE, creationflags=134217728 if sys.platform == 'win32' else 0, timeout=12)
                    rc_ok = (res.returncode == 0)
                    file_ok = os.path.exists(t_path) and os.path.getsize(t_path) > 0
                    success = bool(rc_ok and file_ok)
                    if not success:
                        try:
                            err_text = (res.stderr or b'').decode('utf-8', errors='ignore').strip().splitlines()[-1] if res.stderr else ''
                        except Exception:
                            err_text = ''
                except subprocess.TimeoutExpired:
                    err_text = 'ffmpeg timed out'
                except Exception as e:
                    err_text = str(e)
                finally:
                    try:
                        if os.path.exists(t_path):
                            os.remove(t_path)
                    except Exception:
                        pass
                    try:
                        is_latest = getattr(self, '_current_thumb_request', None) == rid
                        payload = (m, s, is_latest, p_ms, bool(success), p_sec, err_text)
                        if hasattr(self, 'thumbnail_extracted_signal'):
                            self.thumbnail_extracted_signal.emit(payload)
                        else:
                            QTimer.singleShot(0, lambda: self._on_thumb_extracted(payload))
                    except:
                        pass
            mm = int(pos_s // 60)
            ss = pos_s % 60.0
            Thread(target=lambda: _run_extract(request_id, mm, ss, pos_ms, pos_s, temp_thumb), daemon=True).start()
        except Exception as e:
            self.thumb_pick_btn.setEnabled(True)
            self.thumb_pick_btn.setText('📸 SET THUMBNAIL 📸')
            QMessageBox.warning(self, 'Error', f'Failed to pick thumbnail: {e}')

    def _on_thumb_extracted(self, payload, *legacy_args):
        if isinstance(payload, tuple):
            mm, ss, is_latest, pos_ms, success, pos_sec, err_text = payload
        else:
            mm = int(payload)
            ss = float(legacy_args[0]) if len(legacy_args) >= 1 else 0.0
            is_latest = bool(legacy_args[1]) if len(legacy_args) >= 2 else True
            pos_ms = int(legacy_args[2]) if len(legacy_args) >= 3 else 0
            success = True
            pos_sec = pos_ms / 1000.0
            err_text = ''
        self.thumb_pick_btn.setEnabled(True)
        if not is_latest:
            self.thumb_pick_btn.setText('📸 SET THUMBNAIL 📸')
            return
        if success:
            self.selected_intro_abs_time = float(pos_sec)
            self.thumb_pick_btn.setText(f'✅ THUMBNAIL SET ✅')
            self.status_update_signal.emit(f'✅ SUCCESS: Thumbnail set at {int(mm):02d}:{float(ss):05.2f} ✅')
            if hasattr(self, 'positionSlider'):
                self.positionSlider.set_thumbnail_pos_ms(pos_ms)
            if hasattr(self, "_save_recovery_state"):
                self._save_recovery_state()
            if hasattr(self, "_update_granular_button_state"):
                self._update_granular_button_state()
            QTimer.singleShot(4000, lambda: self.thumb_pick_btn.setText(f'📸 THUMBNAIL SET 📸'))
        else:
            self.thumb_pick_btn.setText('📸 SET THUMBNAIL 📸')
            short = err_text or 'extraction failed'
            self.status_update_signal.emit(f'❌ Thumbnail extraction failed: {short}')
            try:
                QMessageBox.warning(self, 'Thumbnail Failed', f'Could not extract a thumbnail at this position.\n\nDetails: {short}')
            except Exception:
                pass

    def set_overlays_force_hidden(self, hidden):
        if hasattr(self, 'timeline_overlay') and self.timeline_overlay:
            self.timeline_overlay.set_force_hidden(hidden)
        if hasattr(self, 'portrait_mask_overlay') and self.portrait_mask_overlay:
            self.portrait_mask_overlay.set_force_hidden(hidden)

    def _on_boss_hp_toggled(self, checked):
        if hasattr(self, "timeline_overlay"):
            self.timeline_overlay.set_boss_hp_mode(checked)
        if hasattr(self, "_update_quality_label"):
            self._update_quality_label()
        if hasattr(self, "_save_recovery_state"):
            self._save_recovery_state()

    def _safe_stop_playback(self):
        if hasattr(self, "player") and self.player:
            self._safe_mpv_set("pause", True)
        m_p = getattr(self, "_music_preview_player", None)
        if m_p:
            self._safe_mpv_set("pause", True, target_player=m_p)
        if hasattr(self, "timer") and self.timer.isActive():
            self.timer.stop()
        if hasattr(self, "playPauseButton"):
            self.playPauseButton.setText("PLAY")
            self.playPauseButton.setIcon(self.style().standardIcon(QStyle.SP_MediaPlay))
        self.is_playing = False
        if not hasattr(self, "granular_button"): return
        if hasattr(self, "_update_granular_button_state"):
            self._update_granular_button_state()

    def _update_process_button_text(self) -> None:
        try:
            self._pulse_phase = (getattr(self, '_pulse_phase', 0) + 1) % 8
            if getattr(self, 'is_processing', False):
                dots = '.' * (1 + self._pulse_phase // 2)
                spinner = '⣾⣽⣻⢿⡿⣟⣯ⷿ'
                glyph = spinner[self._pulse_phase % len(spinner)]
                pm = QPixmap(24, 24)
                pm.fill(Qt.transparent)
                p = QPainter(pm)
                f = QFont(self.font())
                f.setPixelSize(14)
                f.setBold(True)
                p.setFont(f)
                p.setPen(Qt.black)
                p.drawText(pm.rect(), Qt.AlignCenter, glyph)
                p.end()
                self.process_button.setText(f'PROCESSING{dots}')
                self.process_button.setIcon(QIcon(pm))
                self.process_button.setIconSize(QSize(24, 24))
            else:
                self.process_button.setText('PROCESS')
                self.process_button.setIcon(QIcon())
        except:
            pass

    def _on_granular_checkbox_toggled(self, checked):
        if hasattr(self, "_update_quality_label"):
            self._update_quality_label()
        if hasattr(self, "positionSlider"):
            visible_segments = list(getattr(self, "speed_segments", []) or []) if checked else []
            self.positionSlider.set_speed_segments(visible_segments)
            self.positionSlider.update()
        if not checked:
            self._in_freeze_segment = False
            self._freeze_seg = None
            try:
                base_rate = getattr(self, "playback_rate", None)
                if base_rate is None and hasattr(self, "speed_spinbox"):
                    base_rate = self.speed_spinbox.value()
                self._safe_mpv_set("speed", base_rate or 1.0)
            except Exception:
                pass
        if hasattr(self, "_sync_music_preview"):
            QTimer.singleShot(0, self._sync_music_preview)
        if hasattr(self, "_save_recovery_state"):
            self._save_recovery_state()

    def _ensure_default_trim(self):
        try:
            if self.end_minute_input.maximum() == 0 and self.positionSlider.maximum() == 0:
                QMessageBox.warning(self, 'No video', 'Please load a video file first.')
                return False
            if (self.start_minute_input.value() == 0 and self.start_second_input.value() == 0) and (self.end_minute_input.value() == 0 and self.end_second_input.value() == 0):
                self.end_minute_input.setValue(self.end_minute_input.maximum())
                self.end_second_input.setValue(self.end_second_input.maximum())
            return True
        except:
            return False

    def _on_process_clicked(self):
        try:
            if not getattr(self, 'input_file_path', None) or self.positionSlider.maximum() <= 0:
                QMessageBox.warning(self, 'No Video Loaded', 'Please load a video file before starting the process.')
                return
            if not getattr(self, 'scan_complete', False):
                self.process_button.setEnabled(False)
                self.process_button.setText('WAITING FOR SCAN...')
                self.status_update_signal.emit('⌛ Hardware scan in progress... Export will start automatically.')
                self._pending_process = True
                return
            if hasattr(self, 'portrait_mask_overlay') and self.portrait_mask_overlay:
                self.portrait_mask_overlay.hide()
            try:
                if hasattr(self, '_safe_stop_playback'):
                    self._safe_stop_playback()
                elif getattr(self, 'player', None):
                    self.player.stop()
            except:
                pass
            if not self._ensure_default_trim():
                return
            self.start_processing()
        except:
            QMessageBox.critical(self, 'Error', 'Could not start processing.')

    def _maybe_enable_process(self):
        try:
            path = getattr(self, 'input_file_path', None)
            has_video = bool(path and isinstance(path, str) and os.path.exists(path) and self.positionSlider.maximum() > 0)
            scan_done = getattr(self, 'scan_complete', False)
            if has_video and scan_done:
                if not self.process_button.isEnabled():
                    self.process_button.setEnabled(True)
                    self.process_button.setText('PROCESS')
                    self._ensure_default_trim()
            elif has_video and not scan_done:
                self.process_button.setEnabled(False)
                self.process_button.setText('SCANNING HW...')
            else:
                self.process_button.setEnabled(False)
                self.process_button.setText('PROCESS')
            if hasattr(self, 'quality_slider'):
                self.quality_slider.setEnabled(has_video)
            if hasattr(self, 'music_button'):
                self.music_button.setCursor(Qt.PointingHandCursor if self.music_button.isEnabled() else Qt.ArrowCursor)
            if hasattr(self, 'granular_button'):
                self.granular_button.setCursor(Qt.PointingHandCursor if self.granular_button.isEnabled() else Qt.ArrowCursor)
        except:
            pass

    def launch_video_merger(self):
        p = os.path.join(self.base_dir, 'utilities', 'video_merger.py')
        if not os.path.exists(p):
            QMessageBox.critical(self, 'Missing Component', f'Missing: {p}')
            return
        try:
            from system.state_transfer import StateTransfer
            StateTransfer.save_state({'input_file': getattr(self, 'input_file_path', None), 'trim_start': getattr(self, 'trim_start_ms', 0), 'trim_end': getattr(self, 'trim_end_ms', 0), 'mobile_checked': self.mobile_checkbox.isChecked()})
            flags = 16 if sys.platform == 'win32' else 0
            if getattr(self, "player", None):
                try: self.player.stop()
                except Exception: pass
            self.hide()
            proc = subprocess.Popen([sys.executable, p], cwd=self.base_dir, creationflags=flags, close_fds=True, start_new_session=True)

            def _complete_merger_handoff():
                if proc.poll() is None:
                    self._preserve_child_processes_on_close = True
                    self._preserve_staged_input_on_close = True
                    self.close()
                    return
                self.show()
                QMessageBox.critical(self, 'Launch Error', f'Video Merger closed unexpectedly (Code: {proc.returncode}).')
            QTimer.singleShot(900, _complete_merger_handoff)
        except Exception as e:
            self.show()
            QMessageBox.critical(self, 'Launch Error', f'Failed: {e}')

    def eventFilter(self, obj, event):
        if event.type() in (QEvent.Resize, QEvent.Move):
            vs = getattr(self, 'video_surface', None)
            if obj is self or obj is vs:
                if event.type() == QEvent.Resize and obj is vs:
                    if hasattr(self, 'portrait_mask_overlay'):
                        self._update_portrait_mask_overlay_state()
                if hasattr(self, '_update_upload_hint_responsive'):
                    self._update_upload_hint_responsive()
        return False

    def init_ui(self):
        main_layout = QHBoxLayout()
        main_layout.setContentsMargins(0, 0, 0, 0)
        left_layout = QVBoxLayout()
        left_layout.setContentsMargins(0, 0, 0, 0)
        left_layout.setSpacing(0)
        self._top_row = QHBoxLayout()
        self._top_row.setSpacing(6)
        self._build_player_column()
        self._build_right_panel()
        self._top_row.addWidget(self.player_col_container, stretch=1)
        self._top_row.addWidget(self.right_panel)
        left_layout.addLayout(self._top_row)
        main_layout.addLayout(left_layout, stretch=1)
        self.central_widget.setLayout(main_layout)
        if hasattr(self, '_init_upload_hint_blink'):
            self._init_upload_hint_blink()
        self._ensure_overlay_widgets()
        self._hide_processing_overlay()
        self._maybe_enable_process()
        single_shot = getattr(QTimer, "singleShot", None)
        if callable(single_shot):
            single_shot(0, self._adjust_trim_margins)
            single_shot(0, self.apply_master_volume)
            single_shot(0, lambda: self.setFocus(getattr(Qt, "ActiveWindowFocusReason", 0)))
        else:
            self._adjust_trim_margins()
            self.apply_master_volume()

    def _build_player_column(self):
        player_col = QVBoxLayout()
        player_col.setContentsMargins(0, 0, 0, 0)
        player_col.setSpacing(6)
        video_and_volume = QHBoxLayout()
        video_and_volume.setContentsMargins(0, 0, 0, 0)
        video_and_volume.setSpacing(2)
        self._init_volume_slider()
        video_and_volume.addWidget(self.volume_container, 0, Qt.AlignHCenter)
        self._init_video_surface()
        video_and_volume.addWidget(self.video_frame, stretch=1)
        player_col.addLayout(video_and_volume)
        player_col.setStretch(0, 1)
        badge_style = "background: rgb(52, 152, 219); color: white; border-radius: 4px; padding: 2px 6px; font-weight: bold; font-size: 11px; border: none;"
        self._top_badge_container = QFrame(self)
        self._top_badge_container.setAttribute(Qt.WA_TransparentForMouseEvents)
        self._top_badge_container.hide()
        tl = QHBoxLayout(self._top_badge_container); tl.setContentsMargins(0,0,0,0)
        self._main_time_badge_top = QLabel(self._top_badge_container)
        self._main_time_badge_top.setStyleSheet(badge_style)
        tl.addWidget(self._main_time_badge_top)
        self._top_badge_opacity = QGraphicsOpacityEffect(self._top_badge_container)
        self._top_badge_container.setGraphicsEffect(self._top_badge_opacity)
        self._top_badge_opacity.setOpacity(0.0)
        self._bottom_badge_container = QFrame(self)
        self._bottom_badge_container.setAttribute(Qt.WA_TransparentForMouseEvents)
        self._bottom_badge_container.hide()
        bl = QHBoxLayout(self._bottom_badge_container); bl.setContentsMargins(0,0,0,0)
        self._main_time_badge_bottom = QLabel(self._bottom_badge_container)
        self._main_time_badge_bottom.setStyleSheet(badge_style)
        bl.addWidget(self._main_time_badge_bottom)
        self._bottom_badge_opacity = QGraphicsOpacityEffect(self._bottom_badge_container)
        self._bottom_badge_container.setGraphicsEffect(self._bottom_badge_opacity)
        self._bottom_badge_opacity.setOpacity(0.0)
        self._init_trim_controls()
        # 3-column grid: cols 0 and 2 share equal stretch so col 1 (trim_layout) is
        # always anchored to the true horizontal centre of the player column.
        # All widgets stored on self to prevent Python 3.13 GC freeing wrappers
        # while Qt C++ still holds live pointers (cause of 0xc0000005 crashes).
        self._trim_grid = QGridLayout()
        self._trim_grid.setContentsMargins(10, 0, 10, 0)
        self._trim_grid.setHorizontalSpacing(0)
        self._trim_grid.setVerticalSpacing(0)
        self._trim_grid.setColumnStretch(0, 1)
        self._trim_grid.setColumnStretch(1, 0)
        self._trim_grid.setColumnStretch(2, 1)
        self._trim_left_panel = QWidget()
        _left_trim_l = QHBoxLayout(self._trim_left_panel)
        _left_trim_l.setContentsMargins(5, 0, 0, 0)
        _left_trim_l.setSpacing(20)
        _left_trim_l.addWidget(self.thumb_pick_btn)
        _left_trim_l.addWidget(self.boss_hp_checkbox)
        _left_trim_l.addStretch(1)
        self._trim_grid.addWidget(self._trim_left_panel, 0, 0, Qt.AlignLeft | Qt.AlignVCenter)
        self._trim_grid.addLayout(self.trim_layout, 0, 1, Qt.AlignHCenter | Qt.AlignVCenter)
        self._trim_right_spacer = QWidget()
        self._trim_grid.addWidget(self._trim_right_spacer, 0, 2)
        player_col.addSpacing(8)
        player_col.addLayout(self._trim_grid)
        self._init_process_controls()
        player_col.addLayout(self.center_btn_container)
        player_col.addSpacing(2)
        self._init_status_bar()
        player_col.addWidget(self.progress_bar)
        self.player_col_container = QWidget()
        self.player_col_container.setLayout(player_col)
        self.player_col_container.installEventFilter(self)

    def _set_video_controls_enabled(self, enabled: bool):
        widgets = [self.playPauseButton, self.start_trim_button, self.end_trim_button, self.thumb_pick_btn, self.boss_hp_checkbox, self.quality_slider, self.speed_spinbox, self.granular_button, getattr(self, "granular_clear_button", None), self.granular_checkbox, self.mobile_checkbox, self.teammates_checkbox, self.no_fade_checkbox, self.portrait_text_input, self.music_button, self.positionSlider, self.start_minute_input, self.start_second_input, self.start_ms_input, self.end_minute_input, self.end_second_input, self.end_ms_input]
        for w in widgets:
            if w is not None:
                w.setEnabled(enabled)
        if enabled:
            is_m = self.mobile_checkbox.isChecked()
            self.teammates_checkbox.setEnabled(is_m)
            self.portrait_text_input.setEnabled(is_m)
        if hasattr(self, "_update_granular_button_state"):
            self._update_granular_button_state()

    def _set_preview_controls_available(self, available: bool):
        preview_widgets = [self.playPauseButton, self.start_trim_button, self.end_trim_button, self.thumb_pick_btn, self.granular_button, self.granular_checkbox, self.music_button, self.positionSlider]
        for w in preview_widgets:
            if w is not None:
                w.setEnabled(bool(available))
        clear_btn = getattr(self, "granular_clear_button", None)
        if clear_btn is not None:
            clear_btn.setEnabled(bool(available and getattr(self, "speed_segments", [])))
        if hasattr(self, "_update_granular_button_state"):
            self._update_granular_button_state()

    def _init_volume_slider(self):
        v_l = QVBoxLayout()
        v_l.setContentsMargins(0, 16, 0, 16)
        v_l.setSpacing(0)
        self.volume_slider = QSlider(Qt.Vertical)
        self.volume_slider.setFixedWidth(40)
        self.volume_slider.setRange(0, 100)
        self.volume_slider.setCursor(Qt.PointingHandCursor)
        self.volume_slider.setToolTip("Adjust Video Volume (Up/Down / Scroll)")
        self.volume_slider.setStyleSheet(UIStyles.SLIDER_VOLUME_VERTICAL_METALLIC)
        try:
            eff = int(self.config_manager.config.get('video_mix_volume', 100))
        except:
            eff = 100
        self.volume_slider.setValue(eff)
        self.volume_slider.valueChanged.connect(self._on_master_volume_changed)
        self.volume_slider.sliderMoved.connect(lambda _: self._update_volume_badge())
        self.volume_badge = QLabel('0%', self.volume_slider)
        self.volume_badge.setStyleSheet(f'color: {UI_COLORS.TEXT_PRIMARY}; background: {UI_COLORS.OVERLAY_DIM}; padding: 2px 8px; border-radius: 6px; font-weight: bold;')
        self.volume_badge.hide()
        v_l.addWidget(self.volume_badge, 0, Qt.AlignHCenter)
        v_l.addWidget(self.volume_slider, 1, Qt.AlignHCenter)
        self.volume_container = QWidget()
        self.volume_container.setLayout(v_l)
        self.volume_container.setFixedWidth(48)

    def _init_video_surface(self):
        if hasattr(self, 'video_frame') and self.video_frame is not None:
            return
        self.video_frame = QFrame()
        self.video_frame.setFrameShape(getattr(QFrame, "NoFrame", 0))
        self.video_frame.setFrameShadow(getattr(QFrame, "Plain", 0))
        self.video_frame.setLineWidth(0)
        self.video_frame.setMidLineWidth(0)
        self.video_frame.setMinimumHeight(360)
        self.video_frame.setFocusPolicy(Qt.NoFocus)
        self.video_stack = QStackedLayout(self.video_frame)
        self.video_stack.setStackingMode(QStackedLayout.StackAll)
        self.video_surface = QWidget()
        self.video_surface.setStyleSheet(f'background-color: {UI_COLORS.BACKGROUND_DARK};')
        self.video_surface.setAttribute(Qt.WA_DontCreateNativeAncestors)
        self.video_surface.setAttribute(Qt.WA_NativeWindow)
        self.video_surface.setAttribute(Qt.WA_OpaquePaintEvent)
        self.video_surface.setAttribute(Qt.WA_NoSystemBackground)
        self.video_surface.setAutoFillBackground(False)
        self.video_surface.winId()
        self.video_stack.addWidget(self.video_surface)
        self.video_viewport = self.video_surface
        self.hint_overlay_widget = QWidget()
        self.hint_overlay_widget.setCursor(Qt.ArrowCursor)
        self.hint_overlay_widget.setAttribute(Qt.WA_TransparentForMouseEvents, True)
        self.hint_overlay_widget.setAttribute(Qt.WA_TranslucentBackground, True)
        self.hint_overlay_widget.setStyleSheet('background: transparent; border: none;')
        self.hint_group_container = QWidget(self.hint_overlay_widget)
        self.hint_group_container.setCursor(Qt.ArrowCursor)
        self.hint_group_container.setAttribute(Qt.WA_TranslucentBackground, True)
        self.hint_group_container.setStyleSheet('background: transparent; border: none;')
        self.hint_group_layout = QHBoxLayout(self.hint_group_container)
        self.hint_group_layout.setContentsMargins(0, 0, 0, 0)
        self.hint_group_layout.setSpacing(0)
        self.upload_hint_container = QFrame()
        self.upload_hint_container.setFrameShape(getattr(QFrame, "NoFrame", 0))
        self.upload_hint_container.setFrameShadow(getattr(getattr(QFrame, "Shadow", QFrame), "Plain", 0))
        self.upload_hint_container.setLineWidth(0)
        self.upload_hint_container.setMidLineWidth(0)
        self.upload_hint_container.setObjectName('uploadHintContainer')
        hi_l = QVBoxLayout(self.upload_hint_container)
        hi_l.setContentsMargins(16, 16, 16, 16)
        self.upload_hint_label = QLabel('Upload Video File to begin!')
        self.upload_hint_label.setAlignment(Qt.AlignCenter)
        self.upload_hint_label.setWordWrap(True)
        self.upload_hint_label.hide()
        hi_l.addWidget(self.upload_hint_label)
        self.upload_hint_arrow = QLabel()
        self.upload_hint_arrow.hide()
        self.hint_group_layout.addWidget(self.upload_hint_container)
        self.hint_group_layout.addWidget(self.upload_hint_arrow)
        self.upload_hint_container.hide()
        self.hint_group_container.hide()
        self.preview_notice_container = QFrame(self.hint_overlay_widget)
        self.preview_notice_container.setObjectName('previewIsolationContainer')
        self.preview_notice_container.hide()
        pn_l = QVBoxLayout(self.preview_notice_container)
        pn_l.setContentsMargins(16, 16, 16, 16)
        pn_l.setSpacing(8)
        self.preview_notice_title = QLabel('Diagnostic Isolation Active')
        self.preview_notice_title.setStyleSheet(f'color: {UI_COLORS.TEXT_PRIMARY}; font-weight: bold; font-size: 14px;')
        self.preview_notice_title.setAlignment(Qt.AlignCenter)
        self.preview_notice_detail = QLabel(
            'CPU-only crash triage is active. Video should remain visible using software decode,\n'
            'while hardware acceleration stays disabled and extra containment guards are applied.'
        )
        self.preview_notice_detail.setStyleSheet(f'color: {UI_COLORS.TEXT_SECONDARY}; font-size: 12px;')
        self.preview_notice_detail.setAlignment(Qt.AlignCenter)
        self.preview_notice_detail.setWordWrap(True)
        pn_l.addWidget(self.preview_notice_title)
        pn_l.addWidget(self.preview_notice_detail)
        self.video_stack.addWidget(self.hint_overlay_widget)
        if PortraitMaskOverlay:
            self.portrait_mask_overlay = PortraitMaskOverlay(self)
            self.portrait_mask_overlay.hide()
        else:
            self.portrait_mask_overlay = None
        self.video_surface.installEventFilter(self)
        self.video_frame.installEventFilter(self)
        self.video_stack.setCurrentWidget(self.hint_overlay_widget)

    def _init_trim_controls(self):
        self.playPauseButton = QPushButton('PLAY')
        self.playPauseButton.setProperty('class', 'success')
        self.playPauseButton.setIcon(self.style().standardIcon(QStyle.SP_MediaPlay))
        self.playPauseButton.setFixedSize(85, 40)
        self.playPauseButton.setStyleSheet("min-width: 66px; max-width: 66px; min-height: 29px; max-height: 29px;")
        self.playPauseButton.setCursor(Qt.PointingHandCursor)
        self.playPauseButton.setToolTip("Toggle video playback (Space)")
        self.playPauseButton.clicked.connect(self.toggle_play_pause)
        self.playPauseButton.setEnabled(False)
        self.thumb_pick_btn = QPushButton('📸 THUMBNAIL')
        self.thumb_pick_btn.setProperty('class', 'primary')
        self.thumb_pick_btn.setFixedSize(UI_LAYOUT.BTN_WIDTH_MD + 10, UI_LAYOUT.BUTTON_HEIGHT)
        self.thumb_pick_btn.setCursor(Qt.PointingHandCursor)
        self.thumb_pick_btn.setToolTip("Use current frame as video intro/thumbnail")
        self.thumb_pick_btn.clicked.connect(self._pick_thumbnail_from_current_frame)
        self.thumb_pick_btn.setEnabled(False)
        self.start_minute_input = QSpinBox()
        self.start_second_input = QSpinBox()
        self.start_ms_input = QSpinBox()
        self.end_minute_input = QSpinBox()
        self.end_second_input = QSpinBox()
        self.end_ms_input = QSpinBox()
        for s in (self.start_minute_input, self.start_second_input, self.start_ms_input, self.end_minute_input, self.end_second_input, self.end_ms_input):
            s.setFixedHeight(22)
            s.setMaximumWidth(38)
            s.setEnabled(False)
            s.setCursor(Qt.PointingHandCursor)
            s.setToolTip("Fine-tune trim timing")
            s.valueChanged.connect(self._on_trim_spin_changed)
        # Seconds need slightly more room than minutes (always 2 digits 00-59)
        self.start_second_input.setMaximumWidth(46)
        self.end_second_input.setMaximumWidth(46)
        self.start_trim_button = QPushButton('MARK START')
        self.start_trim_button.setProperty('class', 'primary')
        self.start_trim_button.setFixedSize(UI_LAYOUT.BTN_WIDTH_MD, UI_LAYOUT.BUTTON_HEIGHT)
        self.start_trim_button.setCursor(Qt.PointingHandCursor)
        self.start_trim_button.setToolTip("Set trim start to current position ([)")
        self.start_trim_button.clicked.connect(self.set_start_time)
        self.start_trim_button.setEnabled(False)
        self.end_trim_button = QPushButton('MARK END')
        self.end_trim_button.setProperty('class', 'primary')
        self.end_trim_button.setFixedSize(UI_LAYOUT.BTN_WIDTH_MD, UI_LAYOUT.BUTTON_HEIGHT)
        self.end_trim_button.setCursor(Qt.PointingHandCursor)
        self.end_trim_button.setToolTip("Set trim end to current position (])")
        self.end_trim_button.clicked.connect(self.set_end_time)
        self.end_trim_button.setEnabled(False)

        def _ml(t, s):
            l = QHBoxLayout()
            l.setContentsMargins(0, 0, 0, 0)
            l.setSpacing(2)
            lbl = QLabel(t)
            lbl.setStyleSheet(f'color: {UI_COLORS.TEXT_SECONDARY}; font-size: 8px; font-weight: bold; text-transform: uppercase;')
            l.addWidget(lbl)
            l.addWidget(s)
            return l
        self.start_ms_input.hide()
        self.end_ms_input.hide()

        # Create professional Timecode-style containers
        self.start_time_group = QFrame()
        self.start_time_group.setStyleSheet(f"QFrame {{ background: #1a252f; border: 1px solid #1f3545; border-radius: 6px; padding: 1px; }}")
        stg_l = QHBoxLayout(self.start_time_group)
        stg_l.setContentsMargins(3, 1, 3, 1)
        stg_l.setSpacing(4)
        stg_l.addLayout(_ml('MIN', self.start_minute_input))
        stg_l.addLayout(_ml('SEC', self.start_second_input))

        self.end_time_group = QFrame()
        self.end_time_group.setStyleSheet(f"QFrame {{ background: #1a252f; border: 1px solid #1f3545; border-radius: 6px; padding: 1px; }}")
        etg_l = QHBoxLayout(self.end_time_group)
        etg_l.setContentsMargins(3, 1, 3, 1)
        etg_l.setSpacing(4)
        etg_l.addLayout(_ml('MIN', self.end_minute_input))
        etg_l.addLayout(_ml('SEC', self.end_second_input))

        self.boss_hp_checkbox = QCheckBox('Boss HP')
        self.boss_hp_checkbox.setStyleSheet(f'color: {UI_COLORS.TEXT_PRIMARY}; font-size: 9px;')
        self.boss_hp_checkbox.setCursor(Qt.PointingHandCursor)
        self.boss_hp_checkbox.setToolTip("Enable social media safe-zone for Boss HP bars")
        self.boss_hp_checkbox.toggled.connect(self._on_boss_hp_toggled)
        self.boss_hp_checkbox.setEnabled(False)
        self.trim_layout = QHBoxLayout()
        self.trim_layout.setSpacing(12)
        self.trim_layout.addWidget(self.start_time_group)
        self.trim_layout.addSpacing(4)
        self.trim_button_cluster = QWidget()
        cluster_layout = QHBoxLayout(self.trim_button_cluster)
        cluster_layout.setContentsMargins(0, 0, 0, 0)
        cluster_layout.setSpacing(0)
        cluster_layout.addWidget(self.start_trim_button)
        cluster_layout.addSpacing(44)
        cluster_layout.addWidget(self.playPauseButton)
        cluster_layout.addSpacing(44)
        cluster_layout.addWidget(self.end_trim_button)
        self.trim_layout.addWidget(self.trim_button_cluster)
        self.trim_layout.addSpacing(4)
        self.trim_layout.addWidget(self.end_time_group)

    def _init_process_controls(self):
        self.quality_label = QLabel('OUTPUT FILE SIZE')
        self.quality_label.setStyleSheet(f'color: {UI_COLORS.TEXT_PRIMARY}; font-size: 10px; font-weight: bold;')

        from ui.widgets.spinning_wheel_slider import SpinningWheelSlider
        self.quality_slider = SpinningWheelSlider()
        self.quality_slider.setRange(0, 20)
        mb_labels = [f'{5 + i * 5}MB' for i in range(20)] + ['ORIGINAL QUALITY']
        self.quality_slider.setLabels(mb_labels)
        self.quality_slider.setValue(7)
        self.quality_slider.setFixedSize(160, UI_LAYOUT.BUTTON_HEIGHT)
        self.quality_slider.setEnabled(False)
        self.quality_slider.setCursor(Qt.PointingHandCursor)
        self.quality_slider.setToolTip("Adjust target output file size (MB)")
        self.quality_slider.valueChanged.connect(self._on_quality_slider_changed)
        self.quality_value_label = QLabel('')
        self.quality_value_label.setStyleSheet('font-size: 9px; font-weight: bold;')
        self.quality_value_label.setMinimumWidth(80)
        self.quality_value_label.setAlignment(Qt.AlignCenter)
        q_v = QVBoxLayout()
        q_v.setContentsMargins(0, 0, 0, 0)
        q_v.setSpacing(2)
        q_v.addWidget(self.quality_label, 0, Qt.AlignHCenter)
        q_v.addWidget(self.quality_slider, 0, Qt.AlignHCenter)
        q_v.addWidget(self.quality_value_label, 0, Qt.AlignHCenter)
        self.quality_container = QWidget()
        self.quality_container.setLayout(q_v)
        self.process_button = QPushButton('PROCESS')
        self.process_button.setObjectName('processButton')
        self.process_button.setProperty('class', 'success')
        self.process_button.setFixedSize(125, 65)
        self.process_button.setStyleSheet("min-width: 106px; max-width: 106px; min-height: 54px; max-height: 54px;")
        self.process_button.setCursor(Qt.PointingHandCursor)
        self.process_button.setToolTip("Start video processing (Enter)")
        self.process_button.clicked.connect(self._on_process_clicked)
        self.process_button.setEnabled(False)
        self.cancel_button = ClickableButton('CANCEL')
        self.cancel_button.setProperty('class', 'warning')
        self.cancel_button.setFixedSize(125, 65)
        self.cancel_button.setCursor(Qt.PointingHandCursor)
        self.cancel_button.setToolTip("Abort the current processing task")
        self.cancel_button.setVisible(False)
        self.cancel_button.clicked.connect(self.cancel_processing)
        self.is_processing = False
        self._pulse_phase = 0
        self._pulse_timer = QTimer(self)
        self._pulse_timer.timeout.connect(self._update_process_button_text)
        btn_l = QHBoxLayout()
        btn_l.setSpacing(12)
        btn_l.addWidget(self.cancel_button)
        btn_l.addWidget(self.process_button)
        self.speed_spinbox = ClickableSpinBox()
        self.speed_spinbox.setRange(0.1, 4.0)
        self.speed_spinbox.setDecimals(1)
        self.speed_spinbox.setSingleStep(0.1)
        self.speed_spinbox.setValue(1.1)
        self.speed_spinbox.setFixedWidth(70)
        self.speed_spinbox.setFixedHeight(UI_LAYOUT.BUTTON_HEIGHT)
        self.speed_spinbox.setEnabled(False)
        self.speed_spinbox.setCursor(Qt.PointingHandCursor)
        self.speed_spinbox.setToolTip("Adjust overall playback speed (Up/Down)")
        self.speed_spinbox.valueChanged.connect(self._on_speed_changed)
        s_l = QVBoxLayout()
        s_l.setSpacing(2)
        speed_title = QLabel('Speed Multiplier')
        speed_title.setStyleSheet(f'color: {UI_COLORS.TEXT_PRIMARY}; font-size: 10px; font-weight: bold;')
        s_l.addWidget(speed_title, 0, Qt.AlignHCenter)
        s_l.addWidget(self.speed_spinbox, 0, Qt.AlignHCenter)
        speed_w = QWidget()
        speed_w.setLayout(s_l)
        self.granular_checkbox = QCheckBox('GRANULAR SPEED')
        self.granular_checkbox.setVisible(False)
        self.granular_checkbox.toggled.connect(self._on_granular_checkbox_toggled)
        self.granular_button = QPushButton('GRANULAR SPEED')
        self.granular_button.setProperty('class', 'primary')
        self.granular_button.setFixedSize(120, UI_LAYOUT.BUTTON_HEIGHT)
        self.granular_button.setCursor(Qt.PointingHandCursor)
        self.granular_button.setToolTip("Open the detailed speed curve editor")
        self.granular_button.clicked.connect(self._handle_granular_click)
        self.granular_button.setEnabled(False)
        granular_w = QWidget()
        g_l = QHBoxLayout(granular_w)
        g_l.setContentsMargins(0,0,0,0)
        g_l.setSpacing(0)
        g_l.addWidget(self.granular_button)
        self.mobile_checkbox = QCheckBox('Portrait (9:16)')
        self.mobile_checkbox.setStyleSheet(UIStyles.CHECKBOX)
        self.mobile_checkbox.setCursor(Qt.PointingHandCursor)
        self.mobile_checkbox.setToolTip("Switch to vertical 9:16 portrait crop for social media")
        try:
            val = bool(self.config_manager.config.get('mobile_checked', False))
        except:
            val = False
        self.mobile_checkbox.setChecked(val)
        self.mobile_checkbox.setEnabled(False)
        self.mobile_checkbox.toggled.connect(self._on_mobile_toggled)
        self.teammates_checkbox = QCheckBox('Show Teammates Healthbar')
        self.teammates_checkbox.setStyleSheet(f'color: {UI_COLORS.TEXT_PRIMARY}; font-size: 10px;')
        self.teammates_checkbox.setCursor(Qt.PointingHandCursor)
        self.teammates_checkbox.setToolTip("Enable overlay for teammates' health in portrait mode")
        self.teammates_checkbox.setEnabled(False)
        self.teammates_checkbox.setVisible(self.mobile_checkbox.isChecked())
        self.teammates_checkbox.toggled.connect(lambda _checked: self._save_recovery_state() if hasattr(self, "_save_recovery_state") else None)
        self.portrait_text_input = QLineEdit()
        self.portrait_text_input.setPlaceholderText('Overlay Text')
        self.portrait_text_input.setToolTip("Custom text for the portrait mode overlay")
        self.portrait_text_input.setEnabled(False)
        self.portrait_text_input.setVisible(self.mobile_checkbox.isChecked())
        self.portrait_text_input.setStyleSheet(f'background-color: #0b141d; color: {UI_COLORS.TEXT_PRIMARY}; border: 1px solid #1f3545; border-radius: 4px; padding: 4px; font-size: 10px;')
        self.portrait_text_input.editingFinished.connect(lambda: self._save_recovery_state() if hasattr(self, "_save_recovery_state") else None)
        # Portrait stuff
        l_col = QVBoxLayout()
        l_row = QHBoxLayout()
        l_row.addWidget(self.mobile_checkbox)
        l_row.addWidget(self.teammates_checkbox)
        l_col.addLayout(l_row)
        l_col.addWidget(self.portrait_text_input)
        self._portrait_inner_w = QWidget()
        self._portrait_inner_w.setLayout(l_col)

        # Left cell: portrait + quality, left-anchored
        # Stored on self to prevent GC-during-C++-use crash (python313.dll 0xc0000005)
        self._proc_left_panel = QWidget()
        left_proc_l = QHBoxLayout(self._proc_left_panel)
        left_proc_l.setContentsMargins(10, 0, 0, 0)
        left_proc_l.setSpacing(12)
        left_proc_l.addWidget(self._portrait_inner_w)
        left_proc_l.addWidget(self.quality_container)
        left_proc_l.addStretch(1)

        # Right cell: granular + speed, right-anchored
        self._proc_right_panel = QWidget()
        right_proc_l = QHBoxLayout(self._proc_right_panel)
        right_proc_l.setContentsMargins(0, 0, 10, 0)
        right_proc_l.setSpacing(8)
        right_proc_l.addStretch(1)
        right_proc_l.addWidget(granular_w)
        right_proc_l.addWidget(speed_w)

        # 3-column grid: equal stretch on cols 0 & 2 — PROCESS always at exact centre
        grid = QGridLayout()
        grid.setContentsMargins(0, 0, 0, 0)
        grid.setHorizontalSpacing(0)
        grid.setColumnStretch(0, 1)
        grid.setColumnStretch(1, 0)
        grid.setColumnStretch(2, 1)
        grid.addWidget(self._proc_left_panel, 0, 0, Qt.AlignLeft | Qt.AlignVCenter)
        grid.addLayout(btn_l, 0, 1, Qt.AlignHCenter | Qt.AlignVCenter)
        grid.addWidget(self._proc_right_panel, 0, 2, Qt.AlignRight | Qt.AlignVCenter)
        self.center_btn_container = grid

    def _build_right_panel(self):
        self.right_panel = QFrame()
        self.right_panel.setMinimumWidth(140)
        self.right_panel.setFrameShape(getattr(QFrame, "StyledPanel", 0))
        self.right_panel.setStyleSheet(f"QFrame {{ border: 1px solid #1f3545; background-color: {UI_COLORS.BACKGROUND_MEDIUM}; border-radius: 6px; }} QFrame > * {{ border: none; }}")
        self.right_panel.installEventFilter(self)
        r_l = QVBoxLayout(self.right_panel)
        r_l.setContentsMargins(8, 10, 8, 4)
        r_l.setSpacing(12)
        r_l.setAlignment(Qt.AlignTop)
        self.drop_area = DropAreaFrame()
        self.drop_area.file_dropped.connect(self.handle_file_selection)
        self.drop_area.clicked.connect(self.select_file)
        self.drop_area.setMinimumHeight(150)
        self.drop_area.setCursor(Qt.PointingHandCursor)
        self.drop_area.setToolTip("Drag & drop a video file here, or click to browse")
        d_l = QVBoxLayout(self.drop_area)
        d_l.setContentsMargins(4, 4, 4, 4)
        self.drop_label = QLabel('Drag & Drop\r\na Video File Here:')
        self.drop_label.setAlignment(Qt.AlignCenter)
        self.drop_label.setStyleSheet(f'color: {UI_COLORS.TEXT_PRIMARY}; font-size: 9px; font-weight: bold;')
        d_l.addWidget(self.drop_label, 1, Qt.AlignCenter)
        r_l.addWidget(self.drop_area)
        self.upload_button = QPushButton('📂  UPLOAD VIDEO')
        self.upload_button.setProperty('class', 'primary')
        self.upload_button.setCursor(Qt.PointingHandCursor)
        self.upload_button.setFixedSize(120, UI_LAYOUT.BUTTON_HEIGHT)
        self.upload_button.setToolTip("Browse files to upload a video")
        self.upload_button.clicked.connect(self.select_file)
        r_l.addWidget(self.upload_button, 0, Qt.AlignCenter)
        self.music_button = QPushButton('♪  ADD MUSIC')
        self.music_button.setProperty('class', 'primary')
        self.music_button.setCursor(Qt.PointingHandCursor)
        self.music_button.setFixedSize(120, UI_LAYOUT.BUTTON_HEIGHT)
        self.music_button.setToolTip("Open the background music synchronization wizard")
        self.music_button.clicked.connect(self.on_music_button_clicked)
        self.music_button.setEnabled(False)
        r_l.addWidget(self.music_button, 0, Qt.AlignCenter)
        r_l.addStretch(1)
        if hasattr(self, "hardware_status_label"):
            r_l.addWidget(self.hardware_status_label, 0, Qt.AlignCenter)
        self.no_fade_checkbox = QCheckBox('Disable Fade-In/Out')
        self.no_fade_checkbox.setStyleSheet(f'color: {UI_COLORS.TEXT_PRIMARY}; font-size: 9px;')
        self.no_fade_checkbox.setCursor(Qt.PointingHandCursor)
        self.no_fade_checkbox.setToolTip("Toggle automatic fade transitions at start/end")
        self.no_fade_checkbox.setEnabled(False)
        self.no_fade_checkbox.toggled.connect(lambda _checked: self._save_recovery_state() if hasattr(self, "_save_recovery_state") else None)
        r_l.addWidget(self.no_fade_checkbox, 0, Qt.AlignRight)
        bb = QVBoxLayout()
        bb.setContentsMargins(0, 0, 0, 0)
        bb.setSpacing(8)
        bb.setAlignment(Qt.AlignHCenter)
        self.merge_btn = QPushButton('VIDEO MERGER')
        self.crop_tool_btn = QPushButton('CROP SETTINGS')
        self.adv_editor_btn = QPushButton('ADVANCED VIDEO EDITOR')
        self.merge_btn.setToolTip("Open the multi-video merger tool")
        self.crop_tool_btn.setToolTip("Open crop and portrait configuration (F12)")
        self.adv_editor_btn.setToolTip("Launch the professional video editor")
        for b in (self.merge_btn, self.crop_tool_btn, self.adv_editor_btn):
            b.setProperty('class', 'primary')
            b.setFixedSize(145, 32)
            b.setStyleSheet("font-size: 10px; padding: 2px 4px; min-height: 25px; max-height: 25px;")
            b.setCursor(Qt.PointingHandCursor)
            bb.addWidget(b, 0, Qt.AlignHCenter)
        self.merge_btn.clicked.connect(self.launch_video_merger)
        self.crop_tool_btn.clicked.connect(self.launch_crop_tool)
        self.adv_editor_btn.clicked.connect(self.launch_advanced_editor)
        adv_editor_script = os.path.join(self.base_dir, 'advanced', 'advanced_video_editor.py')
        if not os.path.exists(adv_editor_script):
            self.adv_editor_btn.setEnabled(False)
            self.adv_editor_btn.setToolTip("Advanced Editor is not bundled with this build.")
            self.adv_editor_btn.setCursor(Qt.ArrowCursor)
        r_l.addLayout(bb)

    def _effective_project_duration_sec(self, trim_start_ms, trim_end_ms):
        base_speed = 1.0
        try:
            base_speed = max(0.001, float(self.speed_spinbox.value()))
        except Exception:
            base_speed = 1.0
        trim_start_ms = int(trim_start_ms)
        trim_end_ms = int(trim_end_ms)
        if trim_end_ms <= trim_start_ms:
            return 0.0
        granular_enabled = bool(getattr(self, "granular_checkbox", None) and self.granular_checkbox.isChecked())
        segments = list(getattr(self, "speed_segments", []) or []) if granular_enabled else []
        if not segments:
            return ((trim_end_ms - trim_start_ms) / 1000.0) / base_speed
        total_ms = 0.0
        cursor = trim_start_ms
        for seg in sorted(segments, key=lambda item: int(item.get("start_ms", item.get("start", 0)))):
            try:
                raw_start = int(seg.get("start_ms", seg.get("start", 0)))
                raw_end = int(seg.get("end_ms", seg.get("end", 0)))
                seg_speed = float(seg.get("speed", base_speed))
            except Exception:
                continue
            seg_start = max(trim_start_ms, raw_start, cursor)
            seg_end = min(trim_end_ms, raw_end)
            if seg_end <= seg_start:
                continue
            if seg_start > cursor:
                total_ms += (seg_start - cursor) / base_speed
            if abs(seg_speed) < 0.001:
                total_ms += seg_end - seg_start
            else:
                total_ms += (seg_end - seg_start) / max(0.001, seg_speed)
            cursor = max(cursor, seg_end)
        if cursor < trim_end_ms:
            total_ms += (trim_end_ms - cursor) / base_speed
        return max(0.001, total_ms / 1000.0)

    def _update_quality_label(self):
        if not hasattr(self, 'quality_value_label'):
            return
        if not getattr(self, 'input_file_path', None) or not os.path.exists(self.input_file_path):
            self.quality_value_label.setText('')
            return
        idx = int(self.quality_slider.value())
        dur_ms = getattr(self, 'trim_end_ms', 0) - getattr(self, 'trim_start_ms', 0)
        if dur_ms <= 0:
            self.quality_value_label.setText('')
            return
        try:
            if idx >= 20:
                self.quality_value_label.setText('Max CQ')
                self.quality_value_label.setStyleSheet('color: #2ecc71; font-weight: bold;')
                return
            else:
                target_mb = 5 + idx * 5
        except:
            target_mb = 5 + idx * 5
        dur_sec = self._effective_project_duration_sec(getattr(self, 'trim_start_ms', 0), getattr(self, 'trim_end_ms', 0))
        dur_sec += 0.1
        if dur_sec <= 0:
            self.quality_value_label.setText('')
            return
        audio_kbps = choose_audio_bitrate(192, dur_sec, target_mb)
        if self.mobile_checkbox.isChecked():
            w, h = 1080, 1920
        else:
            w, h = 1920, 1080
        fps = 60.0
        video_kbps = float(calculate_video_bitrate(self.input_file_path, dur_sec, audio_kbps, target_mb, False, None, f"{w}x{h}", "60", idx))
        bpp = video_kbps * 1000 / (max(1, w) * max(1, h) * fps)
        if not self.mobile_checkbox.isChecked():
            bpp /= 1.5
        spectrum = [(0.02, 'Unwatchable', '#e74c3c'), (0.04, 'Pixelated', '#e74c3c'), (0.06, 'Blurry', '#e74c3c'), (0.1, 'Clear', 'white'), (0.15, 'Sharp', '#2ecc71'), (0.25, 'Crisp-Clear', '#2ecc71'), (99.0, 'Lifelike', '#2ecc71')]
        desc, color = ('Standard', 'white')
        for i, (thresh, d, c) in enumerate(spectrum):
            if bpp < thresh:
                desc, color = (d, c)
                prev = spectrum[i - 1][0] if i > 0 else 0.0
                mid = (thresh + prev) / 2.0
                if thresh < 90.0:
                    desc += ' -' if bpp < mid else ' +'
                break
        self.quality_value_label.setText(desc)
        self.quality_value_label.setStyleSheet(f'color: {color}; font-weight: bold;')

    def _on_quality_slider_changed(self, *_args):
        self._update_quality_label()
        if hasattr(self, "_save_recovery_state"):
            self._save_recovery_state()

    def _on_mobile_toggled(self, checked: bool):
        self.teammates_checkbox.setVisible(checked)
        self.teammates_checkbox.setEnabled(checked)
        self.portrait_text_input.setVisible(checked)
        self.portrait_text_input.setEnabled(checked)
        if hasattr(self, 'portrait_mask_overlay') and self.portrait_mask_overlay:
            self.portrait_mask_overlay.setVisible(checked and bool(self.input_file_path))
            self._update_portrait_mask_overlay_state()
        self._update_quality_label()
        if hasattr(self, "_save_recovery_state"):
            self._save_recovery_state()

    def _update_portrait_mask_overlay_state(self):
        if not hasattr(self, 'portrait_mask_overlay') or not self.portrait_mask_overlay or getattr(self, 'is_processing', False):
            return
        res = getattr(self, 'original_resolution', '1920x1080')
        if self.mobile_checkbox.isChecked() and self.input_file_path:
            try:
                tl = self.video_surface.mapToGlobal(self.video_surface.rect().topLeft())
                self.portrait_mask_overlay.setGeometry(tl.x(), tl.y(), self.video_surface.width(), self.video_surface.height())
                self.portrait_mask_overlay.set_video_info(res, self.video_surface.size())
                self.portrait_mask_overlay.show()
                self.portrait_mask_overlay.raise_()
            except:
                pass
        else:
            self.portrait_mask_overlay.hide()

    def _init_status_bar(self):
        self.progress_bar = QProgressBar()
        self.progress_bar.setStyleSheet(UIStyles.PROGRESS_BAR)
        self.progress_bar.hide()
        self.hardware_status_label = QLabel('')
        self.hardware_status_label.setMinimumWidth(120)
        self.hardware_status_label.setMaximumWidth(128)
        self.hardware_status_label.setAlignment(Qt.AlignCenter)
        self.hardware_status_label.setWordWrap(True)
        self.hardware_status_label.setStyleSheet(UIStyles.LABEL_STATUS)
        self.hardware_status_label.hide()
        self.resolution_label = QLabel('')
        self.resolution_label.setStyleSheet(UIStyles.LABEL_STATUS)
        self._right_status_clear_timer = QTimer(self)
        self._right_status_clear_timer.setSingleShot(True)
        self._right_status_clear_timer.timeout.connect(self._clear_right_status_message)
        self.progress_update_signal.connect(self.on_progress)
        self.status_update_signal.connect(self.on_phase_update)
        self.process_finished_signal.connect(self.on_process_finished)
        self.video_ended_signal.connect(self._handle_video_end)
        try:
            from ui.main_window import _QtLiveLogHandler
            import logging
            h = _QtLiveLogHandler(self)
            h.setFormatter(logging.Formatter('%(asctime)s | %(message)s', '%H:%M:%S'))
            if hasattr(self, 'logger'):
                self.logger.addHandler(h)
        except:
            pass

    def _clear_right_status_message(self):
        label = getattr(self, "hardware_status_label", None)
        if label is None:
            return
        label.clear()
        label.hide()

    def _set_right_status_message(self, message, duration_ms=5000, color="#ecf0f1"):
        label = getattr(self, "hardware_status_label", None)
        if label is None:
            return False
        clean = str(message or "").strip()
        if not clean:
            self._clear_right_status_message()
            return True
        label.setText(clean)
        label.setStyleSheet(f"color: {color}; font-size: 9px; font-weight: bold; background: transparent; border: none; padding: 1px;")
        label.show()
        timer = getattr(self, "_right_status_clear_timer", None)
        if timer is not None:
            timer.stop()
            if duration_ms and duration_ms > 0:
                timer.start(int(duration_ms))
        return True
