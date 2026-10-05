"""GLPI'ye dışa aktarım (bkz. docs/integrations/glpi.md): bilgisayarlar, kurulu yazılımlar ve destek talepleri
POps'tan GLPI'nin REST API'sine (apirest.php) gider. Yön tektir: GLPI'den hiçbir şey okunup POps'a yazılmaz.

Ayarlar global_settings'te (glpi_*); uygulama ve kullanıcı jetonu secretbox ile şifreli saklanır, panele ve loga
hiçbir zaman gitmez. Eşitleme kapalı başlar; superadmin açar (denetim kaydına yazılır).

Eşleşme POps tarafındaki glpi_links tablosuyla tutulur (anahtar POps cihaz kimliği, HW-…): bağlanmış cihaz hep
bağlantısıyla güncellenir, bir daha aranmaz. Bağlantısız cihaz önce BIOS seri numarası, sonra SMBIOS UUID ile aranır:
tek eşleşme bağlanır, eşleşme yoksa bilgisayar oluşturulur, birden çok eşleşmede tahmin yapılmaz (Sistem'de
gösterilir). POps yalnızca kendi alanlarını yazar (ad, seri numarası, UUID, eşlenmişse konum); GLPI'deki bir
bilgisayarı, yazılımı ya da sürümü hiçbir zaman silmez, yalnızca kendi oluşturduğu yazılım bağlantısını
(Item_SoftwareVersion) kaldırır. Yalnızca değişen cihazlar gönderilir (gönderilen alanların özeti saklanır). Talepler
bir kez gönderilir (seçenek A), sonra GLPI'nindir; sonradan yazılan açık yanıtlar takip (ITILFollowup) olarak
eklenir, iç notlar gitmez.

Dışarıya her çağrı gibi süreli ve hiçbir zaman ölümcül değildir: GLPI kapalı ya da yavaşsa ajanlar ve panel
etkilenmez. Hedef adres webhook gibi denetlenir (iç ağ yalnızca GLPI_ALLOW_PRIVATE=1 ile), bağlantı denetlenen
adrese sabitlenir, yönlendirme izlenmez. Kişisel veri en aza indirilir: oturumdaki kullanıcı, ekran ve olay kaydı
gönderilmez; talebi bildiren kişi yalnızca ayar açıksa talep metnine yazılır.
"""

import asyncio
import datetime
import hashlib
import html
import http.client
import ipaddress
import json
import logging
import re
import socket
import ssl
import time
import urllib.parse
from typing import Dict, List, Optional, Tuple

from pops import config, db, secretbox
from pops.db import execute_query

log = logging.getLogger("pops.glpi")

TIMEOUT = 15.0              # tek istek
RUN_LIMIT_SECONDS = 600     # bir eşitleme turu; kalan cihazlar sonraki tura kalır (özetleri yazılmadığı için)
BATCH = 50                  # toplu oluşturmada bir istekteki öğe
MAX_RESPONSE = 8 * 1024 * 1024
INTERVALS = (0, 6, 12, 24)  # saat; 0 = yalnızca elle
SYNC_DEFAULT = {"computers": True, "software": True, "tickets": True, "ticket_reporter": False}
FAILURE_NOTIFY_AFTER = 3

# POps -> GLPI (Ticket.priority 1-6, Ticket.status 1-6)
PRIORITY = {"low": 2, "normal": 3, "high": 4}
STATUS = {"open": 1, "in_progress": 2, "waiting": 4, "resolved": 5, "closed": 6}

_KEYS = ("glpi_enabled", "glpi_url", "glpi_app_token", "glpi_user_token", "glpi_entity", "glpi_interval_hours",
         "glpi_sync", "glpi_tickets_since", "glpi_locations", "glpi_last_run")
_LOCK = 0x504F4750  # "POGP": aynı anda tek eşitleme (birden çok backend süreci olsa da)

# GLPI'nin hata kodları -> panelde gösterilen açıklama (jeton değerleri hiçbir iletiye girmez)
_ERRORS = {
    "ERROR_GLPI_LOGIN_USER_TOKEN":
        "GLPI kullanıcı jetonunu kabul etmedi (Yönetim → Kullanıcılar → kullanıcı → Uzak erişim anahtarları).",
    "ERROR_LOGIN_PARAMETERS_MISSING": "Kullanıcı jetonu eksik.",
    "ERROR_WRONG_APP_TOKEN_PARAMETER": "GLPI uygulama jetonunu kabul etmedi (Kurulum → Genel → API → API istemcisi).",
    "ERROR_APP_TOKEN_PARAMETERS_MISSING":
        "GLPI bir uygulama jetonu istiyor (Kurulum → Genel → API → API istemcisi).",
    "ERROR_NOT_ALLOWED_IP":
        "GLPI bu sunucunun adresinden API isteğine izin vermiyor (API istemcisinin IP aralığı).",
    "ERROR_SESSION_TOKEN_INVALID": "GLPI oturumu geçersiz ya da süresi doldu.",
    "ERROR_SESSION_TOKEN_MISSING": "GLPI oturumu açılamadı.",
    "ERROR_RIGHT_MISSING": "GLPI kullanıcısının bu işlem için yetkisi yok.",
    "ERROR_API_DISABLED": "GLPI'de REST API kapalı (Kurulum → Genel → API).",
}

# Kullanılamayan seri numaraları: bunlarla arama yanlış bilgisayarı bağlar
_JUNK_SERIALS = {"", "-", "0", "none", "default string", "to be filled by o.e.m.", "system serial number",
                 "not specified", "not applicable", "00000000-0000-0000-0000-000000000000",
                 "ffffffff-ffff-ffff-ffff-ffffffffffff", "03000200-0400-0500-0006-000700080009"}

