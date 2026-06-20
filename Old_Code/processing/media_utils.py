import os
import sys
import subprocess
import json
from fractions import Fraction

class MediaProber:
    def __init__(self, bin_dir, input_path):
        self.bin_dir = bin_dir
        self.input_path = input_path
        self.ffprobe_path = os.path.join(self.bin_dir, 'ffprobe.exe')
        self.probe_timeout = 8.0

    def _run_command(self, args):
        try:
            full_cmd = [self.ffprobe_path, "-v", "error", "-of", "default=nw=1:nk=1"] + args + [self.input_path]
            creationflags = 0
            if sys.platform == "win32":
                creationflags = subprocess.CREATE_NO_WINDOW
            result = subprocess.run(
                full_cmd, 
                capture_output=True, 
                text=True, 
                check=True,
                creationflags=creationflags,
                timeout=self.probe_timeout
            )
            return (result.stdout or "").strip()
        except subprocess.TimeoutExpired:
            return None
        except Exception:
            return None

    def _run_json(self, args):
        try:
            full_cmd = [self.ffprobe_path, "-v", "error", "-of", "json"] + args + [self.input_path]
            creationflags = 0
            if sys.platform == "win32":
                creationflags = subprocess.CREATE_NO_WINDOW
            result = subprocess.run(
                full_cmd,
                capture_output=True,
                text=True,
                check=True,
                creationflags=creationflags,
                timeout=self.probe_timeout
            )
            return json.loads(result.stdout or "{}")
        except subprocess.TimeoutExpired:
            return {}
        except Exception:
            return {}

    def run_probe(self, args):
        val_str = self._run_command(args)
        if val_str:
            try:
                val = float(val_str)
                if val > 0:
                    return max(8, int(round(val / 1000.0)))
            except ValueError:
                return None
        return None

    def get_audio_bitrate(self):
        kbps = self.run_probe(["-select_streams", "a:0", "-show_entries", "stream=bit_rate"])
        if kbps and 8 <= kbps <= 1536:
            return kbps
        kbps = self.run_probe(["-show_entries", "format=bit_rate"])
        if kbps and 8 <= kbps <= 1536:
            return kbps
        return None

    def has_audio(self):
        val_str = self._run_command(["-select_streams", "a:0", "-show_entries", "stream=index"])
        return bool(val_str)

    def get_sample_rate(self):
        val_str = self._run_command(["-select_streams", "a:0", "-show_entries", "stream=sample_rate"])
        if val_str:
            try:
                return int(val_str)
            except ValueError:
                return 48000
        return 48000

    def get_video_bitrate(self):
        kbps = self.run_probe(["-select_streams", "v:0", "-show_entries", "stream=bit_rate"])
        if kbps: return kbps
        kbps = self.run_probe(["-show_entries", "format=bit_rate"])
        return kbps or 25000

    def get_duration(self):
        val_str = self._run_command(["-show_entries", "format=duration"])
        try:
            return float(val_str)
        except (TypeError, ValueError):
            return 0.0

    def get_resolution(self):
        w = self._run_command(["-select_streams", "v:0", "-show_entries", "stream=width"])
        h = self._run_command(["-select_streams", "v:0", "-show_entries", "stream=height"])
        if w and h:
            return f"{w}x{h}"
        return None

    def get_video_timing_info(self):
        data = self._run_json([
            "-select_streams", "v:0",
            "-show_entries", "stream=avg_frame_rate,r_frame_rate,nb_frames,duration:format=duration"
        ])
        streams = data.get("streams") or []
        stream = streams[0] if streams else {}
        fmt = data.get("format") or {}

        def _rate_fraction(name):
            raw = stream.get(name)
            try:
                val = Fraction(str(raw))
                if val > 0:
                    return val
            except Exception:
                return Fraction(0, 1)
            return Fraction(0, 1)

        def _rate(name):
            val = _rate_fraction(name)
            if val > 0:
                return float(val)
            return 0.0

        def _float(name):
            try:
                val = float(stream.get(name))
                if val > 0:
                    return val
            except Exception:
                return 0.0
            return 0.0

        def _int(name):
            try:
                val = int(stream.get(name))
                if val > 0:
                    return val
            except Exception:
                return 0
            return 0
        avg_q = _rate_fraction("avg_frame_rate")
        nominal_q = _rate_fraction("r_frame_rate")
        avg = float(avg_q) if avg_q > 0 else 0.0
        nominal = float(nominal_q) if nominal_q > 0 else 0.0
        duration = _float("duration")
        if duration <= 0:
            try:
                duration = float(fmt.get("duration") or 0.0)
            except Exception:
                duration = 0.0
        if duration <= 0:
            duration = self.get_duration()
        frames = _int("nb_frames")
        counted = (frames / duration) if frames and duration > 0 else 0.0
        observed = counted or avg or nominal
        vfr = False
        if avg and nominal and abs(avg - nominal) > 0.5:
            vfr = True
        if counted and avg and abs(counted - avg) > 0.5:
            vfr = True
        return {
            "avg_fps": avg,
            "nominal_fps": nominal,
            "counted_fps": counted,
            "observed_fps": observed,
            "duration": duration,
            "frame_count": frames,
            "is_vfr": vfr,
            "avg_fps_expr": str(avg_q) if avg_q > 0 else "",
            "nominal_fps_expr": str(nominal_q) if nominal_q > 0 else "",
        }

    def get_video_fps_expr(self, fallback: str = "60000/1001"):
        try:
            info = self.get_video_timing_info()
            candidates = []
            for key in ("counted_fps", "avg_fps_expr", "nominal_fps_expr"):
                raw = info.get(key)
                if not raw:
                    continue
                try:
                    candidates.append(Fraction(str(raw)).limit_denominator(1001))
                except Exception:
                    continue
            fps_q = next((q for q in candidates if q > 1), Fraction(0, 1))
            if fps_q <= 1:
                return fallback
            if info.get("is_vfr") and fps_q > 55:
                fps_q = Fraction(60, 1)
            if fps_q > 60:
                if abs(fps_q - Fraction(120000, 1001)) < Fraction(1, 10) or abs(fps_q - Fraction(240000, 1001)) < Fraction(1, 10):
                    return "60000/1001"
                return "60"
            if abs(fps_q - Fraction(60, 1)) < Fraction(1, 1000): return "60"
            if abs(fps_q - Fraction(60000, 1001)) < Fraction(1, 100): return "60000/1001"
            if abs(fps_q - Fraction(30, 1)) < Fraction(1, 1000): return "30"
            if abs(fps_q - Fraction(30000, 1001)) < Fraction(1, 100): return "30000/1001"
            if abs(fps_q - Fraction(24, 1)) < Fraction(1, 1000): return "24"
            if abs(fps_q - Fraction(24000, 1001)) < Fraction(1, 100): return "24000/1001"
            fps_q = min(Fraction(60, 1), fps_q).limit_denominator(1001)
            if fps_q.denominator == 1:
                return str(fps_q.numerator)
            return f"{fps_q.numerator}/{fps_q.denominator}"
        except Exception:
            return fallback

