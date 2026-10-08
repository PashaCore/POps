"""Sınav modu: bir sınıfın bilgisayarlarında süreli ağ kısıtlaması (bkz. docs/api.md "Exam mode", docs/security.md).

Sınav sınıf başına tutulur (exam_sessions; bir sınıfta en fazla bir süren sınav) ve sınıfın bağlı ajanlarına gider:
    {"action": "exam_mode", "enabled": true, "allow": [...], "until": <unix>, "now": <unix>, "message": "...",
     "block_apps": [...]}
Bitince {"action": "exam_mode", "enabled": false}. Ajan, POps sunucusu, DNS, DHCP ve izin listesi dışındaki trafiği
keser, tepside mesajı gösterir, listedeki programları engeller ve until'de (çevrimdışı da olsa) kendiliğinden çıkar.
Durumunu exam_state ile bildirir; yerel "exam" yeteneği kapalıysa capability_denied ile reddeder.

Teslim ve kurtarma:
  * bağlanan ajana sınıfının süren sınavı yeniden gönderilir; sınav sürerken sınıfa taşınan bilgisayar da alır
    (bağlıysa hemen, değilse bağlanınca);
  * sınav bitince (panelden ya da süresi dolunca) bağlı ajanlara enabled:false gider; o sırada çevrimdışı olan ve
    sınavı süresinden önce biten bilgisayar bağlanınca alır. Sınıftan çıkarılan bilgisayar da enabled:false alır;
  * sınav yokken "sınavdayım" diyen ajana enabled:false yeniden gönderilir (en fazla dakikada bir).
Sınav sürerken "sınavda değilim" diyen ajan (yerel olarak kapatılmış ya da kurcalanmış) bildirim ve denetim kaydı
üretir. Bu bir ağ kısıtlaması ve uyarıdır, gözetim değildir: yerel yönetici kapatabilir (docs/security.md).
"""

import asyncio
import datetime
import ipaddress
import json
import logging
import re
import time
from typing import Dict, Iterable, List, Optional

import asyncpg

from pops import modules, timeutil
from pops.labs import UNASSIGNED_LAB
from pops.audit import add_audit_log
from pops.db import execute_query
from pops.manager import manager
from pops.notify import notify

log = logging.getLogger("pops.exams")

UNASSIGNED = UNASSIGNED_LAB
MAX_ALLOW = 50
MAX_BLOCK_APPS = 50
MAX_SECONDS = 8 * 3600
MIN_SECONDS = 60
MESSAGE_MAX = 200
REASON_MAX = 300
DEFAULT_MESSAGE = "Sınav modu: yalnızca izin verilen adresler açık."
# Gönderimden sonra ajanın sınava girdiğini bildirmesi beklenen süre. Bu sürede gelen "sınavda değilim" erken çıkış
# sayılmaz (bağlanan ajan önce eski durumunu bildirebilir); hiç yanıt gelmezse ajan sınav modunu tanımıyor (eski ajan).
ACK_GRACE_SECONDS = 20
# Ajanın bildirdiği durum sunucununkiyle uyuşmazsa düzeltme en fazla bu aralıkla yeniden gönderilir
RESEND_SECONDS = 60
STATES = ("in_exam", "left", "unreachable", "unsupported", "pending", "denied")
DISABLE = {"action": "exam_mode", "enabled": False}

# Ajanın ve Windows oturumunun kendi süreçleri engellenemez (tepsi mesajı explorer'ın bildirim alanında görünür)
PROTECTED_APPS = frozenset((
    "popsagent.exe", "popstray.exe", "popswatchdog.exe", "popsupdater.exe", "popsvision.exe", "pashacoreagent.exe",
    "msiexec.exe", "explorer.exe", "csrss.exe", "wininit.exe", "winlogon.exe", "services.exe", "lsass.exe",
    "smss.exe", "svchost.exe", "dwm.exe", "logonui.exe", "fontdrvhost.exe",
))

_COLS = (
    "id, lab_name, allow_list, until_at, message, block_apps, reason, started_by, started_at, ended_by, ended_at, "
    "end_reason"
)
_LABEL = re.compile(r"^(?!-)[a-z0-9-]{1,63}(?<!-)$")
_APP = re.compile(r"^[\w .()+-]{1,80}\.exe$")
_CONTROL = re.compile(r"[\x00-\x1f\x7f]+")
_resent: Dict[str, float] = {}


