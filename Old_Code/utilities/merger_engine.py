import os
import queue
import re
import subprocess
import threading
from PyQt5.QtCore import QThread, pyqtSignal
from utilities.merger_utils import _get_logger, kill_process_tree

class MergerEngine(QThread):
    H264_LEVEL_51_MAX_BPS = 100_000_000
    H264_LEVEL_51_MIN_BPS = 300_000
    progress = pyqtSignal(int, str)
    finished = pyqtSignal(bool, str)
    log_line = pyqtSignal(str)

    def __init__(self, ffmpeg_path, cmd_base, output_path, total_duration_sec=0, use_gpu=False, target_v_bitrate=0, target_a_bitrate=0, target_a_rate=48000, quality_level=4):
        super().__init__()
        self.ffmpeg_path = ffmpeg_path
        self.cmd_base = cmd_base
        self.output_path = output_path
        self.total_duration = max(1.0, float(total_duration_sec))
        self.use_gpu = use_gpu
        self.target_v_bitrate = target_v_bitrate
        self.target_a_bitrate = target_a_bitrate
        self.target_a_rate = target_a_rate
        self.quality_level = quality_level
        self.logger = _get_logger()
        self._process = None
        self._is_cancelled = False
        self._last_time_str = "00:00:00"

    def _cmd_base_with_decode_flags(self):
        return list(self.cmd_base)

    def _video_bitrate_args(self, multiplier):
        if self.target_v_bitrate <= 0:
            return []
        requested = int(self.target_v_bitrate * multiplier)
        effective = max(self.H264_LEVEL_51_MIN_BPS, min(self.H264_LEVEL_51_MAX_BPS, requested))
        if effective != requested:
            self.logger.info(
                "GPU: Clamped merger video bitrate from %s to %s for H.264 Level 5.1.",
                requested,
                effective,
            )
        bufsize = min(self.H264_LEVEL_51_MAX_BPS, max(effective, effective * 2))
        return ["-b:v", str(effective), "-maxrate:v", str(effective), "-bufsize:v", str(bufsize)]

    def _detect_gpu_encoder(self):
        quality_multipliers = {0: 0.20, 1: 0.40, 2: 0.60, 3: 0.80, 4: 1.0}
        mult = quality_multipliers.get(self.quality_level, 1.0)
        crf_map = {4: 22, 3: 26, 2: 30, 1: 34, 0: 40}
        crf_val = crf_map.get(self.quality_level, 26)
        v_bitrate_args = self._video_bitrate_args(mult)
        if not self.use_gpu:
            return self._get_cpu_flags(crf_val, v_bitrate_args)
        try:
            cmd = [self.ffmpeg_path, "-hide_banner", "-encoders"]
            flags = subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0
            res = subprocess.run(cmd, capture_output=True, text=True, creationflags=flags, timeout=5)
            out = res.stdout
            if re.search(r"\s+h264_nvenc\s+", out):
                self.logger.info(f"GPU: NVIDIA NVENC detected. Quality Level: {self.quality_level}")
                nv_preset = "p7" if self.quality_level >= 4 else "p6"
                base = ["-c:v", "h264_nvenc", "-preset", nv_preset, "-tune", "hq", "-pix_fmt", "yuv420p", "-profile:v", "high", "-level:v", "5.1", "-spatial-aq", "1", "-temporal-aq", "1", "-aq-strength", "10" if nv_preset == "p7" else "9", "-bf", "2", "-b_ref_mode", "middle", "-weighted_pred", "0", "-nonref_p", "0", "-strict_gop", "1", "-forced-idr", "1", "-rc-lookahead", "64" if nv_preset == "p7" else "48", "-multipass", "fullres"]
                if not v_bitrate_args: base.extend(["-cq", str(crf_val)])
                else: base.extend(["-rc", "cbr"] + v_bitrate_args + ["-cbr", "1", "-cbr_padding", "1"])
                return base
            elif re.search(r"\s+h264_amf\s+", out):
                self.logger.info(f"GPU: AMD AMF detected. Quality Level: {self.quality_level}")
                base = ["-c:v", "h264_amf", "-quality", "quality", "-pix_fmt", "yuv420p", "-profile:v", "high", "-level:v", "5.1", "-vbaq", "1"]
                if not v_bitrate_args: base.extend(["-rc", "cqp", "-qp_i", str(crf_val), "-qp_p", str(crf_val)])
                else: base.extend(v_bitrate_args)
                return base
            elif re.search(r"\s+h264_qsv\s+", out):
                self.logger.info(f"GPU: Intel QSV detected. Quality Level: {self.quality_level}")
                base = ["-c:v", "h264_qsv", "-preset", "slow", "-pix_fmt", "yuv420p", "-profile:v", "high", "-level:v", "5.1"]
                if not v_bitrate_args: base.extend(["-global_quality", str(crf_val)])
                else: base.extend(v_bitrate_args)
                return base
        except Exception as e:
            self.logger.warning(f"GPU Probe failed: {e}")
        if self.use_gpu:
            raise RuntimeError("GPU encoding was requested, but no H.264 hardware encoder was exposed.")
        return self._get_cpu_flags(crf_val, v_bitrate_args)

    def _audio_bitrate_kbps(self):
        raw = self.target_a_bitrate
        try:
            text = str(raw).strip().lower()
            if text.endswith("k"):
                value = int(float(text[:-1]))
            else:
                value = int(float(text)) if text else 128
                if value > 5000:
                    value = int(round(value / 1000.0))
        except Exception:
            value = 128
        return max(64, min(320, value))

    def _get_cpu_flags(self, crf_val, v_bitrate_args):
        base = ["-c:v", "libx264", "-preset", "medium", "-pix_fmt", "yuv420p", "-profile:v", "high", "-level:v", "5.1"]
        if not v_bitrate_args:
            base.extend(["-crf", str(crf_val)])
        else:
            base.extend(v_bitrate_args)
        return base

    def run(self):
        while True:
            self._is_cancelled = False
            cmd = [self.ffmpeg_path, "-y", "-hide_banner", "-progress", "pipe:1"] + self._cmd_base_with_decode_flags()
            a_bitrate = f"{self._audio_bitrate_kbps()}k"
            a_rate = f"{self.target_a_rate}" if self.target_a_rate > 0 else "48000"
            cmd.extend(["-c:a", "aac", "-ar", a_rate, "-b:a", a_bitrate])
            try:
                video_flags = self._detect_gpu_encoder()
                used_cpu = len(video_flags) >= 2 and video_flags[1] == "libx264"
                cmd.extend(video_flags)
            except Exception as e:
                if self.use_gpu:
                    msg = f"GPU setup failed and CPU fallback is disabled: {e}"
                    self.logger.error(msg)
                    self.finished.emit(False, msg)
                    return
                self.finished.emit(False, str(e))
                return
            cmd.append(str(self.output_path))
            self.logger.info(f"ENGINE: Executing: {' '.join(cmd)}")
            startupinfo = None
            if os.name == 'nt':
                startupinfo = subprocess.STARTUPINFO()
                startupinfo.dwFlags |= subprocess.STARTF_USESHOWWINDOW
            try:
                self._process = subprocess.Popen(
                    cmd,
                    stdout=subprocess.PIPE,
                    stderr=subprocess.STDOUT,
                    stdin=subprocess.DEVNULL,
                    universal_newlines=True,
                    encoding='utf-8',
                    errors='replace',
                    startupinfo=startupinfo,
                    creationflags=subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0
                )
            except Exception as e:
                self.finished.emit(False, f"Failed to start FFmpeg: {e}")
                return
            log_queue = queue.Queue()

            def _reader_thread(proc, q):
                try:
                    for line in iter(proc.stdout.readline, ''):
                        q.put(line)
                    proc.stdout.close()
                except: pass
            t = threading.Thread(target=_reader_thread, args=(self._process, log_queue))
            t.daemon = True
            t.start()
            log_buffer = []
            while True:
                if self._is_cancelled:
                    break
                try:
                    line = log_queue.get(timeout=0.1)
                    line = line.strip()
                    if not line: continue
                    self.log_line.emit(line)
                    if '=' in line:
                        self._parse_progress_v2(line)
                    else:
                        self._parse_progress(line)
                    log_buffer.append(line)
                    if len(log_buffer) > 100:
                        log_buffer.pop(0)
                except queue.Empty:
                    if not t.is_alive():
                        break
            if self._is_cancelled:
                self._kill_process()
                self.finished.emit(False, "Cancelled by user.")
                return
            self._process.wait()
            rc = self._process.returncode
            self.logger.info(f"FFMPEG LOG DUMP:\n" + "\n".join(log_buffer))
            if rc == 0:
                if os.path.exists(self.output_path) and os.path.getsize(self.output_path) > 0:
                    self.finished.emit(True, str(self.output_path))
                else:
                    self.finished.emit(False, "Render complete but output file is empty.")
            else:
                important = [
                    l for l in log_buffer
                    if re.search(r"error|failed|invalid|unable|cannot|no such", l, re.IGNORECASE)
                ]
                err_msg = "\n".join(important[-12:] if important else log_buffer[-12:]) or f"Exit Code {rc}"
                self.logger.error(f"FFMPEG ERROR OUTPUT:\n" + err_msg)
                if self.use_gpu and not used_cpu:
                    self.logger.error("Hardware encode failed during merge; CPU fallback is disabled.")
                    self.finished.emit(False, f"Hardware Encoding Failed (CPU fallback disabled):\n{err_msg}")
                    return
                self.finished.emit(False, f"Encoding Failed:\n{err_msg}")
            break

    def _parse_progress_v2(self, line):
        if 'out_time_us=' in line:
            try:
                _, val = line.split('=')
                us = int(val)
                current_sec = us / 1000000.0
                pct = int((current_sec / self.total_duration) * 100)
                pct = max(0, min(100, pct))
                self.progress.emit(pct, f"{int(current_sec)}s")
            except Exception:
                pass

    def _parse_progress(self, line):
        if "time=" in line:
            try:
                match = re.search(r'time=\s*(\d+):(\d+):(\d+(?:\.\d+)?)', line)
                if match:
                    h, m, s = map(float, match.groups())
                    current_sec = h*3600 + m*60 + s
                    pct = int((current_sec / self.total_duration) * 100)
                    pct = max(0, min(100, pct))
                    self._last_time_str = f"{int(h):02}:{int(m):02}:{int(s):02}"
                    self.progress.emit(pct, self._last_time_str)
            except (ValueError, TypeError, ZeroDivisionError, AttributeError) as e:
                self.logger.debug(f"Progress parse failed: {e}")

    def cancel(self):
        self._is_cancelled = True
        self._kill_process()

    def _kill_process(self):
        if self._process:
            kill_process_tree(self._process.pid)