_task: Optional[asyncio.Task] = None
_progress: dict = {}


class GlpiError(Exception):
    """Panelde gösterilecek Türkçe açıklama (gizli değer içermez). Tek bir öğenin hatası: tur sürer."""


class GlpiFatal(GlpiError):
    """Bağlantı, oturum ya da yetki hatası: tur durur."""


# Bu kodlarla gelen hata bir öğeye değil bağlantıya ya da oturuma aittir
_FATAL = {"ERROR_SESSION_TOKEN_INVALID", "ERROR_SESSION_TOKEN_MISSING", "ERROR_NOT_ALLOWED_IP", "ERROR_API_DISABLED",
          "ERROR_WRONG_APP_TOKEN_PARAMETER", "ERROR_APP_TOKEN_PARAMETERS_MISSING", "ERROR_GLPI_LOGIN_USER_TOKEN",
          "ERROR_LOGIN_PARAMETERS_MISSING"}


# ─── Ayarlar ──────────────────────────────────────────────────────────────────
def _json(value, default):
    try:
        out = json.loads(value) if value else default
    except (TypeError, ValueError):
        return default
    return out if isinstance(out, type(default)) else default


async def load() -> dict:
    """Ayarlar (jetonlar çözülmüş hâlde; yalnızca bu modül içinde kullanılır, dışarıya public() verilir)."""
    rows = await execute_query("SELECT key, value FROM global_settings WHERE key = ANY($1::text[])", (list(_KEYS),),
                               fetch=True)
    raw = {r["key"]: r["value"] for r in rows or []}
    sync = dict(SYNC_DEFAULT)
    sync.update({k: bool(v) for k, v in _json(raw.get("glpi_sync"), {}).items() if k in SYNC_DEFAULT})
    try:
        interval = int(raw.get("glpi_interval_hours") or 24)
    except ValueError:
        interval = 24
    try:
        entity = max(0, int(raw.get("glpi_entity") or 0))
    except ValueError:
        entity = 0
    locations = {str(k): int(v) for k, v in _json(raw.get("glpi_locations"), {}).items()
                 if isinstance(v, int) and v > 0}
    return {
        "enabled": raw.get("glpi_enabled") == "1",
        "url": raw.get("glpi_url") or "",
        "app_token": secretbox.unseal(raw.get("glpi_app_token")) or "",
        "user_token": secretbox.unseal(raw.get("glpi_user_token")) or "",
        "entity": entity,
        "interval_hours": interval if interval in INTERVALS else 24,
        "sync": sync,
        "tickets_since": raw.get("glpi_tickets_since") or None,
        "locations": locations,
        "last_run": _json(raw.get("glpi_last_run"), {}),
    }


def public(s: dict) -> dict:
    """Panele giden ayarlar: jetonların yalnızca kayıtlı olup olmadığı."""
    out = {k: v for k, v in s.items() if k not in ("app_token", "user_token")}
    out["app_token_set"] = bool(s.get("app_token"))
    out["user_token_set"] = bool(s.get("user_token"))
    return out


async def _put(key: str, value: str) -> None:
    await execute_query(
        "INSERT INTO global_settings (key, value) VALUES ($1, $2) ON CONFLICT (key) DO UPDATE SET value = $2",
        (key, value),
    )


async def save(data: dict) -> List[str]:
    """Verilen ayarları yazar; değişen ayarların adlarını döner (denetim kaydı için, değerler değil).
    Jetonlar: None değişmez, "" silinir, başka değer şifrelenip yazılır."""
    cur = await load()
    changed = []
    plain = {
        "enabled": ("glpi_enabled", "1" if data.get("enabled") else "0"),
        "url": ("glpi_url", data.get("url")),
        "entity": ("glpi_entity", None if data.get("entity") is None else str(data["entity"])),
        "interval_hours": ("glpi_interval_hours",
                           None if data.get("interval_hours") is None else str(data["interval_hours"])),
        "sync": ("glpi_sync", None if data.get("sync") is None else json.dumps(data["sync"], sort_keys=True)),
        "tickets_since": ("glpi_tickets_since", data.get("tickets_since")),
        "locations": ("glpi_locations",
                      None if data.get("locations") is None else json.dumps(data["locations"], sort_keys=True)),
    }
    for name, (key, value) in plain.items():
        if name not in data or value is None:
            continue
        before = cur[name]
        if isinstance(before, dict):
            before = json.dumps(before, sort_keys=True)
        elif isinstance(before, bool):
            before = "1" if before else "0"
        else:
            before = "" if before is None else str(before)
        if value != before:
            changed.append(name)
        await _put(key, value)
    for name in ("app_token", "user_token"):
        value = data.get(name)
        if value is None:
            continue
        if value != cur[name]:
            changed.append(name)
        await _put("glpi_" + name, secretbox.seal(value) if value else "")
    return changed


def state() -> dict:
    return {"running": bool(_task and not _task.done()), "progress": dict(_progress) if _progress else None}


# ─── Bağlantı ─────────────────────────────────────────────────────────────────
def api_url(url: str) -> str:
    """Kullanıcının yazdığı adres -> apirest.php'nin adresi (https://glpi.okul/ ya da …/apirest.php ikisi de olur)."""
    url = (url or "").strip().rstrip("/")
    if not url:
        return ""
    return url if url.lower().endswith("/apirest.php") else url + "/apirest.php"


def _addr_allowed(ip: str) -> bool:
    a = ipaddress.ip_address(ip)
    if a.is_multicast or a.is_unspecified:
        return False
    return True if config.GLPI_ALLOW_PRIVATE else a.is_global