# ── Doğrulama (saf işlevler; birim testleri) ─────────────────────────────────────
def clean_text(value, limit: int) -> str:
    """Kontrol karakterleri boşluk olur, baştaki/sondaki boşluk atılır, uzunluk sınırlanır."""
    return _CONTROL.sub(" ", str(value or "")).strip()[:limit]


def clean_allow_entry(raw) -> str:
    """Alan adı, IP adresi ya da CIDR'nin normal hâli. Geçersizse ValueError (gerekçe Türkçe).

    Yapıştırılan adresin şeması, yolu ve kapısı atılır (https://sinav.meb.gov.tr/giris -> sinav.meb.gov.tr). Türkçe
    harfli alan adı punycode'a çevrilir. Joker (*.) kabul edilmez: ajan adları çözerek izin verir."""
    s = str(raw if raw is not None else "").strip().lower()
    if not s:
        raise ValueError("boş giriş")
    if len(s) > 300:
        raise ValueError("çok uzun")
    if "://" in s:
        s = s.split("://", 1)[1].split("/", 1)[0].rsplit("@", 1)[-1]
        if s.startswith("["):
            s = s[1:].split("]", 1)[0]
        elif s.count(":") == 1:
            s = s.split(":", 1)[0]
    if "/" in s:
        try:
            net = ipaddress.ip_network(s, strict=False)
        except ValueError:
            host = s.split("/", 1)[0]
            try:
                ipaddress.ip_address(host)
            except ValueError:
                return clean_allow_entry(host)   # alan adı + yol
            raise ValueError("geçersiz CIDR")
        if net.prefixlen < (8 if net.version == 4 else 32):
            raise ValueError("ağ çok geniş (IPv4'te en az /8, IPv6'da en az /32)")
        return str(net.network_address) if net.num_addresses == 1 else str(net)
    try:
        return str(ipaddress.ip_address(s))
    except ValueError:
        pass
    if "*" in s:
        raise ValueError("joker (*) desteklenmez; alan adlarını tek tek yazın")
    s = s.rstrip(".")
    try:
        s = s.encode("idna").decode("ascii")
    except UnicodeError:
        raise ValueError("alan adı, IP adresi ya da CIDR olmalı")
    labels = s.split(".")
    if (len(s) > 253 or len(labels) < 2 or not all(_LABEL.match(x) for x in labels)
            or labels[-1].isdigit()):
        raise ValueError("alan adı, IP adresi ya da CIDR olmalı")
    return s


def clean_allow(entries: Iterable) -> List[str]:
    """İzin listesi: normal hâller, tekrarsız, sırası korunur, en fazla MAX_ALLOW. İlk geçersiz giriş ValueError."""
    out = []
    for raw in entries or []:
        try:
            entry = clean_allow_entry(raw)
        except ValueError as exc:
            raise ValueError("Geçersiz izin girişi '%s': %s." % (str(raw)[:80], exc))
        if entry not in out:
            out.append(entry)
    if len(out) > MAX_ALLOW:
        raise ValueError("İzin listesinde en fazla %d giriş olabilir (%d verildi)." % (MAX_ALLOW, len(out)))
    return out


def clean_apps(entries: Iterable) -> List[str]:
    """Engellenecek programlar: yalnızca dosya adı (yol atılır), küçük harf, uzantısızsa .exe eklenir."""
    out = []
    for raw in entries or []:
        name = str(raw or "").strip().replace("/", "\\").rsplit("\\", 1)[-1].strip().lower()
        if not name:
            continue
        if "." not in name:
            name += ".exe"
        if not _APP.match(name):
            raise ValueError("Geçersiz program adı '%s': ör. cmd.exe." % str(raw)[:80])
        if name in PROTECTED_APPS:
            raise ValueError("'%s' engellenemez: POps ya da Windows oturumu için gerekli." % name)
        if name not in out:
            out.append(name)
    if len(out) > MAX_BLOCK_APPS:
        raise ValueError("En fazla %d program engellenebilir." % MAX_BLOCK_APPS)
    return out


