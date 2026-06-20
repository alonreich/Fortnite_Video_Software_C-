import math
import re
from fractions import Fraction
from typing import Tuple
PORTRAIT_W = 1080
PORTRAIT_H = 1920
INTERNAL_W = 1280
INTERNAL_H = 1920
UI_PADDING_TOP = 150
UI_PADDING_BOTTOM = 150
UI_CONTENT_H = 1620
TARGET_W = INTERNAL_W
TARGET_H = INTERNAL_H
CONTENT_W = PORTRAIT_W
CONTENT_H = UI_CONTENT_H
PADDING_TOP = UI_PADDING_TOP
BACKEND_SCALE = Fraction(INTERNAL_W, PORTRAIT_W)
UI_TO_INTERNAL_SCALE = BACKEND_SCALE

def _fraction(value) -> Fraction:
    if isinstance(value, Fraction):
        return value
    if isinstance(value, int):
        return Fraction(value, 1)
    return Fraction(str(value))

def _floor(value: Fraction) -> int:
    return value.numerator // value.denominator

def _ceil(value: Fraction) -> int:
    return -((-value.numerator) // value.denominator)

def _even_down(value: int) -> int:
    return value if value % 2 == 0 else value - 1

def _even_up(value: int) -> int:
    return value if value % 2 == 0 else value + 1

def scale_round(val) -> int:
    value = _fraction(val)
    if value >= 0:
        return _floor(value + Fraction(1, 2))
    return -_floor((-value) + Fraction(1, 2))

def outward_round_rect(x, y, w, h) -> Tuple[int, int, int, int]:
    fx = _fraction(x)
    fy = _fraction(y)
    fw = _fraction(w)
    fh = _fraction(h)
    ix = _floor(fx)
    iy = _floor(fy)
    iw = _ceil(fx + fw) - ix
    ih = _ceil(fy + fh) - iy
    return ix, iy, max(1, iw), max(1, ih)

def get_resolution_ints(res_str: str) -> Tuple[int, int]:
    if not res_str:
        return 1920, 1080
    try:
        match = re.search(r'(\d+)\s*[x:X\s]\s*(\d+)', str(res_str))
        if match:
            return int(match.group(1)), int(match.group(2))
    except (ValueError, IndexError):
        return 1920, 1080
    return 1920, 1080

def _scale_plan(original_resolution: str) -> Tuple[int, int, int, int, Fraction]:
    in_w, in_h = get_resolution_ints(original_resolution)
    scale = max(Fraction(INTERNAL_W, in_w), Fraction(INTERNAL_H, in_h))
    scaled_w = _even_up(_ceil(Fraction(in_w, 1) * scale))
    scaled_h = _even_up(_ceil(Fraction(in_h, 1) * scale))
    crop_x = _even_down(_floor(Fraction(scaled_w - INTERNAL_W, 2)))
    crop_y = _even_down(_floor(Fraction(scaled_h - INTERNAL_H, 2)))
    return scaled_w, scaled_h, crop_x, crop_y, scale

def transform_to_content_area(rect: Tuple[float, float, float, float], original_resolution: str) -> Tuple[Fraction, Fraction, Fraction, Fraction]:
    x, y, w, h = (_fraction(v) for v in rect)
    _, _, crop_x, crop_y, scale = _scale_plan(original_resolution)
    internal_x = (x * scale) - crop_x
    internal_y = (y * scale) - crop_y
    internal_w = w * scale
    internal_h = h * scale
    return (
        internal_x / UI_TO_INTERNAL_SCALE,
        internal_y / UI_TO_INTERNAL_SCALE,
        internal_w / UI_TO_INTERNAL_SCALE,
        internal_h / UI_TO_INTERNAL_SCALE,
    )

def inverse_transform_from_content_area(rect: Tuple[float, float, float, float], original_resolution: str, drift_type: str = None) -> Tuple[Fraction, Fraction, Fraction, Fraction]:
    in_w, in_h = get_resolution_ints(original_resolution)
    ui_x, ui_y, ui_w, ui_h = (_fraction(v) for v in rect)
    internal_x = ui_x * UI_TO_INTERNAL_SCALE
    internal_y = ui_y * UI_TO_INTERNAL_SCALE
    internal_w = ui_w * UI_TO_INTERNAL_SCALE
    internal_h = ui_h * UI_TO_INTERNAL_SCALE
    _, _, crop_x, crop_y, scale = _scale_plan(original_resolution)
    orig_x = (internal_x + crop_x) / scale
    orig_y = (internal_y + crop_y) / scale
    orig_w = internal_w / scale
    orig_h = internal_h / scale
    if drift_type == "left":
        orig_x -= 1
        orig_w += 1
    elif drift_type == "right":
        orig_w += 1
    final_x = max(Fraction(0), min(orig_x, Fraction(in_w - 1)))
    final_y = max(Fraction(0), min(orig_y, Fraction(in_h - 1)))
    final_w = max(Fraction(1), min(orig_w, Fraction(in_w) - final_x))
    final_h = max(Fraction(1), min(orig_h, Fraction(in_h) - final_y))
    return final_x, final_y, final_w, final_h

def inverse_transform_from_content_area_int(rect: Tuple[int, int, int, int], original_resolution: str, drift_type: str = None) -> Tuple[int, int, int, int]:
    in_w, in_h = get_resolution_ints(original_resolution)
    fx, fy, fw, fh = inverse_transform_from_content_area(
        (rect[0], rect[1], rect[2], rect[3]),
        original_resolution,
        drift_type,
    )
    ix = _floor(fx)
    iy = _floor(fy)
    ex = _ceil(fx + fw)
    ey = _ceil(fy + fh)
    ix = max(0, min(_even_down(ix), in_w - 2))
    iy = max(0, min(_even_down(iy), in_h - 2))
    ex = max(ix + 2, min(_even_up(ex), in_w))
    ey = max(iy + 2, min(_even_up(ey), in_h))
    return ix, iy, max(2, ex - ix), max(2, ey - iy)

def transform_to_content_area_int(rect: Tuple[int, int, int, int], original_resolution: str) -> Tuple[int, int, int, int]:
    fx, fy, fw, fh = transform_to_content_area(rect, original_resolution)
    return outward_round_rect(fx, fy, fw, fh)

def clamp_overlay_position(x, y, width, height, padding_top_ui: int = UI_PADDING_TOP, padding_bottom_ui: int = UI_PADDING_BOTTOM) -> Tuple[int, int]:
    fx = _fraction(x)
    fy = _fraction(y)
    fw = _fraction(width)
    fh = _fraction(height)
    min_y = Fraction(padding_top_ui)
    max_y = max(min_y, Fraction(PORTRAIT_H - padding_bottom_ui) - fh)
    max_x = max(Fraction(0), Fraction(PORTRAIT_W) - fw)
    return scale_round(max(Fraction(0), min(fx, max_x))), scale_round(max(min_y, min(fy, max_y)))

def clamp_content_crop(rect: Tuple[int, int, int, int]) -> Tuple[int, int, int, int]:
    x, y, w, h = (int(rect[0]), int(rect[1]), int(rect[2]), int(rect[3]))
    w = max(0, min(CONTENT_W * 3, w))
    h = max(0, min(CONTENT_H, h))
    y = max(0, min(CONTENT_H - h if h else CONTENT_H, y))
    x = max(-CONTENT_W * 2, min(CONTENT_W * 3, x))
    return w, h, x, y

def scale_rect(rect: Tuple[float, float, float, float], scale_factor: float) -> Tuple[Fraction, Fraction, Fraction, Fraction]:
    x, y, w, h = (_fraction(v) for v in rect)
    factor = _fraction(scale_factor)
    return x, y, w * factor, h * factor

def scale_rect_int(rect: Tuple[int, int, int, int], scale_factor: float) -> Tuple[int, int, int, int]:
    x, y, w, h = scale_rect(rect, scale_factor)
    return scale_round(x), scale_round(y), max(1, _ceil(w)), max(1, _ceil(h))