def resolve(url: str) -> Tuple[urllib.parse.SplitResult, str]:
    """(parçalanmış apirest.php adresi, bağlanılacak IP). Webhook'taki gibi her çözülen adres denetlenir ve bağlantı
    denetlenen adrese sabitlenir. http:// yalnızca GLPI_ALLOW_PRIVATE açıkken ve adresin hepsi iç ağdaysa olur."""
    u = urllib.parse.urlsplit(api_url(url))
    if u.scheme not in ("http", "https") or not u.hostname:
        raise GlpiError("GLPI adresi https:// ile başlamalı.")
    try:
        port = u.port or (443 if u.scheme == "https" else 80)
    except ValueError:
        raise GlpiError("GLPI adresindeki port geçersiz.")
    try:
        ips = sorted({i[4][0] for i in socket.getaddrinfo(u.hostname, port, type=socket.SOCK_STREAM)})
    except (socket.gaierror, UnicodeError):
        raise GlpiError("GLPI adresi çözülemedi: %s" % u.hostname)
    bad = [ip for ip in ips if not _addr_allowed(ip)]
    if bad or not ips:
        raise GlpiError(
            "GLPI adresi iç ağa ya da yerel bir adrese çıkıyor (%s). Okul ağındaki bir GLPI için sunucunun .env "
            "dosyasında GLPI_ALLOW_PRIVATE=1 olmalı." % ", ".join(bad or ips)
        )
    if u.scheme == "http" and (not config.GLPI_ALLOW_PRIVATE or any(ipaddress.ip_address(ip).is_global for ip in ips)):
        raise GlpiError("GLPI'ye https:// ile bağlanılmalı (jetonlar şifresiz gitmesin). http:// yalnızca iç ağdaki "
                        "bir GLPI için ve GLPI_ALLOW_PRIVATE=1 iken kabul edilir.")
    return u, ips[0]


class _PinnedHTTP(http.client.HTTPConnection):
    def __init__(self, ip, host, port, timeout):
        super().__init__(host, port, timeout=timeout)
        self._ip = ip

    def connect(self):
        self.sock = socket.create_connection((self._ip, self.port), self.timeout)


class _PinnedHTTPS(http.client.HTTPSConnection):
    """Denetlenen IP'ye bağlanır; sertifika ana bilgisayar adıyla doğrulanır."""

    def __init__(self, ip, host, port, timeout):
        ctx = ssl.create_default_context(cafile=config.GLPI_CA_FILE or None)
        super().__init__(host, port, timeout=timeout, context=ctx)
        self._ip = ip

    def connect(self):
        sock = socket.create_connection((self._ip, self.port), self.timeout)
        self.sock = self._context.wrap_socket(sock, server_hostname=self.host)


def _glpi_message(status: int, data) -> str:
    """GLPI'nin hata gövdesi (["ERROR_X", "açıklama"]) -> Türkçe ileti."""
    code, detail = None, ""
    if isinstance(data, list) and data and isinstance(data[0], str):
        code = data[0]
        extra = data[1] if len(data) > 1 else ""
        if isinstance(extra, list):   # toplu işlemin öğe sonuçları: [{"12": false, "message": "…"}, …]
            extra = "; ".join(str(i["message"]) for i in extra if isinstance(i, dict) and i.get("message"))
        detail = str(extra or "")[:200]
    if code in _ERRORS:
        return _ERRORS[code] + (" (%s)" % detail if code == "ERROR_RIGHT_MISSING" and detail else "")
    if code:
        return "GLPI hata döndü: %s%s" % (code, (" — " + detail) if detail else "")
    if status == 404:
        return "Bu adreste GLPI API'si yok (apirest.php bulunamadı)."
    if 300 <= status < 400:
        return "GLPI başka bir adrese yönlendiriyor (HTTP %d); yönlendirme izlenmez, adresi düzeltin." % status
    return "GLPI beklenmeyen bir yanıt verdi (HTTP %d)." % status


def error(status: int, data) -> GlpiError:
    """Başarısız yanıtın hatası: oturum, yetki ve bağlantı hataları turu durdurur, gerisi yalnızca o öğeyi."""
    code = data[0] if isinstance(data, list) and data and isinstance(data[0], str) else None
    cls = GlpiFatal if code in _FATAL or status in (401, 403) or status >= 500 else GlpiError
    return cls(_glpi_message(status, data))