def resolve_until(until: Optional[float], duration_minutes: Optional[int], now: Optional[float] = None) -> int:
    """Bitiş (unix saniye): until ya da süre (dakika), ikisinden biri. En az 1 dakika, en çok 8 saat sonra."""
    now = time.time() if now is None else now
    if (until is None) == (duration_minutes is None):
        raise ValueError("Bitiş zamanı (until) ya da süre (duration_minutes) verilmeli; ikisi birden değil.")
    value = now + duration_minutes * 60 if until is None else float(until)
    if value < now + MIN_SECONDS:
        raise ValueError("Bitiş zamanı gelecekte olmalı (en az 1 dakika sonra).")
    if value > now + MAX_SECONDS:
        raise ValueError("Sınav en çok 8 saat sürebilir.")
    return int(value)


def _ts(value) -> Optional[datetime.datetime]:
    """Ajanın gönderdiği unix zamanı (2000–2100 arası); başka her şey None."""
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        return None
    if not 946684800 <= value <= 4102444800:
        return None
    return datetime.datetime.fromtimestamp(value, datetime.timezone.utc)


def _epoch(dt) -> Optional[float]:
    return dt.timestamp() if dt is not None else None


def _iso(dt) -> Optional[str]:
    return timeutil.iso(dt)   # API biçimi: sunucunun saat diliminde ofsetli ISO 8601 (bkz. pops/timeutil.py)


def device_state(online: bool, row: Optional[dict], now: float) -> str:
    """Bir bilgisayarın süren sınavdaki durumu (STATES). row: exam_devices satırı (yoksa None)."""
    if not online:
        return "unreachable"
    sent = _epoch((row or {}).get("sent_at"))
    if sent is None:
        return "pending"
    reported, denied = _epoch(row.get("reported_at")), _epoch(row.get("denied_at"))
    if denied is not None and (reported is None or denied >= reported):
        return "denied"
    if reported is None:
        return "unsupported" if now - sent > ACK_GRACE_SECONDS else "pending"
    if row.get("enabled"):
        return "in_exam"
    return "pending" if now - sent <= ACK_GRACE_SECONDS else "left"


def public(row: dict, now: Optional[float] = None) -> dict:
    """Sınav kaydının API biçimi (until: unix saniye; diğer zamanlar ISO 8601)."""
    now = time.time() if now is None else now
    until = row["until_at"].timestamp()
    active = row["ended_at"] is None
    return {
        "id": row["id"],
        "lab": row["lab_name"],
        "allow": _json_list(row["allow_list"]),
        "until": int(until),
        "until_at": _iso(row["until_at"]),
        "message": row["message"],
        "block_apps": _json_list(row["block_apps"]),
        "reason": row["reason"],
        "started_by": row["started_by"],
        "started_at": _iso(row["started_at"]),
        "ended_by": row["ended_by"],
        "ended_at": _iso(row["ended_at"]),
        "end_reason": row["end_reason"],
        "active": active,
        "remaining_seconds": max(0, int(until - now)) if active else 0,
    }


def _json_list(value) -> list:
    if isinstance(value, list):
        return value
    try:
        out = json.loads(value or "[]")
    except (TypeError, ValueError):
        return []
    return out if isinstance(out, list) else []


def agent_message(exam: dict) -> dict:
    return {
        "action": "exam_mode",
        "enabled": True,
        "allow": exam["allow"],
        "until": exam["until"],
        # Sunucunun saati: ajan kalan süreyi until - now ile bulup kendi monotonik saatiyle sayar; PC saati ileri ya da
        # geri olsa da sınavın süresi değişmez (eski ajanlar alanı yok sayar ve until'i kendi saatiyle karşılaştırır)
        "now": int(time.time()),
        "message": exam["message"],
        "block_apps": exam["block_apps"],
    }


# ── Sorgular ─────────────────────────────────────────────────────────────────
async def lab_exists(lab: str) -> bool:
    rows = await execute_query(
        "SELECT 1 FROM custom_labs WHERE lab_name = $1 UNION ALL SELECT 1 FROM clients WHERE lab_name = $1 LIMIT 1",
        (lab,),
        fetch=True,
    )
    return bool(rows)


async def lab_pcs(lab: str) -> List[str]:
    rows = await execute_query("SELECT pc_name FROM clients WHERE lab_name = $1 ORDER BY pc_name", (lab,), fetch=True)
    return [r["pc_name"] for r in rows or []]


async def active_exam(lab: Optional[str]) -> Optional[dict]:
    """Sınıfın süren sınavı. Süresi dolmuş ama zamanlayıcının henüz kapatmadığı sınav önce kapatılır."""
    if not lab:
        return None
    rows = await execute_query(
        "SELECT %s FROM exam_sessions WHERE lab_name = $1 AND ended_at IS NULL" % _COLS, (lab,), fetch=True
    )
    if not rows:
        return None
    if rows[0]["until_at"].timestamp() <= time.time():
        await end_expired()
        return None
    return public(rows[0])


