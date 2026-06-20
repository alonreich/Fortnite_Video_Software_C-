import copy
import math
from fractions import Fraction
from typing import Any, Dict, Optional
from .coordinate_math import (
    CONTENT_H,
    UI_PADDING_TOP,
    clamp_content_crop,
    clamp_overlay_position,
    scale_round,
)
HUD_REQUIRED_SECTIONS = ("crops_1080p", "scales", "overlays", "z_orders")
HUD_KEYS = ("loot", "stats", "normal_hp", "boss_hp", "team", "spectating")
HUD_COORDINATE_SPACE = "content_1080x1620"
HUD_SCHEMA_VERSION = 2
HUD_Z_DEFAULTS = {
    "loot": 10,
    "normal_hp": 20,
    "boss_hp": 20,
    "stats": 30,
    "team": 40,
    "spectating": 100,
}
DEFAULT_HUD_CONFIG = {
    "schema_version": HUD_SCHEMA_VERSION,
    "coordinate_space": HUD_COORDINATE_SPACE,
    "crops_1080p": {
        "loot": [0, 0, 0, 0],
        "stats": [0, 0, 0, 0],
        "normal_hp": [0, 0, 0, 0],
        "boss_hp": [0, 0, 0, 0],
        "team": [0, 0, 0, 0],
        "spectating": [0, 0, 0, 0],
    },
    "scales": {
        "loot": 1.0,
        "stats": 1.0,
        "team": 1.0,
        "normal_hp": 1.0,
        "boss_hp": 1.0,
        "spectating": 1.0,
    },
    "overlays": {
        "loot": {"x": 680, "y": 1370},
        "stats": {"x": 730, "y": 150},
        "team": {"x": 30, "y": 250},
        "normal_hp": {"x": 30, "y": 1620},
        "boss_hp": {"x": 30, "y": 1620},
        "spectating": {"x": 30, "y": 1300},
    },
    "z_orders": HUD_Z_DEFAULTS.copy(),
}

def _to_int(value: Any, default: int = 0) -> int:
    try:
        return scale_round(Fraction(str(value)))
    except Exception:
        return default

def _to_scale(value: Any, default: float = 1.0) -> float:
    try:
        raw = float(value)
        if not math.isfinite(raw):
            return default
        return round(max(0.0001, min(8.0, raw)), 4)
    except Exception:
        return default

def _is_current_space(config: Dict[str, Any]) -> bool:
    return config.get("coordinate_space") == HUD_COORDINATE_SPACE and int(config.get("schema_version", 0) or 0) >= HUD_SCHEMA_VERSION

def crop_drift_type(key: str) -> Optional[str]:
    if key in {"stats", "normal_hp", "boss_hp", "team", "spectating"}:
        return "left"
    if key == "loot":
        return "right"
    return None

def sanitize_hud_config(config: Optional[Dict[str, Any]], migrate_legacy: bool = True) -> Dict[str, Any]:
    if not isinstance(config, dict):
        config = {}
    clean = copy.deepcopy(config)
    current_space = _is_current_space(clean)
    defaults = copy.deepcopy(DEFAULT_HUD_CONFIG)
    for section in HUD_REQUIRED_SECTIONS:
        if not isinstance(clean.get(section), dict):
            clean[section] = {}
    keys = set(HUD_KEYS)
    for section in HUD_REQUIRED_SECTIONS:
        keys.update(clean.get(section, {}).keys())
    clean["schema_version"] = HUD_SCHEMA_VERSION
    clean["coordinate_space"] = HUD_COORDINATE_SPACE
    for key in keys:
        rect = clean["crops_1080p"].get(key, defaults["crops_1080p"].get(key, [0, 0, 0, 0]))
        if not isinstance(rect, (list, tuple)) or len(rect) < 4:
            rect = defaults["crops_1080p"].get(key, [0, 0, 0, 0])
        w = _to_int(rect[0], 0)
        h = _to_int(rect[1], 0)
        x = _to_int(rect[2], 0)
        y = _to_int(rect[3], 0)
        if migrate_legacy and not current_space and h > 0:
            y -= UI_PADDING_TOP
        clean["crops_1080p"][key] = list(clamp_content_crop((x, y, w, h)))
        scale = _to_scale(clean["scales"].get(key, defaults["scales"].get(key, 1.0)), defaults["scales"].get(key, 1.0))
        clean["scales"][key] = scale
    for key in keys:
        overlay = clean["overlays"].get(key, defaults["overlays"].get(key, {"x": 0, "y": UI_PADDING_TOP}))
        if not isinstance(overlay, dict):
            overlay = {"x": 0, "y": UI_PADDING_TOP}
        x = _to_int(overlay.get("x", 0), 0)
        y = _to_int(overlay.get("y", UI_PADDING_TOP), UI_PADDING_TOP)
        crop = clean["crops_1080p"].get(key, [0, 0, 0, 0])
        scale = clean["scales"].get(key, 1.0)
        width = max(1, scale_round(Fraction(str(crop[0])) * Fraction(str(scale))))
        height = max(1, scale_round(Fraction(str(crop[1])) * Fraction(str(scale))))
        clean["overlays"][key] = dict(zip(("x", "y"), clamp_overlay_position(x, y, width, height)))
        clean["z_orders"][key] = _to_int(clean["z_orders"].get(key, HUD_Z_DEFAULTS.get(key, 10)), HUD_Z_DEFAULTS.get(key, 10))
    return clean

def validate_hud_config(config: Dict[str, Any]) -> list[str]:
    issues: list[str] = []
    if not isinstance(config, dict):
        return ["Configuration must be a JSON object"]
    if not _is_current_space(config):
        issues.append("HUD coordinate schema requires migration")
    for section in HUD_REQUIRED_SECTIONS:
        if not isinstance(config.get(section), dict):
            issues.append(f"Invalid section: {section}")
    sanitized = sanitize_hud_config(config)
    if not sanitized.get("crops_1080p"):
        issues.append("Missing crop data")
    for key, rect in sanitized.get("crops_1080p", {}).items():
        if not isinstance(rect, list) or len(rect) < 4:
            issues.append(f"Invalid crop data for '{key}'")
            continue
        if rect[0] < 0 or rect[1] < 0 or rect[1] > CONTENT_H:
            issues.append(f"Invalid crop dimensions for '{key}'")
    return issues