class Client:
    """Bloklayan GLPI istemcisi; eşitleme çağrıları a…() ile iş parçacığında çalıştırır."""

    def __init__(self, url: str, app_token: str, user_token: str):
        if not url or not user_token:
            raise GlpiError("GLPI adresi ve kullanıcı jetonu girilmeli.")
        self.u, self.ip = resolve(url)
        self.app_token = app_token
        self.user_token = user_token
        self.session: Optional[str] = None
        self.calls = 0
        self.last_used = 0.0
        port = self.u.port or (443 if self.u.scheme == "https" else 80)
        cls = _PinnedHTTPS if self.u.scheme == "https" else _PinnedHTTP
        try:
            self.conn = cls(self.ip, self.u.hostname, port, TIMEOUT)
        except OSError:
            raise GlpiFatal("GLPI_CA_FILE okunamadı.")

    def call(self, method: str, path: str, body=None, params: Optional[dict] = None, auth: bool = False):
        """(HTTP durumu, JSON gövde). Bağlantı ve zaman aşımı hataları GlpiFatal olur.

        GLPI 10 metni temizlenmiş (&, <, > kodlanmış) saklar ve öyle döndürür; X-GLPI-Sanitized-Content: false ile
        yanıtlar ham gelir, karşılaştırmalar ham metinle yapılır. POST yeniden denenmez: GLPI isteği işlemiş ama yanıt
        yolda kopmuşsa ikinci deneme kopya oluştururdu (bir sonraki tur bağlantı tablosundan devam eder)."""
        headers = {"Content-Type": "application/json", "Accept": "application/json", "User-Agent": "POps-server",
                   "X-GLPI-Sanitized-Content": "false"}
        if self.app_token:
            headers["App-Token"] = self.app_token
        if auth:
            headers["Authorization"] = "user_token " + self.user_token
        elif self.session:
            headers["Session-Token"] = self.session
        query = ("?" + urllib.parse.urlencode(params, doseq=True)) if params else ""
        target = urllib.parse.quote(self.u.path + "/" + path, safe="/") + query
        data = json.dumps(body, ensure_ascii=False).encode("utf-8") if body is not None else None
        self.calls += 1
        if method == "POST" and time.monotonic() - self.last_used > 2:
            self.conn.close()   # boşta kalmış canlı bağlantı sunucuca kapatılmış olabilir: POST yeni bağlantıyla
        for attempt in (1, 2):
            try:
                self.conn.request(method, target, body=data, headers=headers)
                resp = self.conn.getresponse()
                raw = resp.read(MAX_RESPONSE + 1)
                self.last_used = time.monotonic()
                break
            except (http.client.RemoteDisconnected, BrokenPipeError, ConnectionResetError) as exc:
                # Sunucu canlı tutulan bağlantıyı kapatmış: okuma ve güncelleme bir kez yeniden denenir
                self.conn.close()
                if attempt == 2 or method == "POST":
                    raise GlpiFatal("GLPI bağlantıyı kapattı: %s" % type(exc).__name__)
            except ValueError:
                # Başlıkta geçersiz karakter (jeton) ya da yol: ileti jetonu içermez
                self.conn.close()
                raise GlpiFatal("GLPI isteği oluşturulamadı: adres ya da jetonda geçersiz karakter var.")
            except socket.timeout:
                self.conn.close()
                raise GlpiFatal("GLPI %d saniyede yanıt vermedi." % TIMEOUT)
            except ssl.SSLCertVerificationError as exc:
                self.conn.close()
                raise GlpiFatal("GLPI'nin sertifikası doğrulanamadı (%s). Kurumun kendi sertifika otoritesi için "
                                "GLPI_CA_FILE." % (exc.verify_message or "geçersiz"))
            except (OSError, http.client.HTTPException) as exc:
                self.conn.close()
                raise GlpiFatal("GLPI'ye bağlanılamadı: %s" % (getattr(exc, "strerror", None) or type(exc).__name__))
        if len(raw) > MAX_RESPONSE:
            self.conn.close()
            raise GlpiFatal("GLPI'nin yanıtı çok büyük.")
        try:
            payload = json.loads(raw) if raw.strip() else None
        except ValueError:
            payload = None
        return resp.status, payload

    def ok(self, method: str, path: str, body=None, params=None):
        status, data = self.call(method, path, body, params)
        if status >= 300:
            raise error(status, data)
        return data

    def open(self) -> dict:
        status, data = self.call("GET", "initSession", params={"get_full_session": "false"}, auth=True)
        if status != 200 or not isinstance(data, dict) or not data.get("session_token"):
            raise GlpiFatal(_glpi_message(status, data))
        self.session = data["session_token"]
        return data

    def close(self) -> None:
        if self.session:
            try:
                self.call("GET", "killSession")
            except GlpiError:
                pass
            self.session = None
        self.conn.close()

    async def a(self, method: str, path: str, body=None, params=None):
        return await asyncio.to_thread(self.ok, method, path, body, params)


async def test_connection(url: str, app_token: str, user_token: str) -> dict:
    """Oturum aç, kim olduğunu ve etkin varlığı oku, oturumu kapat. {ok, user, entity, error}"""
    client = None
    try:
        client = await asyncio.to_thread(Client, url, app_token, user_token)
        await asyncio.to_thread(client.open)
        full = await client.a("GET", "getFullSession")
        sess = (full or {}).get("session", {}) if isinstance(full, dict) else {}
        return {"ok": True, "user": sess.get("glpiname"), "entity": sess.get("glpiactive_entity_name"),
                "plain_http": client.u.scheme == "http"}
    except GlpiError as exc:
        return {"ok": False, "error": str(exc)}
    finally:
        if client:
            await asyncio.to_thread(client.close)


# ─── Eşleme ───────────────────────────────────────────────────────────────────
def _fp(value) -> str:
    return hashlib.sha256(json.dumps(value, sort_keys=True, ensure_ascii=False, default=str).encode()).hexdigest()


def usable_serial(value: Optional[str]) -> bool:
    return bool(value) and str(value).strip().lower() not in _JUNK_SERIALS


def computer_fields(row: dict, locations: Dict[str, int]) -> dict:
    """POps'un sahibi olduğu Computer alanları; değeri olmayan alan gönderilmez (GLPI'dekini silmesin diye)."""
    out = {"name": (row.get("hostname") or row["pc_name"])[:255]}
    if usable_serial(row.get("dna_bios")):
        out["serial"] = row["dna_bios"].strip()[:255]
    if usable_serial(row.get("dna_uuid")):
        out["uuid"] = row["dna_uuid"].strip()[:255]
    loc = locations.get(row.get("lab_name") or "")
    if loc:
        out["locations_id"] = loc   # eşlenmemiş sınıfın konumuna dokunulmaz
    return out


def install_date(value: Optional[str]) -> Optional[str]:
    """Windows'un YYYYMMDD biçimi -> YYYY-MM-DD; tanınmayan değer gönderilmez."""
    m = re.fullmatch(r"(\d{4})-?(\d{2})-?(\d{2})", (value or "").strip())
    if not m:
        return None
    try:
        return datetime.date(int(m[1]), int(m[2]), int(m[3])).isoformat()
    except ValueError:
        return None


