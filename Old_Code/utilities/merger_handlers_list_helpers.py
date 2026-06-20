def _natural_key(text: str):
    import re
    import locale
    return [int(c) if c.isdigit() else locale.strxfrm(c.lower()) for c in re.split(r"(\d+)", text)]

def _human_time(seconds: float) -> str:
    total = max(0, int(round(float(seconds or 0.0))))
    return f"{total//3600:02}:{(total%3600)//60:02}:{total%60:02}"
