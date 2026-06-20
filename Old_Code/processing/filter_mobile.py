from fractions import Fraction
from .coordinate_math import (
    BACKEND_SCALE,
    CONTENT_H,
    CONTENT_W,
    PADDING_TOP,
    PORTRAIT_H,
    PORTRAIT_W,
    TARGET_H,
    TARGET_W,
    UI_PADDING_BOTTOM,
    UI_PADDING_TOP,
    inverse_transform_from_content_area_int,
    scale_round,
)

from .hud_config import crop_drift_type

def _fraction(value) -> Fraction:
    return value if isinstance(value, Fraction) else Fraction(str(value))

def _ceil_fraction(value: Fraction) -> int:
    return -((-value.numerator) // value.denominator)

def _even_ceil(value) -> int:
    n = _ceil_fraction(_fraction(value))
    return n if n % 2 == 0 else n + 1

class MobileFilterMixin:
    def build_mobile_filter_chain(self, input_pad, mobile_coords, is_boss_hp, show_teammates, show_spectating=False, txt_input_label=None, use_cuda=False, original_resolution="1920x1080"):
        coords_data = mobile_coords or {}
        parts = []
        scales = coords_data.get("scales", {})
        overlays = coords_data.get("overlays", {})
        z_orders = coords_data.get("z_orders", {})
        hp_key = "boss_hp" if is_boss_hp else "normal_hp"
        active_layers = []

        def get_rect(section, key):
            return tuple(coords_data.get(section, {}).get(key, [0, 0, 0, 0]))

        def register_layer(name, conf_key, crop_key_1080, ov_key):
            rect_1080 = get_rect("crops_1080p", crop_key_1080)
            try:
                sc = _fraction(scales.get(conf_key, 1.0))
            except Exception:
                sc = Fraction(1, 1)
            if rect_1080 and len(rect_1080) >= 4 and int(rect_1080[0]) >= 1 and int(rect_1080[1]) >= 1:
                active_layers.append({
                    "name": name,
                    "conf_key": conf_key,
                    "ui_rect": rect_1080,
                    "scale": sc,
                    "pos": overlays.get(ov_key, {"x": 0, "y": UI_PADDING_TOP}),
                    "z": z_orders.get(ov_key, 50),
                })
        register_layer("hp", hp_key, hp_key, hp_key)
        register_layer("loot", "loot", "loot", "loot")
        register_layer("stats", "stats", "stats", "stats")
        if show_spectating:
            register_layer("spec", "spectating", "spectating", "spectating")
        if show_teammates:
            register_layer("team", "team", "team", "team")
        active_layers.sort(key=lambda x: x["z"])
        if active_layers:
            split_count = 1 + len(active_layers)
            parts.append(f"{input_pad}split={split_count}[v_base_in]" + "".join([f"[v_layer_in_{i}]" for i in range(len(active_layers))]))
            parts.append(f"[v_base_in]scale={TARGET_W}:{TARGET_H}:force_original_aspect_ratio=increase:flags=lanczos,crop={TARGET_W}:{TARGET_H}[main_base]")
            curr_v = "[main_base]"
            for i, layer in enumerate(active_layers):
                ck = layer["conf_key"]
                r = layer["ui_rect"]
                source_rect = inverse_transform_from_content_area_int((r[2], r[3], r[0], r[1]), original_resolution, crop_drift_type(ck))
                sx, sy, sw, sh = source_rect
                rw = max(2, _even_ceil(_fraction(r[0]) * layer["scale"] * BACKEND_SCALE))
                rh = max(2, _even_ceil(_fraction(r[1]) * layer["scale"] * BACKEND_SCALE))
                pos = layer["pos"] if isinstance(layer["pos"], dict) else {"x": 0, "y": UI_PADDING_TOP}
                lx_raw = _fraction(pos.get("x", 0)) * BACKEND_SCALE
                ly_raw = (_fraction(pos.get("y", UI_PADDING_TOP)) - UI_PADDING_TOP) * BACKEND_SCALE
                lx = scale_round(max(Fraction(0), min(lx_raw, Fraction(TARGET_W - rw))))
                max_internal_y = Fraction(TARGET_H - rh) - (Fraction(UI_PADDING_BOTTOM) * BACKEND_SCALE)
                ly = scale_round(max(Fraction(0), min(ly_raw, max_internal_y)))
                parts.append(f"[v_layer_in_{i}]crop=w={sw}:h={sh}:x={sx}:y={sy},scale=w={rw}:h={rh}:flags=lanczos[v_layer_out_{i}]")
                next_v = f"[v_comp_{i}]"
                parts.append(f"{curr_v}[v_layer_out_{i}]overlay=x={lx}:y={ly}:eof_action=pass{next_v}")
                curr_v = next_v
        else:
            parts.append(f"{input_pad}scale={TARGET_W}:{TARGET_H}:force_original_aspect_ratio=increase:flags=lanczos,crop={TARGET_W}:{TARGET_H}[main_base]")
            curr_v = "[main_base]"
        parts.append(f"{curr_v}scale={CONTENT_W}:{CONTENT_H}:flags=lanczos,pad={PORTRAIT_W}:{PORTRAIT_H}:0:{PADDING_TOP}:black,setsar=1[v_padded]")
        curr_v = "[v_padded]"
        if txt_input_label:
            parts.append(f"{curr_v}{txt_input_label}overlay=0:0:shortest=1:eof_action=repeat:format=auto[v_final_raw]")
            curr_v = "[v_final_raw]"
        parts.append(f"{curr_v}format=yuv420p[v_final]")

        from .filter_builder import FilterResult
        return FilterResult((";".join(parts), "[v_final]"))

    def build_mobile_filter(self, *args, **kwargs):
        if len(args) >= 2 and isinstance(args[0], dict) and isinstance(args[1], str):
            return self.build_mobile_filter_chain(
                "[0:v]",
                args[0],
                kwargs.get("is_boss_hp", False),
                kwargs.get("show_teammates", False),
                False,
                original_resolution=args[1],
            )
        return self.build_mobile_filter_chain(*args, **kwargs)