# ── Gönderim ─────────────────────────────────────────────────────────────────
async def _send(pcs: Iterable[str], message: dict) -> List[str]:
    pcs = list(dict.fromkeys(pcs))
    up = await manager.online_among(pcs)   # birden fazla süreçte bütün süreçlerin ajanları
    online = [pc for pc in pcs if pc in up]
    if not online:
        return []
    results = await asyncio.gather(*[manager.send_command(message, pc) for pc in online])
    return [pc for pc, ok in zip(online, results) if ok]


async def deliver(exam: dict, pcs: Iterable[str]) -> List[str]:
    """Sınavı bağlı bilgisayarlara gönderir; ulaşanları döner. Gönderim zamanı mesajdan ÖNCE yazılır: ajanın hemen
    gelen exam_state'i gönderilmemiş bir sınava ait sanılmasın. Bilgisayarın önceki bir sınavı (başka sınıftan
    taşındı) bu gönderimle geçersizleşir: ajanın ayarı yenisiyle değişir."""
    pcs = list(dict.fromkeys(pcs))
    up = await manager.online_among(pcs)
    online = [pc for pc in pcs if pc in up]
    if not online:
        return []
    await execute_query(
        "INSERT INTO exam_devices (exam_id, pc_name, first_sent_at, sent_at) "
        "SELECT $1, p, NOW(), NOW() FROM unnest($2::text[]) AS p "
        "ON CONFLICT (exam_id, pc_name) DO UPDATE SET sent_at = NOW(), released_at = NULL, "
        "first_sent_at = COALESCE(exam_devices.first_sent_at, NOW())",
        (exam["id"], online),
    )
    await execute_query(
        "UPDATE exam_devices SET released_at = NOW() WHERE pc_name = ANY($1::text[]) AND exam_id <> $2 "
        "AND released_at IS NULL",
        (online, exam["id"]),
    )
    return await _send(online, agent_message(exam))


async def release(pcs: Iterable[str]) -> List[str]:
    """enabled:false gönderir; ulaşan bilgisayarların açık sınav satırları kapanır (ulaşmayan bağlanınca alır)."""
    sent = await _send(pcs, DISABLE)
    if sent:
        await execute_query(
            "UPDATE exam_devices SET released_at = NOW() WHERE pc_name = ANY($1::text[]) AND released_at IS NULL",
            (sent,),
        )
    return sent


async def _resend(pc_name: str, exam: Optional[dict]) -> bool:
    now = time.monotonic()
    if now - _resent.get(pc_name, -1e9) < RESEND_SECONDS:
        return False
    if len(_resent) > 10000:
        _resent.clear()
    _resent[pc_name] = now
    sent = await (deliver(exam, [pc_name]) if exam is not None else release([pc_name]))
    return bool(sent)


async def sync_pc(pc_name: str, lab: Optional[str] = None, known_lab: bool = False) -> Optional[str]:
    """Bağlanan ya da taşınan bilgisayarı sınıfının durumuna getirir: süren sınav varsa gönderilir; yoksa ve
    bilgisayarda hâlâ sürüyor olabilecek (bitişi gelmemiş, kapatılmamış) bir sınav varsa enabled:false gider.
    Dönen: "enabled", "disabled" ya da None (bir şey gönderilmedi)."""
    if not known_lab:
        lab = await modules.lab_of(pc_name)
    exam = await active_exam(lab)
    if exam is not None and await modules.enabled("exam", lab):
        return "enabled" if await deliver(exam, [pc_name]) else None
    stale = await execute_query(
        "SELECT 1 FROM exam_devices d JOIN exam_sessions e ON e.id = d.exam_id WHERE d.pc_name = $1 "
        "AND d.sent_at IS NOT NULL AND d.released_at IS NULL AND e.until_at > NOW() LIMIT 1",
        (pc_name,),
        fetch=True,
    )
    if stale:
        return "disabled" if await release([pc_name]) else None
    return None


async def sync_pcs(pc_names: Iterable[str], lab: str) -> None:
    """Taşınan bilgisayarlar: yalnızca bağlı olanlar (diğerleri bağlanınca eşitlenir)."""
    for pc in dict.fromkeys(pc_names):
        if await manager.is_online(pc):
            await sync_pc(pc, lab, known_lab=True)


