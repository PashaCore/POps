"""Sunucu sağlık uyarıları: disk dolmak üzere, TLS sertifikasının süresi bitmek üzere. Zamanlayıcı çağırır; sonuç
bildirim (panel zili + ayarlıysa e-posta/webhook) ve /api/system/diagnostics'te görünür.

Disk: uygulama dizini, yüklenen dosyalar ve DISK_CHECK_PATHS (virgülle). Boş alan %10'un ya da 2 GB'ın altına
inince "high", %5'in ya da 1 GB'ın altına inince "critical". Aynı yol için en fazla 6 saatte bir bildirim.

Sertifika: pops-tls'in dosyaları (/etc/pops/tls/server.crt, /etc/pops/ca/pops-ca.pem; TLS_CERT_FILES ile
değiştirilebilir) ve panelin dış adresi (TLS_CHECK_URL; yoksa CORS_ALLOWED_ORIGINS'teki https adresleri). 21 gün
kala "high", 7 gün kala "critical". Günde bir kez. Adrese bağlanırken yalnızca süre okunur (zincir doğrulanmaz).
"""

import asyncio
import datetime
import logging
import os
import shutil
import socket
import ssl
import time
from urllib.parse import urlparse

from cryptography import x509

from pops.config import BASE_DIR, UPLOAD_DIR
from pops.notify import notify

log = logging.getLogger("pops.health")

DISK_WARN_FRACTION, DISK_WARN_BYTES = 0.10, 2 * 1024**3
DISK_CRIT_FRACTION, DISK_CRIT_BYTES = 0.05, 1 * 1024**3
DISK_CHECK_SECONDS = 3600
DISK_NOTIFY_SECONDS = 6 * 3600
CERT_WARN_DAYS, CERT_CRIT_DAYS = 21, 7
CERT_CHECK_SECONDS = 24 * 3600

last = {"disk": [], "tls": []}
_checked_at = {"disk": 0.0, "tls": 0.0}
_disk_notified = {}  # yol -> son bildirim zamanı


def _disk_paths():
    paths = [BASE_DIR, UPLOAD_DIR] + [p.strip() for p in os.environ.get("DISK_CHECK_PATHS", "").split(",")]
    out = []
    for p in paths:
        if p and os.path.exists(p) and p not in out:
            out.append(p)
    return out


def disk_status():
    out = []
    for path in _disk_paths():
        try:
            u = shutil.disk_usage(path)
        except OSError:
            continue
        free_frac = u.free / u.total if u.total else 1.0
        if free_frac < DISK_CRIT_FRACTION or u.free < DISK_CRIT_BYTES:
            level = "critical"
        elif free_frac < DISK_WARN_FRACTION or u.free < DISK_WARN_BYTES:
            level = "high"
        else:
            level = "ok"
        out.append({"path": path, "free_bytes": u.free, "total_bytes": u.total,
                    "free_percent": round(free_frac * 100, 1), "level": level})
    return out


def _cert_files():
    raw = os.environ.get("TLS_CERT_FILES", "/etc/pops/tls/server.crt,/etc/pops/ca/pops-ca.pem")
    return [p.strip() for p in raw.split(",") if p.strip() and os.path.isfile(p.strip())]


def _check_urls():
    raw = os.environ.get("TLS_CHECK_URL", "").strip() or os.environ.get("CORS_ALLOWED_ORIGINS", "")
    out = []
    for u in raw.split(","):
        p = urlparse(u.strip())
        if p.scheme == "https" and p.hostname and (p.hostname, p.port or 443) not in out:
            out.append((p.hostname, p.port or 443))
    return out[:3]


def _not_after_of_file(path):
    with open(path, "rb") as f:
        return x509.load_pem_x509_certificate(f.read()).not_valid_after_utc


def _not_after_of_host(host, port):
    ctx = ssl.create_default_context()
    ctx.minimum_version = ssl.TLSVersion.TLSv1_2
    ctx.check_hostname = False
    ctx.verify_mode = ssl.CERT_NONE  # yalnızca süre okunur; güven zinciri ajan/tarayıcı tarafında doğrulanır
    with socket.create_connection((host, port), timeout=10) as sock:
        with ctx.wrap_socket(sock, server_hostname=host) as tls:
            der = tls.getpeercert(binary_form=True)
    return x509.load_der_x509_certificate(der).not_valid_after_utc


def tls_status():
    now = datetime.datetime.now(datetime.timezone.utc)
    targets = [("file", p, _not_after_of_file, (p,)) for p in _cert_files()]
    targets += [("host", "%s:%d" % hp, _not_after_of_host, hp) for hp in _check_urls()]
    out = []
    for kind, name, fn, args in targets:
        try:
            not_after = fn(*args)
        except Exception as exc:
            out.append({"kind": kind, "name": name, "error": type(exc).__name__, "level": "unknown"})
            continue
        days = (not_after - now).total_seconds() / 86400
        level = "critical" if days < CERT_CRIT_DAYS else "high" if days < CERT_WARN_DAYS else "ok"
        out.append({"kind": kind, "name": name, "not_after": not_after.isoformat(), "days_left": round(days, 1),
                    "level": level})
    return out


async def check(force: bool = False) -> None:
    now = time.time()
    if force or now - _checked_at["disk"] >= DISK_CHECK_SECONDS:
        _checked_at["disk"] = now
        last["disk"] = await asyncio.to_thread(disk_status)
        for d in last["disk"]:
            if d["level"] == "ok" or now - _disk_notified.get(d["path"], 0) < DISK_NOTIFY_SECONDS:
                continue
            _disk_notified[d["path"]] = now
            await notify(
                "disk_low",
                d["level"],
                "Sunucuda disk dolmak üzere: %s (%%%s boş)" % (d["path"], d["free_percent"]),
                "Boş alan %.1f GB / %.1f GB" % (d["free_bytes"] / 1024**3, d["total_bytes"] / 1024**3),
            )
    if force or now - _checked_at["tls"] >= CERT_CHECK_SECONDS:
        _checked_at["tls"] = now
        last["tls"] = await asyncio.to_thread(tls_status)
        for c in last["tls"]:
            if c["level"] in ("high", "critical"):
                await notify(
                    "cert_expiring",
                    c["level"],
                    "TLS sertifikasının süresi bitiyor: %s (%s gün)" % (c["name"], c["days_left"]),
                    "Bitiş: %s. Yenileme: pops-tls renew ya da sertifika sağlayıcınız." % c["not_after"],
                )
