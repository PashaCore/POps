"""Bildirimler: panel zili (notifications tablosu) + isteğe bağlı e-posta ve webhook.

Yalnızca sunucunun kendi karar verdiği olaylar bildirim üretir (güncelleme sorunu, enroll ile
kimlik ele geçirme girişimi, kural ihlali, karantina...). Ajanın /api/logs ile yazdığı risk
seviyesi bildirim üretmez: kayıtsız bir ajan sahte "kritik" olay yağdıramasın.

Dışarıya gönderim olay akışını hiçbir zaman bloklamaz ve hata fırlatmaz: kayıt önce veritabanına
yazılır, e-posta/webhook arka planda (thread'de, zaman aşımlı) gönderilir, sonuç kayda işlenir.
Aynı olay 10 dakika içinde tekrar gelirse yok sayılır; dışarıya gönderim 10 dakikada en fazla 30. Birden fazla
backend süreci çalışıyorsa (REDIS_URL) süzgeç ve sınır bütün süreçlerde ortaktır (Redis; erişilemezse süreç kendi
sayacına döner).
"""

import asyncio
import http.client
import ipaddress
import json
import logging
import smtplib
import socket
import ssl
import time
import urllib.parse
from email.message import EmailMessage
from typing import Optional

from pops import config
from pops.cluster import cluster
from pops.db import execute_query

log = logging.getLogger("pops.notify")

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


def _addr_allowed(ip: str) -> bool:
    """Webhook hedef adresi: varsayılan yalnız genel (internet) adresler. İç ağ, loopback, link-local
    (169.254.169.254 bulut metadata dahil), CGNAT, ayrılmış ve çoklu yayın adresleri reddedilir."""
    a = ipaddress.ip_address(ip)
    if a.is_multicast or a.is_unspecified:
        return False
    return True if config.NOTIFY_WEBHOOK_ALLOW_PRIVATE else a.is_global


def resolve_webhook(url: str) -> tuple:
    """(parçalanmış URL, bağlanılacak IP). Adres çözülür ve HER sonuç kontrol edilir; bağlantı doğrulanan
    IP'ye sabitlenir, böylece DNS'in arada başka adres döndürmesi (rebinding) işe yaramaz."""
    u = urllib.parse.urlsplit(url)
    if u.scheme not in ("http", "https") or not u.hostname:
        raise ValueError("adres http:// ya da https:// olmalı")
    port = u.port or (443 if u.scheme == "https" else 80)
    try:
        ips = sorted({i[4][0] for i in socket.getaddrinfo(u.hostname, port, type=socket.SOCK_STREAM)})
    except socket.gaierror:
        raise ValueError("adres çözülemedi: %s" % u.hostname)
    bad = [ip for ip in ips if not _addr_allowed(ip)]
    if bad or not ips:
        raise ValueError(
            "adres iç ağa ya da yerel bir adrese çıkıyor (%s); okul içi bir sistem için .env'de "
            "NOTIFY_WEBHOOK_ALLOW_PRIVATE=1" % ", ".join(bad or ips)
        )
    return u, ips[0]


class _PinnedHTTPConnection(http.client.HTTPConnection):
    def __init__(self, ip, host, port, timeout):
        super().__init__(host, port, timeout=timeout)
        self._ip = ip

    def connect(self):
        self.sock = socket.create_connection((self._ip, self.port), self.timeout)


class _PinnedHTTPSConnection(http.client.HTTPSConnection):
    """Doğrulanan IP'ye bağlanır; TLS sertifikası yine ana bilgisayar adıyla doğrulanır (SNI)."""

    def __init__(self, ip, host, port, timeout):
        super().__init__(host, port, timeout=timeout, context=ssl.create_default_context())
        self._ip = ip

    def connect(self):
        sock = socket.create_connection((self._ip, self.port), self.timeout)
        self.sock = self._context.wrap_socket(sock, server_hostname=self.host)