# ── Başlatma ve bitirme ──────────────────────────────────────────────────────
class ExamConflict(Exception):
    pass


async def start(lab: str, allow: List[str], until: int, message: str, block_apps: List[str], reason: str,
                by: str) -> dict:
    """Sınavı kaydeder, denetim kaydına yazar ve sınıfın bağlı ajanlarına gönderir. Sınıfta süren sınav varsa
    ExamConflict (bir sınıfta tek sınav: kısmi tekil indeks, eşzamanlı iki istekten biri kazanır)."""
    try:
        rows = await execute_query(
            "INSERT INTO exam_sessions (lab_name, allow_list, until_at, message, block_apps, reason, started_by) "
            "VALUES ($1, $2::jsonb, $3, $4, $5::jsonb, $6, $7) RETURNING %s" % _COLS,
            (
                lab,
                json.dumps(allow),
                datetime.datetime.fromtimestamp(until, datetime.timezone.utc),
                message,
                json.dumps(block_apps),
                reason,
                by,
            ),
            fetch=True,
        )
    except asyncpg.UniqueViolationError:
        raise ExamConflict()
    exam = public(rows[0])
    await add_audit_log(
        "*",
        "exam_start",
        "Sınav modu başlatıldı: %s (%s)" % (lab, by),
        {"exam_id": exam["id"], "lab": lab, "by": by, "reason": reason, "allow": allow, "until": exam["until_at"],
         "block_apps": block_apps, "message": message},
    )
    pcs = await lab_pcs(lab)
    sent = await deliver(exam, pcs)
    log.info("sınav başladı", extra={"lab": lab, "exam_id": exam["id"], "pcs": len(pcs), "delivered": len(sent)})
    return {"exam": exam, "devices": len(pcs), "delivered": len(sent)}


async def _after_end(exam: dict, by: str, note: str = "") -> List[str]:
    """Biten sınav: sınıftaki ve sınavı almış bütün bağlı bilgisayarlara enabled:false, denetim kaydı."""
    got = await execute_query(
        "SELECT pc_name FROM exam_devices WHERE exam_id = $1 AND released_at IS NULL", (exam["id"],), fetch=True
    )
    pcs = await lab_pcs(exam["lab"]) + [r["pc_name"] for r in got or []]
    sent = await release(pcs)
    auto = exam["end_reason"] == "expired"
    text = "Sınav modu süresi doldu: %s" % exam["lab"] if auto else "Sınav modu bitirildi: %s (%s)" % (exam["lab"], by)
    await add_audit_log(
        "*",
        "exam_auto_end" if auto else "exam_end",
        text,
        {"exam_id": exam["id"], "lab": exam["lab"], "by": by, "end_reason": exam["end_reason"], "note": note or None,
         "started_by": exam["started_by"], "until": exam["until_at"], "released": len(sent)},
    )
    log.info("sınav bitti", extra={"lab": exam["lab"], "exam_id": exam["id"], "end_reason": exam["end_reason"]})
    return sent


async def end(lab: str, by: str, end_reason: str = "admin", note: str = "") -> Optional[dict]:
    """Sınıfın süren sınavını bitirir; yoksa None. Dönen: {"exam", "delivered"}."""
    rows = await execute_query(
        "UPDATE exam_sessions SET ended_at = NOW(), ended_by = $2, end_reason = $3 "
        "WHERE lab_name = $1 AND ended_at IS NULL RETURNING %s" % _COLS,
        (lab, by, end_reason),
        fetch=True,
    )
    if not rows:
        return None
    exam = public(rows[0])
    sent = await _after_end(exam, by, note)
    return {"exam": exam, "delivered": len(sent)}


async def end_expired() -> int:
    """Bitişi gelen sınavları kapatır (zamanlayıcı her turda; okuma uçları da önce bunu çağırır). Ajan until'de
    zaten kendiliğinden çıkar; enabled:false yine gönderilir. Birden fazla süreç aynı anda çağırsa da her sınavı
    yalnızca biri kapatır (UPDATE ... RETURNING)."""
    rows = await execute_query(
        "UPDATE exam_sessions SET ended_at = NOW(), ended_by = 'system', end_reason = 'expired' "
        "WHERE ended_at IS NULL AND until_at <= NOW() RETURNING %s" % _COLS,
        fetch=True,
    )
    for row in rows or []:
        await _after_end(public(row), "system")
    return len(rows or [])


