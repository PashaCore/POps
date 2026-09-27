"""Bildirimler: panel zili (notifications tablosu) + isteğe bağlı e-posta ve webhook.

Yalnızca sunucunun kendi karar verdiği olaylar bildirim üretir (güncelleme sorunu, enroll ile
kimlik ele geçirme girişimi, kural ihlali, karantina...). Ajanın /api/logs ile yazdığı risk
seviyesi bildirim üretmez: kayıtsız bir ajan sahte "kritik" olay yağdıramasın.

Dışarıya gönderim olay akışını hiçbir zaman bloklamaz ve hata fırlatmaz: kayıt önce veritabanına
yazılır, e-posta/webhook arka planda (thread'de, zaman aşımlı) gönderilir, sonuç kayda işlenir.
Aynı olay 10 dakika içinde tekrar gelirse yok sayılır; dışarıya gönderim 10 dakikada en fazla 30.
"""

import asyncio
import json
import smtplib
import ssl
import time
import urllib.request
from email.message import EmailMessage
from typing import Optional

from pops import config
from pops.db import execute_query

SEVERITIES = ("info", "medium", "high", "critical")
_SEV_RANK = {s: i for i, s in enumerate(SEVERITIES)}
_SEV_TR = {"info": "Bilgi", "medium": "Orta", "high": "Yüksek", "critical": "Kritik"}

_DEDUPE_SECONDS = 600
_SEND_WINDOW = 600
_SEND_LIMIT = 30
_TIMEOUT = 10.0

_recent = {}  # (event, pc_name, title) -> son görülme zamanı
_sent_times = []  # dışarıya gönderim zamanları (hız sınırı)
_tasks = set()  # arka plan gönderimleri (GC toplamasın)

DEFAULTS = {
    "notify_enabled": "0",
    "notify_min_severity": "high",
    "notify_email_to": "",
    "notify_webhook_url": "",
}


async def get_settings() -> dict:
    rows = await execute_query(
        "SELECT key, value FROM global_settings WHERE key = ANY($1::text[])", (list(DEFAULTS),), fetch=True
    )
    out = dict(DEFAULTS)
    out.update({r["key"]: r["value"] or "" for r in (rows or [])})
    return out


def smtp_configured() -> bool:
    return bool(config.SMTP_HOST and config.SMTP_FROM)


def _send_email(to_list, subject: str, body: str) -> None:
    msg = EmailMessage()
    msg["Subject"] = subject
    msg["From"] = config.SMTP_FROM
    msg["To"] = ", ".join(to_list)
    msg.set_content(body)
    if config.SMTP_SECURITY == "ssl":
        server = smtplib.SMTP_SSL(
            config.SMTP_HOST, config.SMTP_PORT, timeout=_TIMEOUT, context=ssl.create_default_context()
        )
    else:
        server = smtplib.SMTP(config.SMTP_HOST, config.SMTP_PORT, timeout=_TIMEOUT)
    try:
        if config.SMTP_SECURITY == "starttls":
            server.starttls(context=ssl.create_default_context())
        if config.SMTP_USER:
            server.login(config.SMTP_USER, config.SMTP_PASS)
        server.send_message(msg)
    finally:
        try:
            server.quit()
        except Exception:
            pass


def _send_webhook(url: str, payload: dict) -> None:
    # Slack ("text"), Discord ("content") ve genel JSON alıcıları aynı gövdeyi anlar
    data = json.dumps(payload, ensure_ascii=False).encode("utf-8")
    req = urllib.request.Request(
        url, data=data, method="POST", headers={"Content-Type": "application/json", "User-Agent": "POps-server"}
    )
    with urllib.request.urlopen(req, timeout=_TIMEOUT) as resp:
        resp.read(1024)


