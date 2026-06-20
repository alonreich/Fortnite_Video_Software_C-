import re
import os
try:
    from PyQt5.QtGui import QFont, QFontMetrics, QGuiApplication
    HAS_QT = True
except ImportError:
    HAS_QT = False
try:
    from PIL import ImageFont
    HAS_PIL = True
except ImportError:
    HAS_PIL = False
try:
    from bidi.algorithm import get_display
    HAS_BIDI_LIB = True
except ImportError:
    HAS_BIDI_LIB = False

def is_pure_rtl(text: str) -> bool:
    if not text:
        return False
    has_rtl = any("\u0590" <= c <= "\u06ff" for c in text)
    has_latin = any(c.isalpha() and c <= "\u007f" for c in text)
    return has_rtl and not has_latin

def fix_hebrew_text(text: str) -> str:
    if not text:
        return ""
    return text[::-1]

def apply_bidi_formatting(text: str) -> str:
    if not text:
        return ""
    if HAS_BIDI_LIB:
        try:
            pure_rtl = is_pure_rtl(text)
            base_dir = 'R' if pure_rtl else 'L'
            return "\n".join([get_display(line, base_dir=base_dir) for line in text.split('\n')])
        except Exception:
            pass

    def _reverse_line_robust(line: str) -> str:
        if not line: return ""
        if not any("\u0590" <= c <= "\u06ff" for c in line):
            return line
        blocks = re.findall(r'([\u0590-\u06ff]+|[^\u0590-\u06ff]+)', line)
        processed_blocks = []
        for b in blocks:
            if any("\u0590" <= c <= "\u06ff" for c in b):
                processed_blocks.append(b[::-1])
            else:
                processed_blocks.append(b)
        return "".join(processed_blocks[::-1])
    return "\n".join([_reverse_line_robust(line) for line in text.split('\n')])

class TextWrapper:
    def __init__(self, config):
        self.cfg = config
        self.MAX_LINE_W = config.wrap_at_px
        self.SAFE_MAX_W = config.safe_max_px
        self.use_qt = False
        self.use_pil = False
        if HAS_QT and QGuiApplication.instance():
            self.use_qt = True
        elif HAS_PIL:
            self.use_pil = True
            try:
                self._pil_font_path = "arial.ttf" 
            except:
                pass
        
    def _measure_px(self, s: str, px_size: int) -> int:
        if not s: return 0
        fudge = getattr(self.cfg, "measure_fudge", 1.12)
        if self.use_qt:
            f = QFont("Arial")
            f.setBold(True) 
            f.setPixelSize(int(px_size))
            fm = QFontMetrics(f)
            try:
                w = int(fm.horizontalAdvance(s))
            except Exception:
                w = int(fm.width(s))
            return int(w * fudge) + self.cfg.shadow_pad_px
        if self.use_pil:
            try:
                font = ImageFont.truetype("arial.ttf", int(px_size))
                if hasattr(font, 'getlength'):
                    return int(font.getlength(s) * (fudge * 1.1)) + self.cfg.shadow_pad_px
                return int(font.getsize(s)[0] * (fudge * 1.1)) + self.cfg.shadow_pad_px
            except Exception:
                pass
        return int(len(s) * (px_size * (fudge * 0.7))) + self.cfg.shadow_pad_px

    def _split_long_token(self, tok: str, px_size: int, max_w: int):
        if self._measure_px(tok, px_size) <= max_w:
            return [tok]
        chunks = []
        cur = ""
        for ch in tok:
            cand = cur + ch
            width = self._measure_px(cand, px_size)
            if cur and width > max_w:
                chunks.append(cur)
                cur = ch
            else:
                cur = cand
        if cur:
            chunks.append(cur)
        return chunks

    def _wrap_text(self, s: str, px_size: int, max_w: int):
        tokens = []
        raw_tokens = (s or "").strip().split()
        for t in raw_tokens:
            if self._measure_px(t, px_size) > max_w:
                sub_tokens = self._split_long_token(t, px_size, max_w)
                tokens.extend(sub_tokens)
            else:
                tokens.append(t)
        lines = []
        cur = ""
        for t in tokens:
            cand = t if not cur else (cur + " " + t)
            if not cur or self._measure_px(cand, px_size) <= max_w:
                cur = cand
            else:
                lines.append(cur)
                cur = t
        if cur:
            lines.append(cur)
        return lines if lines else [""]

    def fit_and_wrap(self, s: str, target_width: int = None, logger=None):
        base_max_w = target_width if target_width else self.MAX_LINE_W
        is_portrait = (base_max_w < 1100)
        max_w = base_max_w
        base_size = self.cfg.base_font_size
        words = (s or "").strip().split()
        if is_portrait and len(words) >= 1 and len(words) <= 2:
            boost_size = base_size + 2
            lines = self._wrap_text(s, boost_size, max_w)
            if len(lines) == 1:
                widest = self._measure_px(lines[0], boost_size)
                if widest <= max_w:
                    if logger: logger.info(f"WRAP_PORTRAIT_BOOST: size={boost_size}")
                    return boost_size, lines
        MAX_TOTAL_H = 148 
        if logger: logger.info(f"WRAP_START: text='{s}' target_w={max_w} base_size={base_size} portrait={is_portrait}")
        if is_portrait:
            for size in range(base_size, base_size - 15, -1):
                lines = self._wrap_text(s, size, max_w)
                if len(lines) == 1:
                    widest = self._measure_px(lines[0], size)
                    if widest <= max_w:
                        if logger: logger.info(f"WRAP_PORTRAIT_1_LINE: size={size}")
                        return size, lines
            for num_lines in [2, 3]:
                gap_factor = 0.2 if num_lines == 2 else 0.3
                for size in range(base_size, self.cfg.min_font_size - 1, -1):
                    lines = self._wrap_text(s, size, max_w)
                    if len(lines) <= num_lines:
                        h_val = size * 1.20 
                        total_h = h_val + (len(lines) - 1) * (h_val * (1.0 - gap_factor))
                        widest = max(self._measure_px(ln, size) for ln in lines) if lines else 0
                        if widest <= max_w and total_h <= MAX_TOTAL_H:
                            if logger: logger.info(f"WRAP_PORTRAIT_MULTI: size={size} rows={len(lines)} factor={gap_factor}")
                            return size, lines
        size = base_size
        for i in range(40):
            lines = self._wrap_text(s, size, max_w)
            widest = 0
            if lines:
                widest = max(self._measure_px(ln, size) for ln in lines)
            total_h = len(lines) * size * 1.25
            if widest <= max_w and total_h <= 135 and len(lines) <= 3:
                return size, lines
            ratio_w = (widest / float(max_w)) if max_w else 1.1
            ratio_h = (total_h / 135.0)
            ratio = max(ratio_w, ratio_h, 1.04)
            new_size = int(size / ratio)
            if new_size >= size: new_size = size - 1
            if new_size < self.cfg.min_font_size:
                size = self.cfg.min_font_size
                break
            size = new_size
        final_size = max(self.cfg.min_font_size, int(size))
        final_lines = self._wrap_text(s, final_size, max_w)
        return final_size, final_lines
