"""Cihaz listesi (/api/devices): sürüm sayacı, sınırlı değişiklik günlüğü ve panele "değişti" bildirimi.

Panel listeyi eskiden 5 sn'de bir baştan sona çekiyordu (2.040 cihazda 1,39 MB). Şimdi:
  * sunucu listenin bellekte bir kopyasını (cihaz başına API satırı) ve bir sürüm sayacını tutar. Bir satır gerçekten
    değişince sayaç bir artar ve cihaz değişiklik günlüğüne yazılır;
  * GET /api/devices zayıf ETag (W/"d<sürüm>") döner, If-None-Match tutarsa 304 (gövdesiz);
  * GET /api/devices?since=<sürüm> o sürümden sonra değişen satırları, silinen cihazları ve yalnızca last_seen'i
    ilerleyenleri döner; günlük o kadar geriye gitmiyorsa tam liste (full: true);
  * sürüm değişince ?topics=devices ile açılmış panel soketlerine en fazla saniyede bir
    {"type": "devices_changed", "version": n} gider (bkz. pops/manager.py PANEL_TOPICS).

Değişiklik nasıl bulunur: satırı yazan yer cihazı "kirli" işaretler (touch; sıcak yollar: heartbeat, ajan
bağlanma/kopma) ya da hemen yeniden okutur (sync; panelden yapılan işlemler, yanıt dönmeden sürüm artsın diye).
Kirli satırlar veritabanından okunup kopyayla karşılaştırılır; sayaç yalnızca satır farklıysa artar. Heartbeat'in
yalnızca last_seen'i ileri alması değişiklik sayılmaz, yoksa her heartbeat yeni bir sürüm olurdu. Dakikada bir bütün
liste karşılaştırılır: işaretlemeyi unutan bir yazım en geç o zaman yakalanır ve last_seen'i ilerleyen satırlar
"seen" olarak yayımlanır (panel last_seen'i en fazla bu kadar gecikmeyle görür).

Sürüm, açılış anının milisaniyesiyle başlar ve milisaniyeden çok daha yavaş artar (turda en fazla bir, artı panel
işlemleri): yeniden başlatmadan önceki bir sürüm yeni sürecin günlüğünden her zaman eskidir (tam liste döner). Tek
uvicorn worker'ı varsayılır (bkz. docs/decisions.md).
"""

import asyncio
import collections
import logging
import time
from typing import Iterable, Optional

from pops import agent_health, metrics
from pops.db import execute_query

log = logging.getLogger("pops.devicelist")

LOG_MAX_ENTRIES = 5000      # günlükte en çok bu kadar cihaz değişikliği
LOG_MAX_AGE = 600.0         # ve en çok bu kadar saniyelik geçmiş; daha eski "since" tam liste alır
ROUND_SECONDS = 1.0         # bir tur (kirli satırları okuma + bildirim) en fazla saniyede bir
SCAN_SECONDS = 60.0         # bütün listenin karşılaştırılması ve last_seen yayımı

_QUERY = """
    SELECT
        c.pc_name, c.hostname, c.display_name, c.lab_name, c.last_seen, c.status, c.active_window,
        c.boot_count, c.logged_user, c.ip_address, c.cap_ram_readable, c.is_quarantined,
        c.cap_terminal_enabled, c.cap_vision_enabled, c.cap_server_ca,
        c.cap_terminal_disable_requested, c.cap_vision_disable_requested, c.running_version,
        c.agent_health, c.last_disconnect_at, c.last_disconnect_reason,
        bk.pc_name AS bypass_key_issued, bk.confirmed_at AS bypass_key_confirmed,
        av.version AS agent_version
    FROM clients c
    LEFT JOIN agent_versions av ON c.pc_name = av.pc_name
    LEFT JOIN agent_bypass_keys bk ON c.pc_name = bk.pc_name
"""


def api_row(r: dict) -> dict:
    """Veritabanı satırı → /api/devices satırı (biçim değişmez: başka araçlar ve ajanların araçları da okur)."""
    return {
        "hostname": r["pc_name"],
        "real_hostname": r["hostname"] or r["pc_name"],
        "display_name": r["display_name"],
        "pc_name": r["hostname"] or r["pc_name"],
        "hw_id": r["pc_name"],
        "ip": r["ip_address"],
        "lab": r["lab_name"],
        "status": r["status"],
        "last_seen": r["last_seen"],
        "active_window": r["active_window"],
        "boot_count": r["boot_count"],
        "current_user": r.get("logged_user", "-"),
        "is_quarantined": r.get("is_quarantined", False),
        "agent_version": r.get("agent_version") or "Bilinmiyor",
        "running_version": r.get("running_version"),
        "cap_terminal_enabled": r.get("cap_terminal_enabled"),
        "cap_vision_enabled": r.get("cap_vision_enabled"),
        "cap_server_ca": r.get("cap_server_ca"),
        "cap_terminal_disable_requested": r.get("cap_terminal_disable_requested", False),
        "cap_vision_disable_requested": r.get("cap_vision_disable_requested", False),
        # Ajanın son heartbeat'teki sağlık özeti (0.1.12+; bkz. pops/agent_health.py)
        "agent_health": agent_health.parse(r.get("agent_health")),
        # Son kopuş: ne zaman, neden (WebSocket kapanış kodu)
        "last_disconnect_at": r["last_disconnect_at"].isoformat() if r.get("last_disconnect_at") else None,
        "last_disconnect_reason": r.get("last_disconnect_reason"),
        # Çevrimdışı bypass: device = cihaza özel anahtar onaylı, pending = gönderildi/onay bekliyor, None = eski
        "bypass_key": (
            "device" if r.get("bypass_key_confirmed") else "pending" if r.get("bypass_key_issued") else None
        ),
    }