async def end_where_module_off() -> int:
    """Sınav modülü kapatılan sınıflardaki süren sınavlar biter (modül ayarı değişince)."""
    rows = await execute_query("SELECT lab_name FROM exam_sessions WHERE ended_at IS NULL", fetch=True)
    ended = 0
    for r in rows or []:
        if not await modules.enabled("exam", r["lab_name"]) and await end(r["lab_name"], "system", "module_off"):
            ended += 1
    return ended


# ── Ajanın bildirdikleri ─────────────────────────────────────────────────────
async def _active_for_pc(pc_name: str):
    lab = await modules.lab_of(pc_name)
    exam = await active_exam(lab)
    if exam is not None and not await modules.enabled("exam", lab):
        exam = None
    return lab, exam


async def on_agent_state(pc_name: str, payload: dict) -> None:
    """exam_state: {"type": "exam_state", "enabled": bool, "since": ts, "until": ts}."""
    enabled = payload.get("enabled")
    if not isinstance(enabled, bool):
        log.info("geçersiz exam_state yok sayıldı", extra={"pc_name": pc_name})
        return
    since, until = _ts(payload.get("since")), _ts(payload.get("until"))
    lab, exam = await _active_for_pc(pc_name)
    if exam is None:
        if enabled:
            # Sınıfında sınav yok (bitti, bilgisayar taşındı ya da modül kapandı) ama ajan hâlâ sınavda
            await _resend(pc_name, None)
        else:
            await execute_query(
                "UPDATE exam_devices SET released_at = NOW() WHERE pc_name = $1 AND released_at IS NULL", (pc_name,)
            )
        return
    rows = await execute_query(
        "INSERT INTO exam_devices (exam_id, pc_name, reported_at, enabled, agent_since, agent_until, entered_at) "
        "VALUES ($1, $2, NOW(), $3, $4, $5, CASE WHEN $3 THEN NOW() END) "
        "ON CONFLICT (exam_id, pc_name) DO UPDATE SET reported_at = NOW(), enabled = $3, agent_since = $4, "
        "agent_until = $5, entered_at = COALESCE(exam_devices.entered_at, CASE WHEN $3 THEN NOW() END) "
        "RETURNING sent_at, denied_at, EXTRACT(EPOCH FROM (NOW() - sent_at)) AS since_sent",
        (exam["id"], pc_name, enabled, since, until),
        fetch=True,
    )
    row = rows[0]
    if row["sent_at"] is None:
        # Bu sınav bu bilgisayara hiç gönderilmedi (ör. bağlıyken taşındı ve gönderim kaçtı)
        await deliver(exam, [pc_name])
        return
    if enabled:
        if until is not None and abs(until.timestamp() - exam["until"]) > 5:
            await _resend(pc_name, exam)   # ajan başka (eski) bir sınavın ayarıyla çalışıyor
        return
    # Sınav sürerken "sınavda değilim": gönderimden hemen sonra (ajan henüz uygulamadı), bitişe bir dakikadan az
    # kala (ajanın saati önde) ya da ret zaten bildirildiyse erken çıkış sayılmaz
    if float(row["since_sent"]) < ACK_GRACE_SECONDS or exam["until"] - time.time() < 60:
        return
    if row["denied_at"] is not None and row["denied_at"] >= row["sent_at"]:
        return
    left = await execute_query(
        "UPDATE exam_devices SET left_at = NOW() WHERE exam_id = $1 AND pc_name = $2 AND left_at IS NULL "
        "RETURNING left_at",
        (exam["id"], pc_name),
        fetch=True,
    )
    if not left:
        return
    await add_audit_log(
        pc_name,
        "exam_left",
        "Bilgisayar sınav bitmeden sınav modundan çıktı: %s" % lab,
        {"exam_id": exam["id"], "lab": lab, "agent_since": _iso(since), "agent_until": _iso(until)},
    )
    await notify(
        "exam_left",
        "high",
        "Sınav bitmeden sınav modundan çıktı (%s)" % lab,
        "Ajan sınav modunun kapandığını bildirdi: bilgisayarda yerel olarak kapatılmış ya da kurcalanmış olabilir.",
        pc_name,
    )