def _html(text: str) -> str:
    return "<p>" + html.escape(text or "").replace("\n", "<br>") + "</p>"


def ticket_input(t: dict, entity: int, reporter: bool) -> dict:
    intro = "POps'ta tepsiden açıldı." if t.get("source") == "agent" else "POps'ta panelden açıldı."
    if reporter and t.get("reporter"):
        intro += " Bildiren: %s." % t["reporter"]
    created = t["created_at"].astimezone() if t.get("created_at") else None
    out = {
        "name": (t.get("subject") or "")[:255],
        "content": _html(intro) + _html(t.get("body") or ""),
        "priority": PRIORITY.get(t.get("priority"), 3),
        "status": STATUS.get(t.get("status"), 1),
        "type": 1,   # olay (incident)
        "entities_id": entity,
    }
    if created:
        out["date"] = created.strftime("%Y-%m-%d %H:%M:%S")
    return out


# ─── Bağlantı tablosu ─────────────────────────────────────────────────────────
async def _links(kind: str, prefix: Optional[str] = None) -> Dict[str, dict]:
    if prefix is None:
        rows = await execute_query("SELECT * FROM glpi_links WHERE kind = $1", (kind,), fetch=True)
    else:
        rows = await execute_query(
            "SELECT * FROM glpi_links WHERE kind = $1 AND left(pops_key, $3) = $2",
            (kind, prefix, len(prefix)), fetch=True,
        )
    return {r["pops_key"]: r for r in rows or []}


async def _link(kind: str, key: str, glpi_id: Optional[int], fingerprint: Optional[str] = None,
                state: str = "ok", error: Optional[str] = None) -> None:
    await execute_query(
        "INSERT INTO glpi_links (kind, pops_key, glpi_id, fingerprint, state, error, synced_at) "
        "VALUES ($1, $2, $3, $4, $5, $6, now()) ON CONFLICT (kind, pops_key) DO UPDATE SET glpi_id = $3, "
        "fingerprint = $4, state = $5, error = $6, synced_at = now()",
        (kind, key, glpi_id, fingerprint, state, (error or None) and error[:300]),
    )


async def _unlink(kind: str, key: str) -> None:
    await execute_query("DELETE FROM glpi_links WHERE kind = $1 AND pops_key = $2", (kind, key))


async def forget_device(pc_name: str) -> int:
    """Cihazın bağlantılarını unutur (GLPI'ye dokunmaz): sonraki eşitlemede yeniden aranır."""
    rows = await execute_query(
        "DELETE FROM glpi_links WHERE (kind IN ('computer', 'software_set') AND pops_key = $1) "
        "OR (kind = 'software_link' AND left(pops_key, $3) = $2) RETURNING kind",
        (pc_name, pc_name + "|", len(pc_name) + 1), fetch=True,
    )
    return len(rows or [])


async def problems(limit: int = 50) -> List[dict]:
    rows = await execute_query(
        "SELECT l.pops_key AS pc_name, c.hostname, c.display_name, l.state, l.error, l.glpi_id, l.synced_at "
        "FROM glpi_links l LEFT JOIN clients c ON c.pc_name = l.pops_key "
        "WHERE l.kind = 'computer' AND l.state <> 'ok' ORDER BY l.synced_at DESC LIMIT $1",
        (limit,), fetch=True,
    )
    return [dict(r, synced_at=r["synced_at"].isoformat() if r.get("synced_at") else None) for r in rows or []]


async def counts() -> dict:
    rows = await execute_query(
        "SELECT kind, state, count(*) AS n FROM glpi_links WHERE kind IN ('computer', 'ticket') GROUP BY kind, state",
        fetch=True,
    )
    out = {"computers": 0, "tickets": 0, "problems": 0}
    for r in rows or []:
        if r["state"] != "ok":
            out["problems"] += int(r["n"])
        elif r["kind"] == "computer":
            out["computers"] = int(r["n"])
        else:
            out["tickets"] = int(r["n"])
    return out