async def fetch_rows(pcs: Optional[Iterable[str]] = None) -> list:
    """Bütün cihazlar ya da yalnızca verilenler, API biçiminde."""
    if pcs is None:
        rows = await execute_query(_QUERY, fetch=True)
    else:
        rows = await execute_query(_QUERY + " WHERE c.pc_name = ANY($1::text[])", (list(pcs),), fetch=True)
    return [api_row(r) for r in rows or []]


def differs(old: dict, new: dict) -> bool:
    """last_seen dışında bir alan değişti mi."""
    if len(old) != len(new):
        return True
    return any(k != "last_seen" and old.get(k) != v for k, v in new.items())


class ChangeLog:
    """Sınırlı değişiklik günlüğü: cihaz başına EN SON değişikliği (sürüm, zaman, silindi mi) sürüm sırasıyla tutar.
    En çok max_entries kayıt ve max_age saniye; düşen kaydın sürümü 'floor' olur ve floor'dan eski bir 'since'
    yanıtlanmaz (o arada neyin değiştiği artık bilinmez, istemci tam listeyi alır)."""

    def __init__(self, floor: int, max_entries: int = LOG_MAX_ENTRIES, max_age: float = LOG_MAX_AGE):
        self.floor = floor
        self.max_entries = max_entries
        self.max_age = max_age
        self._items = collections.OrderedDict()   # pc -> (sürüm, zaman, silindi)

    def __len__(self):
        return len(self._items)

    def add(self, version: int, now: float, changed: Iterable[str] = (), removed: Iterable[str] = ()) -> None:
        for pcs, gone in ((changed, False), (removed, True)):
            for pc in pcs:
                self._items.pop(pc, None)
                self._items[pc] = (version, now, gone)
        self.trim(now)

    def trim(self, now: float) -> None:
        while self._items:
            version, at, _gone = next(iter(self._items.values()))
            if len(self._items) <= self.max_entries and now - at <= self.max_age:
                break
            self._items.popitem(last=False)
            self.floor = max(self.floor, version)

    def since(self, version: int):
        """(değişenler, silinenler) — sürümü 'version'dan büyük kayıtlar; günlük yetmiyorsa None."""
        if version < self.floor:
            return None
        changed, removed = [], []
        for pc, (v, _at, gone) in reversed(self._items.items()):
            if v <= version:
                break
            (removed if gone else changed).append(pc)
        return changed, removed


class _State:
    def __init__(self):
        self.ready = False
        self.version = int(time.time() * 1000)
        self.rows = {}        # pc -> API satırı (son okunan)
        self.published = {}   # pc -> istemcilere en son gönderilen last_seen
        self.seen_ver = {}    # pc -> last_seen'inin en son yayımlandığı sürüm
        self.log = ChangeLog(self.version)
        self.dirty = set()
        self.scan_due = False


S = _State()
_lock = asyncio.Lock()
_wake = asyncio.Event()


def reset() -> None:
    """Testler için: durumu baştan kurar (sunucu açılışındaki gibi)."""
    global S, _lock, _wake
    S, _lock, _wake = _State(), asyncio.Lock(), asyncio.Event()


def etag() -> Optional[str]:
    return 'W/"d%d"' % S.version if S.ready else None


def etag_matches(header: Optional[str], tag: Optional[str]) -> bool:
    """If-None-Match karşılaştırması (zayıf). Apache mod_deflate sıkıştırınca ETag'e "-gzip" ekler; istemci o
    hâliyle geri gönderir, o da eşleşir."""
    if not header or not tag:
        return False
    want = tag[2:] if tag.startswith("W/") else tag
    for part in header.split(","):
        part = part.strip()
        if part == "*":
            return True
        if part.startswith("W/"):
            part = part[2:]
        for suffix in ('-gzip"', '-br"', '-deflate"'):
            if part.endswith(suffix):
                part = part[: -len(suffix)] + '"'
                break
        if part == want:
            return True
    return False


def delta(since: int) -> Optional[dict]:
    """'since' sürümünden bu yana değişenler; yanıtlanamıyorsa None (çağıran tam listeyi döner)."""
    if not S.ready or since > S.version:
        return None
    got = S.log.since(since)
    if got is None:
        return None
    changed_pcs, removed = got
    changed = [S.rows[pc] for pc in changed_pcs if pc in S.rows]
    skip = set(changed_pcs)
    seen = {
        pc: S.rows[pc]["last_seen"] for pc, v in S.seen_ver.items() if v > since and pc not in skip and pc in S.rows
    }
    return {"version": S.version, "full": False, "changed": changed, "removed": removed, "seen": seen}


