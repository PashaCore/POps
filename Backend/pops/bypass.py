"""Çevrimdışı bypass kodları: cihaz başına anahtar (ajan 0.1.12+) ve eski ortak anahtar (BYPASS_SECRET).

Karantinadaki cihaz ağa çıkamadığı için kod cihazda yerel olarak doğrulanır; sunucu aynı formülle üretir.
  cihaz anahtarı: HMAC-SHA256(anahtar, "<hw_id>|yyyy-MM-dd") ilk 6 hex, büyük harf
  eski formül:    SHA-256("<hw_id><BYPASS_SECRET>yyyy-MM-dd") ilk 6 hex, büyük harf
Ajan cihaz anahtarını aldıysa (C:\\POpsData\\secure\\bypass.device) YALNIZCA yeni formülü kabul eder.
Formüller POps.Shared/DeviceBypassSecret.cs ile aynıdır; ortak test vektörü tests/test_units.py'de.
Kod sunucunun yerel tarihine göre üretilir; sunucu ve ajanlar aynı saat diliminde olmalıdır.
"""

import base64
import datetime
import hashlib
import hmac
import re
import secrets
from typing import Optional

from pops.config import BYPASS_SECRET
from pops.db import execute_query

# Cihaz anahtarını anlayan ilk ajan sürümü; eskilere gönderilmez (onay gelmez, panel boşuna "bekliyor" der)
MIN_AGENT_VERSION = (0, 1, 12)


def new_key() -> str:
    return base64.urlsafe_b64encode(secrets.token_bytes(32)).rstrip(b"=").decode("ascii")


def _decode(key: str) -> bytes:
    return base64.urlsafe_b64decode(key + "=")


def fingerprint(key: str) -> str:
    """Ajanın onayında gönderdiği parmak izi: SHA-256(anahtar baytları) ilk 16 hex, küçük harf."""
    return hashlib.sha256(_decode(key)).hexdigest()[:16]


def device_code(key: str, hw_id: str, day: datetime.date) -> str:
    message = ("%s|%s" % (hw_id, day.strftime("%Y-%m-%d"))).encode("utf-8")
    return hmac.new(_decode(key), message, hashlib.sha256).hexdigest()[:6].upper()


def legacy_code(hw_id: str, day: datetime.date) -> Optional[str]:
    if not BYPASS_SECRET:
        return None
    raw = "%s%s%s" % (hw_id, BYPASS_SECRET, day.strftime("%Y-%m-%d"))
    return hashlib.sha256(raw.encode("utf-8")).hexdigest()[:6].upper()


def supports_device_key(agent_version: Optional[str]) -> bool:
    # Başlıktan gelen değer: kısa tutulur ve basamak sayısı sınırlıdır (uzun girdide düzenli ifade yavaşlamasın)
    m = re.match(r"v?(\d{1,6})\.(\d{1,6})\.(\d{1,6})", (agent_version or "")[:32].strip())
    return bool(m) and tuple(int(x) for x in m.groups()) >= MIN_AGENT_VERSION


async def key_to_send(pc_name: str) -> Optional[str]:
    """Ajana gönderilecek anahtar: yoksa üretilir; onaylanmamışsa aynısı yeniden gönderilir; onaylıysa None."""
    key = new_key()
    await execute_query(
        "INSERT INTO agent_bypass_keys (pc_name, secret, fingerprint) VALUES ($1, $2, $3) "
        "ON CONFLICT (pc_name) DO NOTHING",
        (pc_name, key, fingerprint(key)),
    )
    rows = await execute_query(
        "SELECT secret, confirmed_at FROM agent_bypass_keys WHERE pc_name = $1", (pc_name,), fetch=True
    )
    if not rows or rows[0]["confirmed_at"] is not None:
        return None
    return rows[0]["secret"]


async def confirm(pc_name: str, reported_fingerprint: str) -> bool:
    """Ajanın onayladığı parmak izi saklanan anahtarınkiyle aynıysa anahtarı etkin sayar."""
    rows = await execute_query(
        "UPDATE agent_bypass_keys SET confirmed_at = NOW() WHERE pc_name = $1 AND fingerprint = $2 RETURNING pc_name",
        (pc_name, str(reported_fingerprint or "")[:64]),
        fetch=True,
    )
    return bool(rows)


async def move(old_pc_name: str, new_pc_name: str) -> None:
    """Cihaz kimliği değişince anahtar yeni kimliğe taşınır (hedefte yoksa)."""
    await execute_query(
        "UPDATE agent_bypass_keys SET pc_name = $1 WHERE pc_name = $2 "
        "AND NOT EXISTS (SELECT 1 FROM agent_bypass_keys WHERE pc_name = $1)",
        (new_pc_name, old_pc_name),
    )


async def codes(pc_name: str, day: datetime.date) -> dict:
    """Panelde gösterilecek kod(lar). method: device (onaylı cihaz anahtarı), pending (anahtar gönderildi ama
    onay gelmedi: iki kod da denenebilir), legacy (eski ortak anahtar), none (kod üretilemiyor)."""
    rows = await execute_query(
        "SELECT secret, confirmed_at FROM agent_bypass_keys WHERE pc_name = $1", (pc_name,), fetch=True
    )
    legacy = legacy_code(pc_name, day)
    if rows and rows[0]["confirmed_at"] is not None:
        return {"method": "device", "token": device_code(rows[0]["secret"], pc_name, day)}
    if rows:
        # Ajan anahtarı yazıp onayı gönderemeden bağlantı kopmuş olabilir: önce yeni kod, olmazsa eskisi
        return {"method": "pending", "token": device_code(rows[0]["secret"], pc_name, day), "fallback_token": legacy}
    if legacy:
        return {"method": "legacy", "token": legacy}
    return {"method": "none", "token": None}