# ─── Eşitleme ─────────────────────────────────────────────────────────────────
class _Run:
    def __init__(self, client: Client, s: dict, deadline: float):
        self.c = client
        self.s = s
        self.entity = s["entity"]
        self.deadline = deadline
        self.n = {"computers_created": 0, "computers_linked": 0, "computers_updated": 0, "computers_ambiguous": 0,
                  "software_added": 0, "software_removed": 0, "tickets_created": 0, "followups_created": 0,
                  "deferred": 0, "item_errors": 0}
        self.errors: List[str] = []
        self.software: Dict[str, int] = {}
        self.versions: Dict[str, int] = {}
        self.makers: Dict[str, int] = {}
        self.linked: set = set()   # başka bir POps cihazına bağlı Computer kimlikleri: ikinci kez bağlanmaz

    def late(self) -> bool:
        return time.monotonic() > self.deadline

    def item_error(self, what: str, exc: Exception) -> None:
        self.n["item_errors"] += 1
        if len(self.errors) < 5:
            self.errors.append("%s: %s" % (what, exc))

    async def missing(self, itemtype: str, item_id: int) -> bool:
        """Öğe GLPI'de yok mu (silinmiş ya da temizlenmiş). Çöpteki öğe vardır."""
        status, data = await asyncio.to_thread(self.c.call, "GET", "%s/%d" % (itemtype, item_id))
        if status == 404:
            return True
        if status >= 300:
            err = error(status, data)
            if isinstance(err, GlpiFatal):
                raise err
        return False

    async def search(self, itemtype: str, field: str, value: str) -> List[dict]:
        """Alanı tam olarak bu değer olan öğeler. Arama GLPI'de LIKE'tır: değer ^…$ ile sabitlenir ve GLPI'nin
        sakladığı biçimde (&, <, > kodlu) gönderilir; sonuç yine birebir karşılaştırılır."""
        stored = value.replace("&", "&#38;").replace("<", "&#60;").replace(">", "&#62;")
        data = await self.c.a("GET", itemtype, params={"searchText[%s]" % field: "^%s$" % stored, "range": "0-99"})
        want = value.strip().lower()
        return [i for i in (data if isinstance(data, list) else [])
                if isinstance(i, dict) and str(i.get(field) or "").strip().lower() == want]

    async def create(self, itemtype: str, fields: dict) -> int:
        data = await self.c.a("POST", itemtype, {"input": fields})
        item = data[0] if isinstance(data, list) and data else data
        if not isinstance(item, dict) or not item.get("id"):
            raise GlpiError("GLPI %s oluşturmadı: %s" % (itemtype, str(item)[:150]))
        return int(item["id"])

    # -- bilgisayarlar
    async def computers(self) -> None:
        rows = await execute_query(
            "SELECT pc_name, hostname, lab_name, dna_bios, dna_uuid FROM clients ORDER BY pc_name", fetch=True)
        links = await _links("computer")
        self.linked = {link["glpi_id"] for link in links.values() if link["glpi_id"]}
        present = {r["pc_name"] for r in rows or []}
        for gone in set(links) - present:   # POps'tan silinen cihaz: GLPI'de kalır, bağlantı unutulur
            await forget_device(gone)
        for row in rows or []:
            if self.late():
                self.n["deferred"] += 1
                continue
            try:
                cid = await self.computer(row, links.get(row["pc_name"]))
                if cid and self.s["sync"]["software"]:
                    await self.software_of(row["pc_name"], cid)
            except GlpiFatal:
                raise
            except GlpiError as exc:
                self.item_error(row["pc_name"], exc)

    async def computer(self, row: dict, link: Optional[dict]) -> Optional[int]:
        pc = row["pc_name"]
        fields = computer_fields(row, self.s["locations"])
        fp = _fp(fields)
        if link and link["state"] == "broken":
            return None   # GLPI'de silinmiş ya da çöpte: kendiliğinden yeniden oluşturulmaz
        if link and link["state"] == "ok" and link["glpi_id"]:
            if link["fingerprint"] == fp:
                return link["glpi_id"]
            status, data = await asyncio.to_thread(self.c.call, "PUT", "Computer/%d" % link["glpi_id"],
                                                   {"input": fields})
            result = data[0] if isinstance(data, list) and data and isinstance(data[0], dict) else {}
            if status >= 300 or result.get(str(link["glpi_id"])) is False:
                # ERROR_GLPI_UPDATE bulunamayan öğe için de, yetki ya da doğrulama hatası için de gelir: yalnızca
                # GLPI'de gerçekten yoksa bağlantı kırık sayılır
                if await self.missing("Computer", link["glpi_id"]):
                    await _link("computer", pc, link["glpi_id"], fp, "broken",
                                "GLPI'de bulunamadı (silinmiş); kendiliğinden yeniden oluşturulmaz.")
                    return None
                raise error(status if status >= 300 else 400, data)
            await _link("computer", pc, link["glpi_id"], fp)
            self.n["computers_updated"] += 1
            return link["glpi_id"]
        # Bağlantısız: seri numarası, sonra UUID ile ara; tahmin yok
        for key in ("serial", "uuid"):
            if key not in fields:
                continue
            found = [f for f in await self.search("Computer", key, fields[key]) if int(f["id"]) not in self.linked]
            if len(found) > 1:
                await _link("computer", pc, None, None, "ambiguous",
                            "GLPI'de bu %s ile %d bilgisayar var; hangisi olduğu tahmin edilmez."
                            % ("seri numarası" if key == "serial" else "UUID", len(found)))
                self.n["computers_ambiguous"] += 1
                return None
            if found:
                cid = int(found[0]["id"])
                await self.c.a("PUT", "Computer/%d" % cid, {"input": fields})
                await _link("computer", pc, cid, fp)
                self.linked.add(cid)
                self.n["computers_linked"] += 1
                return cid
        cid = await self.create("Computer", dict(fields, entities_id=self.entity))
        await _link("computer", pc, cid, fp)
        self.linked.add(cid)
        self.n["computers_created"] += 1
        return cid

    # -- yazılımlar
    async def maker_id(self, name: str) -> int:
        key = name.strip().lower()
        if key not in self.makers:
            found = await self.search("Manufacturer", "name", name.strip())
            if found:
                self.makers[key] = int(found[0]["id"])
            else:
                self.makers[key] = await self.create("Manufacturer", {"name": name.strip()[:255]})
        return self.makers[key]

    async def software_id(self, name: str, publisher: Optional[str]) -> int:
        key = name.strip().lower()
        if key not in self.software:
            found = await self.search("Software", "name", name.strip())
            same = [f for f in found if int(f.get("entities_id") or 0) == self.entity] or found
            if same:
                self.software[key] = int(same[0]["id"])
            else:
                fields = {"name": name.strip()[:255], "entities_id": self.entity}
                if publisher and publisher.strip():
                    fields["manufacturers_id"] = await self.maker_id(publisher)
                self.software[key] = await self.create("Software", fields)
        return self.software[key]

    async def version_id(self, item: dict) -> int:
        sid = await self.software_id(item["name"], item.get("publisher"))
        version = (item.get("version") or "").strip()[:255]
        key = "%d|%s" % (sid, version.lower())
        if key not in self.versions:
            data = await self.c.a("GET", "Software/%d/SoftwareVersion" % sid, params={"range": "0-999"})
            same = [v for v in (data if isinstance(data, list) else [])
                    if isinstance(v, dict) and str(v.get("name") or "").strip().lower() == version.lower()]
            self.versions[key] = int(same[0]["id"]) if same else await self.create(
                "SoftwareVersion", {"softwares_id": sid, "name": version, "entities_id": self.entity})
        return self.versions[key]

    async def software_of(self, pc: str, cid: int) -> None:
        items = await execute_query(
            "SELECT name, version, publisher, install_date FROM device_software WHERE pc_name = $1 "
            "ORDER BY name, version",
            (pc,), fetch=True,
        )
        fp = _fp([[i["name"], i["version"], i["publisher"], i["install_date"], cid] for i in items or []])
        state_row = (await _links("software_set", pc)).get(pc)
        if state_row and state_row["fingerprint"] == fp:
            return
        desired = {"%s|%s|%s" % (pc, i["name"], i["version"] or ""): i for i in items or []}
        existing = await _links("software_link", pc + "|")
        clean = True
        for key in set(existing) - set(desired):   # kaldırılan program: yalnızca POps'un kurduğu bağlantı silinir
            lid = existing[key]["glpi_id"]
            status, data = await asyncio.to_thread(self.c.call, "DELETE", "Item_SoftwareVersion/%d" % lid, None,
                                                   {"force_purge": "true"})
            # GLPI bulunamayan öğe için 400 ERROR_GLPI_DELETE döner: GLPI'de zaten yoksa bağlantı da unutulur
            if status < 300 or await self.missing("Item_SoftwareVersion", lid):
                await _unlink("software_link", key)
                self.n["software_removed"] += 1
            else:
                err = error(status, data)
                if isinstance(err, GlpiFatal):
                    raise err
                clean = False
                self.item_error(key, err)
        new = [k for k in desired if k not in existing]
        # Bilgisayarda zaten bağlı sürümler (GLPI ajanı ya da elle) yeniden eklenmez ve POps'un sayılmaz: GLPI aynı
        # bilgisayar ve sürüm için ikinci bağlantıyı reddeder, POps da kendi kurmadığını silmemeli
        attached = set()
        if new:
            data = await self.c.a("GET", "Computer/%d/Item_SoftwareVersion" % cid, params={"range": "0-9999"})
            attached = {int(i["softwareversions_id"]) for i in (data if isinstance(data, list) else [])
                        if isinstance(i, dict) and i.get("softwareversions_id")}
        for start in range(0, len(new), BATCH):
            if self.late():
                clean = False
                break
            chunk, inputs = [], []
            for key in new[start:start + BATCH]:
                item = desired[key]
                try:
                    vid = await self.version_id(item)
                except GlpiFatal:
                    raise
                except GlpiError as exc:
                    clean = False
                    self.item_error("%s %s" % (item["name"], item["version"] or ""), exc)
                    continue
                if vid in attached:
                    continue
                attached.add(vid)   # aynı listede aynı sürüme düşen iki satır (ör. boşluk farkı) bir kez eklenir
                fields = {"items_id": cid, "itemtype": "Computer", "softwareversions_id": vid,
                          "entities_id": self.entity}
                when = install_date(item.get("install_date"))
                if when:
                    fields["date_install"] = when
                chunk.append(key)
                inputs.append(fields)
            if not inputs:
                continue
            status, data = await asyncio.to_thread(self.c.call, "POST", "Item_SoftwareVersion", {"input": inputs})
            # Hepsi eklenince 201 ve [{id, message}, …]; bir kısmı eklenince 207 ve ["ERROR_GLPI_PARTIAL_ADD",
            # [{id, message}, …]]; hiçbiri eklenmezse 400 ve ["ERROR_GLPI_ADD", [...]]
            results = data[1] if (isinstance(data, list) and len(data) == 2 and isinstance(data[0], str)
                                  and isinstance(data[1], list)) else data
            if status >= 300 and not isinstance(results, list):
                err = error(status, data)
                if isinstance(err, GlpiFatal):
                    raise err
                clean = False
                self.item_error(pc, err)
                continue
            results = results if isinstance(results, list) else [results]
            for key, res in zip(chunk, results + [None] * (len(chunk) - len(results))):
                if isinstance(res, dict) and res.get("id"):
                    await _link("software_link", key, int(res["id"]))
                    self.n["software_added"] += 1
                else:
                    clean = False
                    msg = (res or {}).get("message") if isinstance(res, dict) else res
                    self.item_error(key, GlpiError(str(msg or "eklenmedi")[:150]))
        if clean:
            await _link("software_set", pc, None, fp)

    # -- destek talepleri (seçenek A: bir kez gönderilir; sonraki açık yanıtlar takip olarak eklenir)
    async def tickets(self) -> None:
        since = self.s.get("tickets_since") or datetime.date.today().isoformat()
        rows = await execute_query(
            "SELECT t.* FROM tickets t WHERE t.created_at >= $1::date AND NOT EXISTS (SELECT 1 FROM glpi_links l "
            "WHERE l.kind = 'ticket' AND l.pops_key = t.id::text) ORDER BY t.id LIMIT 500",
            (datetime.date.fromisoformat(since),), fetch=True,
        )
        computers = await _links("computer")
        for t in rows or []:
            if self.late():
                self.n["deferred"] += 1
                continue
            try:
                tid = await self.create("Ticket", ticket_input(t, self.entity, self.s["sync"]["ticket_reporter"]))
                await _link("ticket", str(t["id"]), tid)
                self.n["tickets_created"] += 1
                comp = computers.get(t.get("pc_name") or "")
                if comp and comp["state"] == "ok" and comp["glpi_id"]:
                    await self.create("Item_Ticket", {"itemtype": "Computer", "items_id": comp["glpi_id"],
                                                      "tickets_id": tid})
            except GlpiFatal:
                raise
            except GlpiError as exc:
                self.item_error("talep #%s" % t["id"], exc)
        msgs = await execute_query(
            "SELECT m.id, m.body, m.author, m.created_at, l.glpi_id FROM ticket_messages m JOIN glpi_links l "
            "ON l.kind = 'ticket' AND l.pops_key = m.ticket_id::text WHERE NOT m.internal AND NOT EXISTS "
            "(SELECT 1 FROM glpi_links f WHERE f.kind = 'followup' AND f.pops_key = m.id::text) "
            "ORDER BY m.id LIMIT 1000",
            fetch=True,
        )
        for m in msgs or []:
            if self.late():
                self.n["deferred"] += 1
                continue
            try:
                fid = await self.create("ITILFollowup", {
                    "itemtype": "Ticket", "items_id": m["glpi_id"], "is_private": 0,
                    "content": _html("%s (POps): %s" % (m["author"], m["body"])),
                    "date": m["created_at"].astimezone().strftime("%Y-%m-%d %H:%M:%S"),
                })
                await _link("followup", str(m["id"]), fid)
                self.n["followups_created"] += 1
            except GlpiFatal:
                raise
            except GlpiError as exc:
                # GLPI'nin reddettiği yanıt (ör. kapanmış talebe) her turda yeniden denenmez
                await _link("followup", str(m["id"]), None, None, "broken", str(exc))
                self.item_error("yanıt #%s" % m["id"], exc)