def check_encoder_capability(ffmpeg_path: str, encoder_name: str, logger=None, hardware_scan_details=None) -> bool:
    try:
        cmd = [
            ffmpeg_path, "-y", "-hide_banner", "-loglevel", "error",
            "-f", "lavfi", "-i", "color=c=black:s=1920x1080",
            "-vframes", "1", "-c:v", encoder_name, "-f", "null", "-"
        ]
        startupinfo = None
        if sys.platform == "win32":
            startupinfo = subprocess.STARTUPINFO()
            startupinfo.dwFlags |= subprocess.STARTF_USESHOWWINDOW
            startupinfo.wShowWindow = subprocess.SW_HIDE
        creationflags = 0
        if sys.platform == "win32":
            creationflags = subprocess.CREATE_NO_WINDOW
        try:
            result = subprocess.run(
                cmd,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                startupinfo=startupinfo,
                creationflags=creationflags,
                timeout=10.0
            )
            if result.returncode == 0:
                return True
            else:
                if hardware_scan_details is not None:
                    hardware_scan_details["errors"][encoder_name] = result.stderr.decode(errors="ignore")[:500]
                return False
        except subprocess.TimeoutExpired:
            result2 = subprocess.run(
                cmd,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                startupinfo=startupinfo,
                creationflags=creationflags,
                timeout=5.0
            )
            if result2.returncode == 0:
                return True
            if hardware_scan_details is not None:
                hardware_scan_details["timed_out"].append(encoder_name)
            return False
    except Exception as e:
        if hardware_scan_details is not None:
            hardware_scan_details["errors"][encoder_name] = str(e)
        return False