def heartbeat_differs(pc: str, status, active_window, hostname, ip, health_json) -> bool:
    """Toplu yazılan heartbeat satırın görünen bir alanını değiştiriyor mu (yalnızca ön süzgeç: şüphede True; asıl
    karşılaştırma veritabanından okunan satırla yapılır)."""
    row = S.rows.get(pc)
    if row is None:
        return S.ready
    if row["status"] != status or row["active_window"] != active_window or row["ip"] != ip:
        return True
    if row["real_hostname"] != (hostname or pc):
        return True
    health = agent_health.parse(health_json) if health_json is not None else None
    return health != row["agent_health"]


def touch(pcs: Iterable[str]) -> None:
    """Bu cihazların satırı değişmiş olabilir: bir sonraki turda (en geç ~1 sn) veritabanından okunur."""
    S.dirty.update(pcs)
    _wake.set()


def touch_all() -> None:
    S.scan_due = True
    _wake.set()


async def sync(pcs: Optional[Iterable[str]] = None) -> None:
    """Satırları hemen yeniden okur (None: bütün liste). Panel işlemleri yanıt dönmeden çağırır: panelin hemen
    ardından gelen ?since= isteği değişikliği görür. Hata işlemi bozmaz; satırlar bir sonraki turda okunur."""
    if not S.ready:
        return
    pcs = None if pcs is None else set(pcs)
    try:
        async with _lock:
            await _refresh(pcs, scan=False)
    except Exception:
        log.warning("cihaz listesi yeniden okunamadı", exc_info=True)
        if pcs is None:
            touch_all()
        else:
            touch(pcs)
    _wake.set()


async def _refresh(pcs: Optional[set], scan: bool) -> bool:
    """Satırları okuyup kopyayla karşılaştırır; değişiklik varsa sürüm bir artar. scan=True: bütün liste ve
    last_seen'i ilerleyen satırların yayımı. _lock altında çağrılır."""
    rows = await fetch_rows(pcs)
    now = time.monotonic()
    fresh = {r["hw_id"]: r for r in rows}
    changed, seen = [], []
    for pc, row in fresh.items():
        old = S.rows.get(pc)
        if old is None or differs(old, row):
            changed.append(pc)
        elif scan and row["last_seen"] != S.published.get(pc):
            seen.append(pc)
        S.rows[pc] = row
    asked = S.rows.keys() if pcs is None else pcs
    removed = [pc for pc in asked if pc not in fresh and pc in S.rows]
    if not (changed or removed or seen):
        return False
    S.version += 1
    for pc in changed:
        S.published[pc] = fresh[pc]["last_seen"]
    for pc in seen:
        S.published[pc] = fresh[pc]["last_seen"]
        S.seen_ver[pc] = S.version
    for pc in removed:
        S.rows.pop(pc, None)
        S.published.pop(pc, None)
        S.seen_ver.pop(pc, None)
    S.log.add(S.version, now, changed, removed)
    metrics.count("device_list_versions")
    return True


async def _init() -> None:
    async with _lock:
        rows = await fetch_rows()
        S.rows = {r["hw_id"]: r for r in rows}
        S.published = {pc: r["last_seen"] for pc, r in S.rows.items()}
        S.ready = True


async def run_loop(broadcast) -> None:
    """Açılışta başlatılır (server.startup_event). broadcast(mesaj): panel soketlerine yayın."""
    while not S.ready:
        try:
            await _init()
        except asyncio.CancelledError:
            raise
        except Exception:
            log.warning("cihaz listesi okunamadı, yeniden denenecek", exc_info=True)
            await asyncio.sleep(5)
    pushed = S.version
    last_scan = time.monotonic()
    while True:
        started = time.monotonic()
        try:
            scan = S.scan_due or started - last_scan >= SCAN_SECONDS
            if scan or S.dirty:
                pcs, S.dirty = S.dirty, set()
                if scan:
                    S.scan_due = False
                    last_scan = started
                try:
                    async with _lock:
                        await _refresh(None if scan else pcs, scan=scan)
                except Exception:
                    S.dirty |= pcs   # bir sonraki turda yeniden denenir
                    raise
            if S.version != pushed:
                pushed = S.version
                await broadcast({"type": "devices_changed", "version": pushed})
        except asyncio.CancelledError:
            raise
        except Exception:
            log.warning("cihaz listesi turu başarısız", exc_info=True)
        # En fazla saniyede bir tur; iş yoksa bir işaret ya da sıradaki tarama beklenir
        await asyncio.sleep(max(0.0, ROUND_SECONDS - (time.monotonic() - started)))
        if not (S.dirty or S.scan_due or S.version != pushed):
            try:
                await asyncio.wait_for(_wake.wait(), max(0.0, last_scan + SCAN_SECONDS - time.monotonic()))
            except asyncio.TimeoutError:
                pass
        _wake.clear()