async def run(trigger: str = "schedule") -> dict:
    """Bir eşitleme turu. Sonucu glpi_last_run'a yazar ve döner: {ok, at, trigger, duration, counts, error, errors}."""
    s = await load()
    started = time.monotonic()
    result = {"ok": False, "at": datetime.datetime.now().astimezone().isoformat(timespec="seconds"),
              "trigger": trigger, "counts": {}, "error": None, "errors": []}
    _progress.clear()
    _progress.update(started_at=result["at"], trigger=trigger)
    async with db.acquire() as lock_conn:
        if not await lock_conn.fetchval("SELECT pg_try_advisory_lock($1)", _LOCK):
            result["error"] = "Başka bir eşitleme sürüyor."
            _progress.clear()
            return result
        try:
            client = None
            try:
                client = await asyncio.to_thread(Client, s["url"], s["app_token"], s["user_token"])
                await asyncio.to_thread(client.open)
                if s["entity"]:
                    await client.a("POST", "changeActiveEntities", {"entities_id": s["entity"], "is_recursive": True})
                job = _Run(client, s, started + RUN_LIMIT_SECONDS)
                if s["sync"]["computers"]:
                    await job.computers()
                if s["sync"]["tickets"]:
                    await job.tickets()
                result.update(ok=True, counts=job.n, errors=job.errors)
            except GlpiError as exc:
                result["error"] = str(exc)
            except Exception:
                log.exception("GLPI eşitlemesi beklenmedik bir hatayla durdu")
                result["error"] = "Beklenmeyen hata; ayrıntı sunucu logunda."
            finally:
                if client:
                    await asyncio.to_thread(client.close)
                    result["requests"] = client.calls
        finally:
            await lock_conn.execute("SELECT pg_advisory_unlock($1)", _LOCK)
    result["duration"] = round(time.monotonic() - started, 1)
    prev = s.get("last_run") or {}
    result["failures"] = 0 if result["ok"] else int(prev.get("failures") or 0) + 1
    await _put("glpi_last_run", json.dumps(result, ensure_ascii=False))
    log.info("GLPI eşitlemesi", extra={"ok": result["ok"], "trigger": trigger, "duration": result["duration"],
                                       **(result["counts"] or {})})
    if result["failures"] == FAILURE_NOTIFY_AFTER:
        from pops.notify import notify
        await notify("glpi_failed", "high", "GLPI eşitlemesi üst üste %d kez başarısız" % FAILURE_NOTIFY_AFTER,
                     result["error"] or "")
    _progress.clear()
    return result


