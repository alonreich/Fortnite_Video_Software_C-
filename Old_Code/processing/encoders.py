import os
from fractions import Fraction
from typing import Any, Optional

class EncoderManager:
    ENCODER_PREFERENCE = ["h264_nvenc", "h264_amf", "h264_qsv", "libx264"]
    HARDWARE_BY_STRATEGY = {"NVIDIA": "h264_nvenc", "AMD": "h264_amf", "INTEL": "h264_qsv"}
    MAX_BITRATE_KBPS = 100000

    def _vbv_buf_kbps(self, kbps: int) -> int:
        return min(self.MAX_BITRATE_KBPS, max(kbps, kbps * 2))

    def _fps_fraction(self, fps_expr: str, default: str = "60") -> Fraction:
        try:
            if not fps_expr:
                return Fraction(str(default))
            fps = Fraction(str(fps_expr))
            if fps <= 0:
                return Fraction(str(default))
            return min(Fraction(60, 1), fps)
        except Exception:
            return Fraction(str(default))

    def __init__(self, logger: Any, hardware_strategy: Optional[str] = None, ffmpeg_path: Optional[str] = None):
        self.logger = logger
        if ffmpeg_path:
            self.ffmpeg_path = ffmpeg_path
        else:
            local_ffmpeg = os.path.abspath(os.path.join(os.path.dirname(__file__), '..', 'binaries', 'ffmpeg.exe'))
            self.ffmpeg_path = local_ffmpeg if os.path.exists(local_ffmpeg) else 'ffmpeg'
        self.available_encoders = self._detect_available_encoders()
        self.primary_encoder = os.environ.get('VIDEO_HW_ENCODER', 'h264_nvenc')
        self.forced_cpu = (os.environ.get('VIDEO_FORCE_CPU') == '1')
        self.hardware_strategy = hardware_strategy
        self._encoder_preflight_error: Optional[str] = None
        if hardware_strategy:
            requested_encoder = self.HARDWARE_BY_STRATEGY.get(str(hardware_strategy).upper())
            if requested_encoder:
                self.primary_encoder = requested_encoder
                self.forced_cpu = False
                if self.available_encoders and requested_encoder not in self.available_encoders:
                    self._encoder_preflight_error = f"Export blocked: {hardware_strategy} ({requested_encoder}) not in FFmpeg."
                    self.logger.error(self._encoder_preflight_error)
            elif hardware_strategy == "CPU":
                self.primary_encoder = "libx264"
                self.forced_cpu = True
            else:
                self.primary_encoder = "libx264"
                self.forced_cpu = True
        self.attempted_encoders: set[str] = set()

    def get_encoder_preflight_error(self) -> Optional[str]:
        return self._encoder_preflight_error

    def _detect_available_encoders(self) -> set[str]:
        import subprocess
        try:
            startupinfo = None
            if os.name == 'nt':
                startupinfo = subprocess.STARTUPINFO()
                startupinfo.dwFlags |= subprocess.STARTF_USESHOWWINDOW
            res = subprocess.run([self.ffmpeg_path, '-hide_banner', '-encoders'], 
                                capture_output=True, text=True, 
                                startupinfo=startupinfo,
                                creationflags=0x08000000 if os.name == 'nt' else 0,
                                timeout=5)
            found = set()
            for line in res.stdout.splitlines():
                if 'h264_nvenc' in line: found.add('h264_nvenc')
                if 'h264_amf' in line: found.add('h264_amf')
                if 'h264_qsv' in line: found.add('h264_qsv')
            return found
        except Exception:
            return set()

    def get_initial_encoder(self):
        if self.forced_cpu: return "libx264"
        return self.primary_encoder

    def get_fallback_list(self, failed_encoder: str, allow_cpu: bool = True) -> list[str]:
        self.attempted_encoders.add(failed_encoder)
        if str(self.hardware_strategy or "").upper() in self.HARDWARE_BY_STRATEGY:
            return []
        try:
            start_index = self.ENCODER_PREFERENCE.index(failed_encoder) + 1
        except ValueError:
            start_index = 0
        fallback_options: list[str] = []
        for i in range(start_index, len(self.ENCODER_PREFERENCE)):
            encoder = self.ENCODER_PREFERENCE[i]
            if not allow_cpu and encoder == "libx264": continue
            if encoder != "libx264" and self.available_encoders and encoder not in self.available_encoders: continue
            if encoder not in self.attempted_encoders:
                fallback_options.append(encoder)
        return fallback_options

    def get_codec_flags(self, encoder_name: str, video_bitrate_kbps: Optional[int], effective_duration_sec: float, fps_expr: str = "60000/1001", quality_level: int = 2, size_locked: bool = True) -> tuple[list[str], str]:
        fps_value = self._fps_fraction(fps_expr)
        strict_gpu = str(self.hardware_strategy or "").upper() in self.HARDWARE_BY_STRATEGY
        if encoder_name == "libx264" and strict_gpu and not self.forced_cpu:
            raise RuntimeError("CPU encoder requested in strict GPU mode.")
        if self.forced_cpu:
            cpu_preset = 'fast' if quality_level <= 1 else 'medium'
            flags = ['-c:v', 'libx264', '-preset', cpu_preset, '-pix_fmt', 'yuv420p', '-profile:v', 'high', '-level:v', '5.1', '-bf', '2']
            if video_bitrate_kbps is None:
                crf = '20' if quality_level <= 1 else '17'
                flags.extend(['-crf', crf])
                return flags, f"CPU ({cpu_preset}/CRF{crf})"
            kbps = min(self.MAX_BITRATE_KBPS, max(300, int(video_bitrate_kbps)))
            flags.extend(['-b:v', f'{kbps}k', '-maxrate', f'{kbps}k', '-bufsize', f'{self._vbv_buf_kbps(kbps)}k'])
            return flags, f"CPU ({cpu_preset}/{kbps}k)"
        vcodec = ['-c:v', encoder_name]
        gop = str(int((fps_value * 2) + Fraction(1, 2)))
        keyint_min = str(int(fps_value + Fraction(1, 2)))
        vcodec.extend(['-g', gop, '-keyint_min', keyint_min])
        if encoder_name == 'h264_nvenc':
            nv_preset, multipass, lookahead, aq_strength = ('p7', 'fullres', '32', '10') if quality_level >= 2 else ('p6', 'fullres', '24', '9')
            target_kbps = int(video_bitrate_kbps) if video_bitrate_kbps else 0
            vcodec.extend([
                '-pix_fmt', 'yuv420p', '-preset', nv_preset, '-tune', 'hq',
                '-rc', 'cbr' if size_locked and target_kbps else 'vbr',
                '-multipass', multipass, '-spatial-aq', '1', '-temporal-aq', '1',
                '-aq-strength', aq_strength, '-bf', '2', '-b_ref_mode', 'middle',
                '-weighted_pred', '0', '-nonref_p', '0', '-strict_gop', '1',
                '-forced-idr', '1', '-rc-lookahead', lookahead, '-profile:v', 'high', '-level:v', '5.1'
            ])
            if video_bitrate_kbps:
                kbps = min(self.MAX_BITRATE_KBPS, max(300, int(video_bitrate_kbps)))
                vcodec.extend(['-b:v', f'{kbps}k', '-maxrate', f'{kbps}k', '-bufsize', f'{self._vbv_buf_kbps(kbps)}k'])
                if size_locked: vcodec.extend(['-cbr', '1', '-cbr_padding', '1'])
                rc_label = f"NVENC {nv_preset}/{multipass} ({'CBR' if size_locked else 'VBR'})"
            else:
                cq_val = '22' if quality_level <= 1 else ('15' if quality_level >= 20 else '19')
                vcodec.extend(['-cq', cq_val])
                rc_label = f"NVENC {nv_preset}/{multipass} (CQ {cq_val})"
        elif encoder_name == 'h264_amf':
            amf_quality = 'balanced' if quality_level <= 1 else 'quality'
            vcodec.extend([
                '-pix_fmt', 'yuv420p', '-usage', 'transcoding', '-quality', amf_quality,
                '-rc', 'cbr' if size_locked and video_bitrate_kbps else 'vbr_peak',
                '-enforce_hrd', '1', '-vbaq', '1', '-bf', '2', '-profile:v', 'high', '-level:v', '5.1'
            ])
            if video_bitrate_kbps:
                kbps = min(self.MAX_BITRATE_KBPS, max(300, int(video_bitrate_kbps)))
                vcodec.extend(['-b:v', f'{kbps}k', '-maxrate', f'{kbps}k', '-bufsize', f'{self._vbv_buf_kbps(kbps)}k'])
            rc_label = f"AMD AMF {amf_quality}"
        elif encoder_name == 'h264_qsv':
            qsv_preset = 'balanced' if quality_level <= 1 else 'slow'
            vcodec.extend([
                '-pix_fmt', 'yuv420p', '-preset', qsv_preset, '-bf', '2', '-look_ahead', '1',
                '-look_ahead_depth', '60' if quality_level <= 1 else '100', '-profile:v', 'high', '-level:v', '5.1'
            ])
            if video_bitrate_kbps:
                kbps = min(self.MAX_BITRATE_KBPS, max(300, int(video_bitrate_kbps)))
                vcodec.extend(['-b:v', f'{kbps}k', '-maxrate', f'{kbps}k', '-bufsize', f'{self._vbv_buf_kbps(kbps)}k'])
            rc_label = f"Intel QSV {qsv_preset}"
        elif encoder_name == 'libx264':
            cpu_preset = 'veryfast' if quality_level <= 0 else ('fast' if quality_level <= 1 else 'medium')
            vcodec.append('-pix_fmt'); vcodec.append('yuv420p')
            if video_bitrate_kbps is None:
                crf = '23' if quality_level <= 0 else ('20' if quality_level <= 1 else '17')
                vcodec.extend(['-preset', cpu_preset, '-crf', crf, '-bf', '2', '-profile:v', 'high', '-level:v', '5.1'])
                return vcodec, f"CPU libx264 ({cpu_preset}/CRF{crf})"
            else:
                kbps = min(self.MAX_BITRATE_KBPS, max(300, int(video_bitrate_kbps)))
                vcodec.extend(['-preset', cpu_preset, '-bf', '2', '-b:v', f'{kbps}k', '-maxrate', f'{kbps}k', '-bufsize', f'{self._vbv_buf_kbps(kbps)}k', '-profile:v', 'high', '-level:v', '5.1'])
                return vcodec, f"CPU libx264 ({cpu_preset})"
        else:
            vcodec.extend(['-pix_fmt', 'yuv420p'])
            rc_label = f"{encoder_name} (Generic)"
        return vcodec, rc_label

