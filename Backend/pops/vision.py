"""Vision v2: ikili (binary) ekran karesi başlığı, panel soketindeki aktarım biçimi ve görüntüleyici denetim
mesajlarının doğrulaması. Biçimler docs/vision.md'de ("Vision v2") anlatılır.

Ajan → sunucu (/ws/vision, ikili WebSocket mesajı, big-endian, 18 bayt başlık + JPEG):
    tür (1) · monitör (1; 0xFF = bütün ekranlar yan yana) · sıra (4) · x, y (2+2) · w, h (2+2) · tam çıktı
    genişliği, yüksekliği (2+2) · JPEG
Koordinatlar her zaman çıktının gerçek pikselleridir; ölçek < 1 iken JPEG küçüktür ve görüntüleyici onu (x, y, w, h)
dikdörtgenine çizer. Doğrulama ajanın POps.Shared.VisionFrame.TryParse kurallarıyla aynıdır.
Sunucu → panel (/ws/panel, ikili): 0x01 · cihaz kimliğinin bayt uzunluğu (1) · cihaz kimliği (UTF-8) · ajanın
mesajı olduğu gibi. Cihaz kimliği her zaman tünelin doğrulanmış kimliğidir; ajan mesajında kimlik taşımaz.
"""

import math
import struct
from typing import List, NamedTuple, Optional, Tuple

KIND_FULL = 0x01
KIND_REGION = 0x02
KIND_CURSOR = 0x03

HEADER = struct.Struct(">BBIHHHHHH")
HEADER_SIZE = HEADER.size  # 18
MAX_FRAME_BYTES = 2 * 1024 * 1024  # başlık dahil, ajan mesajı başına
MAX_MONITORS = 16
ALL_MONITORS = 0xFF  # select_monitor "all": ekranlar yan yana tek görüntüde
MAX_DIMENSION = 0xFFFF
JPEG_SOI = b"\xff\xd8"

# Panel soketindeki ikili mesajın ilk baytı (ileride başka ikili mesaj türü eklenebilsin diye)
PANEL_VISION_FRAME = 0x01

CLIPBOARD_MAX_BYTES = 64 * 1024  # UTF-8
QUALITY_MIN, QUALITY_MAX = 30, 75
SCALE_MIN, SCALE_MAX = 0.5, 1.0
FPS_MIN, FPS_MAX = 1, 10


class Frame(NamedTuple):
    kind: int
    monitor: int
    seq: int
    x: int
    y: int
    w: int
    h: int
    full_w: int
    full_h: int


def parse_frame(data: bytes) -> Tuple[Optional[Frame], Optional[str]]:
    """İkili kareyi doğrular. (kare, None) ya da (None, neden) döner; neden "oversize" ya da başlık/içerik hatası
    ("short", "kind", "monitor", "geometry", "jpeg", "cursor_payload")."""
    if len(data) > MAX_FRAME_BYTES:
        return None, "oversize"
    if len(data) < HEADER_SIZE:
        return None, "short"
    f = Frame(*HEADER.unpack_from(data))
    if f.kind not in (KIND_FULL, KIND_REGION, KIND_CURSOR):
        return None, "kind"
    if f.monitor >= MAX_MONITORS and f.monitor != ALL_MONITORS:
        return None, "monitor"
    if not f.full_w or not f.full_h:
        return None, "geometry"
    if f.kind == KIND_CURSOR:
        # İmleç konumu: görüntü yok, w = h = 0, nokta çıktının içinde
        if len(data) != HEADER_SIZE:
            return None, "cursor_payload"
        if f.w or f.h or f.x >= f.full_w or f.y >= f.full_h:
            return None, "geometry"
        return f, None
    if f.kind == KIND_FULL:
        # Tam kare bütün çıktıdır
        if f.x or f.y or f.w != f.full_w or f.h != f.full_h:
            return None, "geometry"
    elif not f.w or not f.h or f.x + f.w > f.full_w or f.y + f.h > f.full_h:
        return None, "geometry"
    if len(data) - HEADER_SIZE <= 3 or data[HEADER_SIZE:HEADER_SIZE + 2] != JPEG_SOI:
        return None, "jpeg"
    return f, None


def pack_frame(kind: int, monitor: int, seq: int, x: int, y: int, w: int, h: int, full_w: int, full_h: int,
               jpeg: bytes = b"") -> bytes:
    """Ajanın gönderdiği biçimde kare (testler ve örnekler için)."""
    return HEADER.pack(kind, monitor, seq & 0xFFFFFFFF, x, y, w, h, full_w, full_h) + jpeg


def panel_prefix(hw_id: str) -> Optional[bytes]:
    """Panel soketindeki ikili kare öneki; kimlik 1..255 bayt değilse None (kare aktarılamaz)."""
    raw = hw_id.encode("utf-8")
    if not 1 <= len(raw) <= 255:
        return None
    return bytes((PANEL_VISION_FRAME, len(raw))) + raw


def _int(v) -> bool:
    return isinstance(v, int) and not isinstance(v, bool)


def _utf8_len(text: str) -> Optional[int]:
    try:
        return len(text.encode("utf-8"))
    except UnicodeEncodeError:  # eşi olmayan vekil (lone surrogate)
        return None


def clipboard_text(value) -> Optional[str]:
    """Pano metni: boş olmayan, UTF-8 olarak en çok 64 KB metin; değilse None."""
    if not isinstance(value, str) or not value:
        return None
    n = _utf8_len(value)
    if n is None or n > CLIPBOARD_MAX_BYTES:
        return None
    return value


def monitors_list(payload: dict) -> Optional[List[dict]]:
    """Ajanın `monitors` mesajındaki liste; yalnızca bilinen alanlar, doğrulanmış. Geçersizse None."""
    items = payload.get("list")
    if not isinstance(items, list) or not 1 <= len(items) <= MAX_MONITORS:
        return None
    out, seen = [], set()
    for m in items:
        if not isinstance(m, dict):
            return None
        idx, w, h, primary = m.get("index"), m.get("width"), m.get("height"), m.get("primary", False)
        if not (_int(idx) and 0 <= idx < MAX_MONITORS and idx not in seen):
            return None
        if not (_int(w) and _int(h) and 1 <= w <= MAX_DIMENSION and 1 <= h <= MAX_DIMENSION):
            return None
        if not isinstance(primary, bool):
            return None
        seen.add(idx)
        out.append({"index": idx, "width": w, "height": h, "primary": primary})
    return out


def viewer_command(msg: dict) -> Optional[dict]:
    """Panelin vision_control mesajından ajana gidecek komut (yalnızca sözleşmedeki alanlar). Geçersizse None."""
    action = msg.get("action")
    if action == "select_monitor":
        idx = msg.get("index")
        if idx == "all" or (_int(idx) and 0 <= idx < MAX_MONITORS):
            return {"action": "select_monitor", "index": idx}
        return None
    if action == "set_quality":
        q, s, fps = msg.get("quality"), msg.get("scale"), msg.get("fps")
        if not (_int(q) and QUALITY_MIN <= q <= QUALITY_MAX):
            return None
        if not (isinstance(s, (int, float)) and not isinstance(s, bool) and math.isfinite(s)
                and SCALE_MIN <= s <= SCALE_MAX):
            return None
        if not (_int(fps) and FPS_MIN <= fps <= FPS_MAX):
            return None
        return {"action": "set_quality", "quality": q, "scale": float(s), "fps": fps}
    if action == "clipboard":
        text = clipboard_text(msg.get("text"))
        return {"action": "clipboard", "text": text} if text is not None else None
    return None