def _deliver(settings: dict, event: str, severity: str, title: str, detail: str, pc_name: Optional[str]) -> tuple:
    """Bloklayan gönderim (thread'de). (kanallar, hata) döner."""
    channels, errors = [], []
    line = "[POps · %s] %s%s" % (_SEV_TR.get(severity, severity), title, (" — %s" % pc_name) if pc_name else "")
    to_list = [a.strip() for a in settings.get("notify_email_to", "").split(",") if a.strip()]
    if to_list and smtp_configured():
        try:
            body = "%s\n\n%s\n\nOlay: %s\nCihaz: %s\nZaman: %s\n" % (
                title,
                detail or "",
                event,
                pc_name or "-",
                time.strftime("%Y-%m-%d %H:%M:%S"),
            )
            _send_email(to_list, line, body)
            channels.append("email")
        except Exception as exc:
            errors.append("email: %s" % exc)
    url = settings.get("notify_webhook_url", "").strip()
    if url:
        try:
            text = line + (("\n" + detail) if detail else "")
            _send_webhook(
                url,
                {
                    "text": text,
                    "content": text[:1900],
                    "event": event,
                    "severity": severity,
                    "title": title,
                    "detail": detail,
                    "pc_name": pc_name,
                },
            )
            channels.append("webhook")
        except Exception as exc:
            errors.append("webhook: %s" % exc)
    return ",".join(channels), "; ".join(errors)


def _rate_ok() -> bool:
    now = time.time()
    while _sent_times and now - _sent_times[0] > _SEND_WINDOW:
        _sent_times.pop(0)
    if len(_sent_times) >= _SEND_LIMIT:
        return False
    _sent_times.append(now)
    return True


async def _deliver_and_record(nid: int, settings: dict, event, severity, title, detail, pc_name) -> None:
    try:
        channels, error = await asyncio.to_thread(_deliver, settings, event, severity, title, detail, pc_name)
        await execute_query(
            "UPDATE notifications SET channels=$1, delivery_error=$2 WHERE id=$3",
            (channels or None, error or None, nid),
        )
    except Exception as exc:
        print("⚠️ bildirim gönderilemedi: %s" % exc)


async def notify(
    event: str, severity: str, title: str, detail: str = "", pc_name: Optional[str] = None, force: bool = False
) -> Optional[int]:
    """Bildirimi kaydeder ve ayarlara göre e-posta/webhook ile gönderir. Hiçbir zaman hata fırlatmaz.
    force=True (test) tekrar süzgecini ve hız sınırını atlar."""
    try:
        severity = severity if severity in _SEV_RANK else "info"
        title = (title or "")[:300]
        detail = (detail or "")[:2000]
        now = time.time()
        key = (event, pc_name, title)
        if not force:
            for k, t in list(_recent.items()):
                if now - t > _DEDUPE_SECONDS:
                    del _recent[k]
            if key in _recent:
                return None
        _recent[key] = now

        rows = await execute_query(
            "INSERT INTO notifications (event, severity, pc_name, title, detail) VALUES ($1,$2,$3,$4,$5) "
            "RETURNING id",
            (event, severity, pc_name, title, detail),
            fetch=True,
        )
        nid = rows[0]["id"]

        settings = await get_settings()
        wants = settings.get("notify_enabled") == "1" and (
            force or _SEV_RANK[severity] >= _SEV_RANK.get(settings.get("notify_min_severity"), 2)
        )
        if wants and (force or _rate_ok()):
            task = asyncio.create_task(_deliver_and_record(nid, settings, event, severity, title, detail, pc_name))
            _tasks.add(task)
            task.add_done_callback(_tasks.discard)
        return nid
    except Exception as exc:
        print("⚠️ bildirim kaydedilemedi: %s" % exc)
        return None


async def send_test(settings: dict) -> dict:
    """Ayar ekranındaki "test gönder": kanalları senkron dener ve sonucu döner."""
    title = "Test bildirimi"
    detail = "POps bildirim ayarları çalışıyor."
    channels, error = await asyncio.to_thread(_deliver, settings, "test", "info", title, detail, None)
    await execute_query(
        "INSERT INTO notifications (event, severity, title, detail, channels, delivery_error, is_read) "
        "VALUES ('test','info',$1,$2,$3,$4,TRUE)",
        (title, detail, channels or None, error or None),
    )
    return {"channels": [c for c in channels.split(",") if c], "error": error or None}