async def on_denied(pc_name: str) -> None:
    """capability_denied (capability: exam): ajan yerel ayarla sınav modunu reddetti."""
    _lab, exam = await _active_for_pc(pc_name)
    if exam is None:
        return
    await execute_query(
        "INSERT INTO exam_devices (exam_id, pc_name, denied_at) VALUES ($1, $2, NOW()) "
        "ON CONFLICT (exam_id, pc_name) DO UPDATE SET denied_at = NOW()",
        (exam["id"], pc_name),
    )


# ── Okuma ────────────────────────────────────────────────────────────────────
async def lab_devices(exam: dict) -> List[dict]:
    """Sınıftaki bilgisayarların süren sınavdaki durumu."""
    rows = await execute_query(
        "SELECT c.pc_name, c.hostname, c.display_name, c.running_version, av.version AS agent_version, "
        "d.first_sent_at, d.sent_at, d.reported_at, d.enabled, d.agent_since, d.agent_until, d.entered_at, "
        "d.left_at, d.denied_at FROM clients c "
        "LEFT JOIN agent_versions av ON av.pc_name = c.pc_name "
        "LEFT JOIN exam_devices d ON d.pc_name = c.pc_name AND d.exam_id = $2 "
        "WHERE c.lab_name = $1 ORDER BY c.pc_name",
        (exam["lab"], exam["id"]),
        fetch=True,
    )
    now = time.time()
    out = []
    up = await manager.online_among([r["pc_name"] for r in rows or []])
    for r in rows or []:
        online = r["pc_name"] in up
        out.append({
            "pc_name": r["pc_name"],
            "name": r["display_name"] or r["hostname"] or r["pc_name"],
            "online": online,
            "state": device_state(online, r, now),
            "agent_version": r["running_version"] or r["agent_version"],
            "enabled": r["enabled"],
            "since": int(r["agent_since"].timestamp()) if r["agent_since"] else None,
            "until": int(r["agent_until"].timestamp()) if r["agent_until"] else None,
            "sent_at": _iso(r["sent_at"]),
            "reported_at": _iso(r["reported_at"]),
            "entered_at": _iso(r["entered_at"]),
            "left_at": _iso(r["left_at"]),
            "denied_at": _iso(r["denied_at"]),
        })
    return out


def counts(devices: List[dict]) -> Dict[str, int]:
    out = {s: 0 for s in STATES}
    for d in devices:
        out[d["state"]] += 1
    return out


async def lab_state(lab: str) -> dict:
    exam = await active_exam(lab)
    devices = await lab_devices(exam) if exam is not None else []
    return {
        "lab": lab,
        "module_enabled": await modules.enabled("exam", lab),
        "active": exam is not None,
        "exam": exam,
        "devices": devices,
        "counts": counts(devices),
    }


async def history(lab: Optional[str] = None, active: Optional[bool] = None, limit: int = 50,
                  labs: Optional[List[str]] = None) -> List[dict]:
    """labs verilirse yalnızca o sınıfların sınavları (kurum birimi kapsamı, pops/tenancy.py)."""
    where, params = [], []
    if labs is not None:
        params.append(list(labs))
        where.append("e.lab_name = ANY($%d::text[])" % len(params))
    if lab:
        params.append(lab)
        where.append("e.lab_name = $%d" % len(params))
    if active is not None:
        where.append("e.ended_at IS NULL" if active else "e.ended_at IS NOT NULL")
    params.append(limit)
    rows = await execute_query(
        "SELECT %s, (SELECT count(*) FROM exam_devices d WHERE d.exam_id = e.id AND d.first_sent_at IS NOT NULL) "
        "AS sent_n, (SELECT count(*) FROM exam_devices d WHERE d.exam_id = e.id AND d.entered_at IS NOT NULL) "
        "AS entered_n, (SELECT count(*) FROM exam_devices d WHERE d.exam_id = e.id AND d.left_at IS NOT NULL) "
        "AS left_n "
        "FROM exam_sessions e %s ORDER BY e.started_at DESC, e.id DESC LIMIT $%d"
        % (_COLS, ("WHERE " + " AND ".join(where)) if where else "", len(params)),
        tuple(params),
        fetch=True,
    )
    out = []
    for r in rows or []:
        item = public(r)
        item["devices_sent"], item["devices_entered"], item["devices_left"] = r["sent_n"], r["entered_n"], r["left_n"]
        if item["active"] and active:
            item["counts"] = counts(await lab_devices(item))
        out.append(item)
    return out