def start(trigger: str = "manual") -> bool:
    """Arka planda bir tur başlatır; zaten sürüyorsa False."""
    global _task
    if _task and not _task.done():
        return False
    _task = asyncio.create_task(run(trigger))
    return True


async def stop() -> None:
    """Kapanışta süren tur iptal edilir; yarıda kalan kayıtların özeti yazılmadığı için sonraki turda yeniden gider."""
    if _task and not _task.done():
        _task.cancel()
        try:
            await _task
        except (asyncio.CancelledError, Exception):
            pass


async def wait(timeout: float) -> Optional[dict]:
    if _task:
        try:
            return await asyncio.wait_for(asyncio.shield(_task), timeout)
        except asyncio.TimeoutError:
            return None
    return None


async def maybe_start() -> None:
    """Zamanlayıcı her turda çağırır: açık, aralığı olan ve vakti gelmiş eşitlemeyi arka planda başlatır."""
    if _task and not _task.done():
        return
    rows = await execute_query(
        "SELECT key, value FROM global_settings WHERE key IN ('glpi_enabled', 'glpi_interval_hours', 'glpi_last_run')",
        fetch=True,
    )
    raw = {r["key"]: r["value"] for r in rows or []}
    if raw.get("glpi_enabled") != "1":
        return
    try:
        hours = int(raw.get("glpi_interval_hours") or 24)
    except ValueError:
        return
    if hours <= 0:
        return
    last = _json(raw.get("glpi_last_run"), {}).get("at")
    if last:
        try:
            age = datetime.datetime.now().astimezone() - datetime.datetime.fromisoformat(last)
        except ValueError:
            age = None
        if age is not None and age < datetime.timedelta(hours=hours):
            return
    start("schedule")