def choose_audio_bitrate(source_audio_kbps, duration_sec, target_mb):
    source = int(source_audio_kbps or 192)
    source = min(320, max(128, source))
    if not target_mb or duration_sec <= 0:
        return min(320, max(192, source))
    total_kbps = (Fraction(str(target_mb)) * 8192) / max(Fraction(1, 1000), Fraction(str(duration_sec)))
    if total_kbps < 900:
        return 128
    if total_kbps < 1800:
        return min(160, source)
    if total_kbps < 3200:
        return min(192, max(160, source))
    return min(256, max(192, source))

def calculate_video_bitrate(input_path, duration, audio_kbps, target_mb, keep_highest_res, logger=None, res_str="1920x1080", fps_expr="60", quality_level=2, prober=None):
    max_h264_bitrate_kbps = 100000
    audio_kbps = min(320, max(128, int(audio_kbps or 128)))
    if duration <= 0:
        return 6000
    if target_mb is None and keep_highest_res and prober:
        orig_br = prober.get_video_bitrate()
        return min(int(orig_br * 1.05), max_h264_bitrate_kbps)
    if target_mb is None:
        target_mb = 45.0
    duration_q = Fraction(str(duration))
    target_mb_q = Fraction(str(target_mb))
    total_bits_available = target_mb_q * 8 * 1024 * 1024
    audio_bits_total = Fraction(audio_kbps * 1000) * duration_q
    mux_margin_bits = max(total_bits_available / 100, Fraction(64 * 1024 * 8))
    video_bits = total_bits_available - audio_bits_total - mux_margin_bits
    if video_bits <= 0:
        if logger:
            logger.info(f"BITRATE: Target {target_mb}MB is below audio budget. Clamping to 300k.")
        return 300
    calculated_kbps = int(video_bits / (1000 * duration_q))
    try:
        w, h = map(int, res_str.lower().split('x'))
    except (AttributeError, TypeError, ValueError):
        w, h = 1920, 1080
    try:
        fps_q = Fraction(str(fps_expr))
    except (TypeError, ValueError, ZeroDivisionError):
        fps_q = Fraction(60, 1)
    fps_q = min(Fraction(60, 1), max(Fraction(1, 1), fps_q))
    bpp_targets = [Fraction(6, 100), Fraction(9, 100), Fraction(13, 100), Fraction(18, 100)]
    target_bpp = bpp_targets[max(0, min(int(quality_level), 3))]
    min_quality_kbps = int((w * h * fps_q * target_bpp) / 1000)
    final_kbps = max(300, min(calculated_kbps, max_h264_bitrate_kbps))
    if logger:
        if min_quality_kbps > final_kbps:
            logger.info(f"BITRATE: Target {target_mb}MB caps video below floor {min_quality_kbps}k | Final {final_kbps}k")
        logger.info(f"BITRATE: Target {target_mb}MB | Dur {duration:.2f}s | Calc {calculated_kbps}k | Cap {max_h264_bitrate_kbps}k | Final {final_kbps}k")
    return final_kbps