def _send_webhook(url: str, payload: dict) -> None:
    # Slack ("text"), Discord ("content") ve genel JSON alıcıları aynı gövdeyi anlar. Yönlendirme izlenmez.
    data = json.dumps(payload, ensure_ascii=False).encode("utf-8")
    u, ip = resolve_webhook(url)
    port = u.port or (443 if u.scheme == "https" else 80)
    cls = _PinnedHTTPSConnection if u.scheme == "https" else _PinnedHTTPConnection
    conn = cls(ip, u.hostname, port, _TIMEOUT)
    path = (u.path or "/") + ("?" + u.query if u.query else "")
    try:
        conn.request("POST", path, body=data, headers={"Content-Type": "application/json", "User-Agent": "POps-server"})
        resp = conn.getresponse()
        resp.read(1024)
        if resp.status >= 300:
            raise ValueError("HTTP %d%s" % (resp.status, " (yönlendirme izlenmez)" if resp.status < 400 else ""))
    finally:
        conn.close()


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


async def _shared_rate_ok() -> bool:
    if cluster.enabled():
        shared = await cluster.rate_ok(_SEND_WINDOW, _SEND_LIMIT)
        if shared is not None:
            return shared
    return _rate_ok()


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
    except Exception:
        log.exception("bildirim gönderilemedi", extra={"notification_id": nid})


# Kaydedilemeyen bildirim (veritabanı kısa süre yanıtsız) arka planda yeniden denenir: kritik bir olay yalnızca
# sunucu günlüğünde kalmasın (F15). Bekleyen deneme sayısı sınırlı.
_RETRY_DELAYS = (5, 30, 120)
_MAX_PENDING_RETRIES = 100
_pending_retries = [0]


async def _retry_later(attempt: int, args: tuple) -> None:
    try:
        await asyncio.sleep(_RETRY_DELAYS[attempt])
    finally:
        _pending_retries[0] -= 1
    await notify(*args, _attempt=attempt + 1)


def _schedule_retry(attempt: int, args: tuple) -> None:
    if attempt >= len(_RETRY_DELAYS) or _pending_retries[0] >= _MAX_PENDING_RETRIES:
        log.error("bildirim kaydedilemedi, yeniden denenmeyecek", extra={"event": args[0], "title": args[2]})
        return
    _pending_retries[0] += 1
    task = asyncio.create_task(_retry_later(attempt, args))
    _tasks.add(task)
    task.add_done_callback(_tasks.discard)


async def notify(
    event: str,
    severity: str,
    title: str,
    detail: str = "",
    pc_name: Optional[str] = None,
    force: bool = False,
    _attempt: int = 0,
) -> Optional[int]:
    """Bildirimi kaydeder ve ayarlara göre e-posta/webhook ile gönderir. Hiçbir zaman hata fırlatmaz.
    force=True (test) tekrar süzgecini ve hız sınırını atlar."""
    key = None
    recorded = False
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
        # Kayıttan önce işaretlenir (aynı anda gelen iki olay iki kayıt açmasın); kayıt başarısız olursa işaret
        # geri alınır, yoksa veritabanı geri gelince aynı olay tekrar süzgecine takılıp hiç kaydedilmezdi (F15)
        _recent[key] = now
        if not force and cluster.enabled() and await cluster.claim_once(key, _DEDUPE_SECONDS) is False:
            return None   # başka bir süreç aynı olayı az önce kaydetti

        rows = await execute_query(
            "INSERT INTO notifications (event, severity, pc_name, title, detail) VALUES ($1,$2,$3,$4,$5) "
            "RETURNING id",
            (event, severity, pc_name, title, detail),
            fetch=True,
        )
        nid = rows[0]["id"]
        recorded = True

        settings = await get_settings()
        wants = settings.get("notify_enabled") == "1" and (
            force or _SEV_RANK[severity] >= _SEV_RANK.get(settings.get("notify_min_severity"), 2)
        )
        if wants and (force or await _shared_rate_ok()):
            task = asyncio.create_task(_deliver_and_record(nid, settings, event, severity, title, detail, pc_name))
            _tasks.add(task)
            task.add_done_callback(_tasks.discard)
        return nid
    except Exception:
        if key is not None and not recorded:
            _recent.pop(key, None)
            if cluster.enabled():
                await cluster.forget_once(key)
        log.exception("bildirim kaydedilemedi", extra={"event": event, "attempt": _attempt + 1})
        if not recorded:
            _schedule_retry(_attempt, (event, severity, title, detail, pc_name, force))
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
