"""Ajan güncellemesinin sınıf içi eş önbelleği, sunucu tarafı (docs/design/peer-cache.md, seçenek A).

Bir sınıfa güncelleme gönderilirken önce sınıftan tek bir "tohum" bilgisayara gönderilir. Tohum paketi sunucudan
indirir, doğrular, kurar ve yeni sürümle yeniden bağlandığında paketi C:\\POpsData\\cache'te tutup yerel ağda salt
okunur sunar (ajan sözleşmesi: docs/agent.md, "Peer cache contract"). Tohum başarılı sonucunu (update_result
success) bildirince sınıfın geri kalanına update_agent, "peers" alanıyla gider; onlar paketi önce eşten, olmazsa
sunucudan indirir. Güven her zaman imzalı manifest'teki boyut ve SHA-256'dan gelir; sunucu bir eşi güvenilir kılan
hiçbir şey göndermez.

Neden "verified" değil de sonuç: tohum "verified"tan hemen sonra POpsUpdater'ı başlatır, servis kurulum boyunca
durur. Sınıfın geri kalanı tam o sırada indirmeye başlarsa eş kapalı olurdu. "verified" yalnızca panelde gösterilir
ve tohumun süresini uzatır (kurulum için ikinci bir süre).

Yalnızca X-Agent-Features ile "peer_cache" duyuran, adresi bilinen ve hedef sürümde olmayan bağlı bilgisayar tohum
olabilir. Tohum süresinde (SEED_TIMEOUT) ilerlemezse ya da başarısız olursa sıradaki aday denenir; aday kalmazsa
sınıfın geri kalanına eşsiz (bugünkü gibi) gönderilir. Sınıfı olmayan bilgisayar, özelliği olmayan sınıf ve tek
hedefli sınıf bugünkü gibi gönderilir.

Durum yalnızca bellektedir: sunucu yeniden başlarsa bekleyen bilgisayarlar gönderilmemiş kalır ve bir sonraki
gönderimde yeniden aşamalanır (gönderilenler pending_updates'te izlenmeye devam eder).
"""

import asyncio
import ipaddress
import logging
import os
import re
import time
from dataclasses import dataclass, field
from typing import Dict, Iterable, List, Optional, Set

from pops import update_tracking
from pops.db import execute_query
from pops.labs import UNASSIGNED_LAB
from pops.manager import manager

log = logging.getLogger("pops.peer_cache")

FEATURE = "peer_cache"          # X-Agent-Features'ta ajanın duyurduğu ad (server_info'da sunucu da duyurur)
HEADER = "X-Agent-Peer-Cache"    # isteğe bağlı: "port=8817; ip=10.0.5.12; link=wired"
SETTING = "update_peer_cache"    # global_settings; "1" açık, yoksa kapalı (bilgisayarlarda gelen port açar)
DEFAULT_PORT = 8817
MAX_PEERS = 3
SEED_TRIES = 3                   # bir sınıfta en çok kaç tohum denenir
# Tohumun "verified" bildirmesi için süre; "verified"tan sonra başarılı sonuç için aynı süre yeniden başlar
SEED_TIMEOUT = float(os.environ.get("PEER_CACHE_SEED_TIMEOUT_SECONDS", "600"))
TICK_SECONDS = max(0.5, min(5.0, SEED_TIMEOUT / 4))
# Ajan paketi doğrulamadan sonra 2 saat tutar; sunucu onu 10 dk önce eş olarak önermeyi bırakır
PEER_TTL = 110 * 60
UNASSIGNED = UNASSIGNED_LAB
_SHA256 = re.compile(r"^[0-9a-f]{64}$")


@dataclass
class Announce:
    """Ajanın şu anki bağlantısında X-Agent-Peer-Cache ile bildirdiği (yoksa varsayılan) eş sunucu bilgisi."""

    port: int = DEFAULT_PORT
    ip: Optional[str] = None
    link: Optional[str] = None   # "wired" / "wireless" / None (bilinmiyor)


@dataclass
class Rollout:
    lab: str
    version: str
    sha256: str
    msg: dict                                         # peers'sız update_agent
    candidates: List[str] = field(default_factory=list)
    seed: Optional[str] = None
    seed_stage: Optional[str] = None
    deadline: float = 0.0
    tried: List[str] = field(default_factory=list)   # süresi dolan ya da başarısız tohumlar
    waiting: Set[str] = field(default_factory=set)   # tohumu bekleyenler (henüz gönderilmedi)
    state: str = "seeding"                            # seeding / released / fallback
    ready: Dict[str, float] = field(default_factory=dict)   # paketi tutan bilgisayar -> hazır olduğu an
    addr: Dict[str, str] = field(default_factory=dict)      # başlıkta adres bildirmeyenlerin bilinen adresi
    via_peers: Set[str] = field(default_factory=set)
    without_peers: Set[str] = field(default_factory=set)
    started: float = 0.0
    released_at: Optional[float] = None


_announce: Dict[str, Announce] = {}
_rollouts: Dict[str, Rollout] = {}   # sınıf -> o sınıfın son gönderimi


def parse_header(value: Optional[str]) -> Announce:
    """X-Agent-Peer-Cache: "port=8817; ip=10.0.5.12; link=wired" (hepsi isteğe bağlı, sıra önemsiz). Geçersiz
    parça atılır: port 1024-65535, ip özel ağdaki bir IPv4 adresi, link wired ya da wireless."""
    out = Announce()
    for part in (value or "")[:256].split(";"):
        key, _, val = part.partition("=")
        key, val = key.strip().lower(), val.strip()
        if key == "port" and val.isdigit() and 1024 <= int(val) <= 65535:
            out.port = int(val)
        elif key == "ip":
            out.ip = lan_ip(val)
        elif key == "link" and val.lower() in ("wired", "wireless"):
            out.link = val.lower()
    return out


def lan_ip(value) -> Optional[str]:
    """Eşlerin bağlanabileceği yerel ağ adresi mi: özel aralıkta bir IPv4 (döngü, link-local, çoklu yayın değil)."""
    try:
        ip = ipaddress.ip_address(str(value or "").strip())
    except ValueError:
        return None
    if ip.version != 4 or not ip.is_private or ip.is_loopback or ip.is_link_local or ip.is_multicast \
            or ip.is_unspecified:
        return None
    return str(ip)


def connected(pc: str, features: Iterable[str], header: Optional[str]) -> None:
    """Bağlantı kaydedilirken (routers/agents.py): özelliği duyuran ajanın eş bilgisi bellekte tutulur."""
    if FEATURE in set(features or ()):
        _announce[pc] = parse_header(header)
    else:
        _announce.pop(pc, None)


def disconnected(pc: str) -> None:
    _announce.pop(pc, None)


def peer_url(ip: str, port: int, sha256: str) -> str:
    return "http://%s:%d/pops-cache/%s" % (ip, port, sha256)


def choose_seeds(rows: List[dict], version: str) -> List[str]:
    """Tohum adayları, sırayla: özelliği duyuran, bağlı, adresi bilinen ve hedef sürümde olmayan bilgisayarlar;
    kablolu bağlı olanlar önce, sonra en son görülen, sonra ada göre. rows: pc_name, version, last_seen, ip
    (resolve_ips'in bulduğu adres), has_feature."""
    def ok(r):
        return (r.get("has_feature") and r["pc_name"] in manager.active_agents and r["pc_name"] in _announce
                and r.get("ip") and not update_tracking.same_version(r.get("version"), version))
    picked = [r for r in rows if ok(r)]
    picked.sort(key=lambda r: r["pc_name"])
    picked.sort(key=lambda r: str(r.get("last_seen") or ""), reverse=True)
    picked.sort(key=lambda r: 0 if _announce[r["pc_name"]].link == "wired" else 1)
    return [r["pc_name"] for r in picked]


def resolve_ips(rows: List[dict]) -> None:
    """Her satıra eşlerin kullanacağı adresi ("ip") yazar: ajanın başlıkta bildirdiği, yoksa envanterde bildirdiği
    yerel adres, o da yoksa bağlantının geldiği adres; ama yalnızca özel bir adresse ve başka bir bilgisayarla
    paylaşılmıyorsa (paylaşılıyorsa araya NAT girmiştir). conn_shared: aynı bağlantı adresli kayıtlı bilgisayar
    sayısı (yoksa satırlar arasında sayılır)."""
    shared: Dict[str, int] = {}
    for r in rows:
        if r.get("conn_ip"):
            shared[r["conn_ip"]] = shared.get(r["conn_ip"], 0) + 1
    for r in rows:
        ann = _announce.get(r["pc_name"])
        count = r.get("conn_shared") or shared.get(r.get("conn_ip"), 0)
        conn = lan_ip(r.get("conn_ip")) if count == 1 else None
        r["ip"] = (ann.ip if ann else None) or lan_ip(r.get("inv_ip")) or conn


async def enabled() -> bool:
    rows = await execute_query("SELECT value FROM global_settings WHERE key = $1", (SETTING,), fetch=True)
    # Varsayılan kapalı: açıkken paketi tutan bilgisayar yerel alt ağa bir port açar; yönetici bilerek açar
    return bool(rows and str(rows[0]["value"]) == "1")


async def set_enabled(on: bool) -> None:
    await execute_query(
        "INSERT INTO global_settings (key, value) VALUES ($1, $2) ON CONFLICT (key) DO UPDATE SET value = $2",
        (SETTING, "1" if on else "0"),
    )
    if not on:
        # Kapatılınca tohum bekleyen bilgisayarlar beklemez: bugünkü gibi eşsiz gönderilir
        for ro in list(_rollouts.values()):
            if ro.state == "seeding":
                await _release(ro, "kapatıldı", use_peers=False)


async def _rows(pcs: List[str]) -> List[dict]:
    rows = await execute_query(
        "SELECT c.pc_name, c.lab_name, c.last_seen, c.ip_address AS conn_ip, h.ip_address AS inv_ip, "
        "COALESCE(av.version, c.running_version) AS version, "
        "COALESCE($2 = ANY(av.features), FALSE) AS has_feature, "
        "(SELECT count(*) FROM clients c2 WHERE c2.ip_address = c.ip_address) AS conn_shared "
        "FROM clients c LEFT JOIN agent_versions av ON av.pc_name = c.pc_name "
        "LEFT JOIN hw_inventory h ON h.pc_name = c.pc_name WHERE c.pc_name = ANY($1::text[])",
        (pcs, FEATURE),
        fetch=True,
    )
    out = [dict(r) for r in rows or []]
    resolve_ips(out)
    return out


@dataclass
class Plan:
    hold: Set[str] = field(default_factory=set)            # tohumu bekleyecekler: şimdi gönderilmez
    seeds: Dict[str, str] = field(default_factory=dict)   # tohum -> sınıf
    peers: Dict[str, List[dict]] = field(default_factory=dict)   # şimdi peers ile gönderilecekler


def _live_peers(ro: Rollout, exclude: str = "") -> List[dict]:
    """Paketi tutan, şu an bağlı, özelliği duyuran ve süresi dolmamış eşler (tohum önce, sonra en yeni)."""
    now = time.time()
    order = sorted(ro.ready, key=lambda pc: (pc != ro.seed, -ro.ready[pc]))
    out = []
    for pc in order:
        ann = _announce.get(pc)
        if pc == exclude or now - ro.ready[pc] > PEER_TTL or pc not in manager.active_agents or not ann:
            continue
        ip = ann.ip or ro.addr.get(pc)
        if ip:
            out.append({"hw_id": pc, "url": peer_url(ip, ann.port, ro.sha256)})
        if len(out) == MAX_PEERS:
            break
    return out


def _rotate(peers: List[dict], i: int) -> List[dict]:
    """Eşler bilgisayardan bilgisayara kaydırılır: herkes aynı eşe önce gitmesin."""
    if len(peers) < 2:
        return peers
    k = i % len(peers)
    return peers[k:] + peers[:k]


async def plan(online: Iterable[str], version: str, sha256: str, msg: dict) -> Plan:
    """deploy-update'in gönderim planı. online: gönderilecek bağlı hedefler (yinelenen gönderim süzgecinden geçmiş).
    Sınıfı hazır eşi olan bilgisayar peers ile hemen, tohum bekleyen sınıfın bilgisayarı sonra, tohum hemen; geri
    kalanı (eşsiz) bugünkü gibi gönderilir."""
    out = Plan()
    online = sorted(set(online))
    if not online or not _SHA256.match(sha256 or "") or not await enabled():
        return out
    # Başka sürümün gönderimi eskidi: bekleyenleri gönderilmez (yeni gönderim onları zaten hedefliyorsa alırlar)
    for lab, ro in list(_rollouts.items()):
        if not update_tracking.same_version(ro.version, version):
            log.info("eski sürümün eş gönderimi bırakıldı", extra={"lab": lab, "version": ro.version,
                                                                   "waiting": len(ro.waiting)})
            del _rollouts[lab]
    rows = await _rows(online)
    # Yalnızca özelliği duyuran ajan beklemeye alınır ve peers alır; diğerleri (eski ajan, sınıfsız) bugünkü gibi
    labs: Dict[str, List[dict]] = {}
    for r in rows:
        lab = r.get("lab_name")
        if lab and lab != UNASSIGNED and r.get("has_feature") and r["pc_name"] in _announce:
            labs.setdefault(lab, []).append(r)
    now = time.time()
    for lab, lab_rows in sorted(labs.items()):
        pcs = sorted(r["pc_name"] for r in lab_rows)
        ro = _rollouts.get(lab)
        if ro and ro.state == "seeding":
            # Aynı sürüm bu sınıfta zaten aşamalanıyor: yeni hedefler de tohumu bekler
            ro.waiting.update(pc for pc in pcs if pc != ro.seed)
            out.hold.update(pc for pc in pcs if pc != ro.seed)
            continue
        peers = _live_peers(ro) if ro else []
        if peers:
            for i, pc in enumerate(pc for pc in pcs if pc not in ro.ready):
                own = [p for p in peers if p["hw_id"] != pc]
                if own:
                    out.peers[pc] = _rotate(own, i)
            continue
        if len(pcs) < 2:
            continue
        candidates = choose_seeds(lab_rows, version)
        if not candidates:
            continue
        seed = candidates.pop(0)
        ro = Rollout(lab=lab, version=version, sha256=sha256, msg=msg, candidates=candidates, seed=seed,
                     deadline=now + SEED_TIMEOUT, waiting={pc for pc in pcs if pc != seed}, started=now,
                     addr={r["pc_name"]: r["ip"] for r in lab_rows if r.get("ip")})
        _rollouts[lab] = ro
        out.seeds[seed] = lab
        out.hold.update(ro.waiting)
    return out


def is_waiting(pc: str) -> bool:
    return any(pc in ro.waiting for ro in _rollouts.values())


def _rollout_of_seed(pc: str) -> Optional[Rollout]:
    for ro in _rollouts.values():
        if ro.state == "seeding" and (ro.seed == pc or pc in ro.tried):
            return ro
    return None


async def not_sent(pc: str) -> None:
    """deploy-update tohuma gönderemedi (bağlantı koptu): sıradaki aday."""
    ro = _rollout_of_seed(pc)
    if ro and ro.seed == pc:
        await _next_seed(ro, "gönderilemedi")


async def _send(pc: str, message: dict, version: str) -> bool:
    if not await manager.send_command(message, pc):
        return False
    await update_tracking.mark_sent(pc, version)
    return True


async def _next_seed(ro: Rollout, why: str) -> None:
    # Süre hemen ertelenir: aşağıdaki beklemeler sürerken tick aynı tohumu ikinci kez bırakmasın
    ro.deadline = time.time() + SEED_TIMEOUT
    if ro.seed:
        ro.tried.append(ro.seed)
        log.info("tohum bırakıldı", extra={"lab": ro.lab, "pc_name": ro.seed, "reason": why})
    ro.seed, ro.seed_stage = None, None
    while ro.candidates and len(ro.tried) < SEED_TRIES:
        pc = ro.candidates.pop(0)
        if pc not in manager.active_agents or pc not in _announce:
            continue
        if await update_tracking.recently_sent([pc], ro.version):
            continue
        # Bekleyenlerden biri tohum olur
        ro.waiting.discard(pc)
        ro.seed, ro.deadline = pc, time.time() + SEED_TIMEOUT
        if await _send(pc, ro.msg, ro.version):
            log.info("yeni tohum", extra={"lab": ro.lab, "pc_name": pc})
            return
        ro.tried.append(pc)
        ro.seed = None
    await _release(ro, "tohum kalmadı")


async def _release(ro: Rollout, why: str, use_peers: bool = True) -> None:
    """Bekleyenlere gönderir: hazır eş varsa peers ile, yoksa bugünkü gibi."""
    if ro.state != "seeding":
        return
    peers = _live_peers(ro) if use_peers else []
    ro.state = "released" if peers else "fallback"
    ro.released_at = time.time()
    waiting = sorted(ro.waiting)
    ro.waiting = set()
    log.info("tohum bekleyenler gönderiliyor",
             extra={"lab": ro.lab, "seed": ro.seed, "peers": len(peers), "count": len(waiting), "reason": why})
    already = await update_tracking.recently_sent(waiting, ro.version)
    i = 0
    for pc in waiting:
        if pc in already or pc not in manager.active_agents:
            continue
        own = [p for p in peers if p["hw_id"] != pc]
        message = dict(ro.msg, peers=_rotate(own, i)) if own else ro.msg
        if await _send(pc, message, ro.version):
            (ro.via_peers if own else ro.without_peers).add(pc)
            i += 1


async def on_progress(pc: str, progress: dict) -> None:
    """Tohumun adımı (update_progress, kabul edilmiş): "verified" paketi doğruladı demektir; süresi kurulum için
    yeniden başlar."""
    ro = _rollout_of_seed(pc)
    if not ro or ro.seed != pc:
        return
    ro.seed_stage = progress.get("stage")
    if ro.seed_stage == "verified":
        ro.deadline = time.time() + SEED_TIMEOUT


async def on_result(pc: str, payload: dict) -> None:
    """Güncelleme sonucu (update_result ya da "rejected" adımı). Başarılıysa bilgisayar paketi tutan bir eştir;
    tohumsa sınıfın geri kalanı gönderilir. Tohumun başarısızlığı sıradaki adayı dener."""
    status = str(payload.get("status") or "")
    to = payload.get("running_version") or payload.get("to_version")
    mine = [ro for ro in _rollouts.values() if update_tracking.same_version(to, ro.version)]
    if status == "success" and mine and pc in _announce:
        rows = await _rows([pc])
        for ro in mine:
            ro.ready[pc] = time.time()
            if rows and rows[0].get("ip"):
                ro.addr[pc] = rows[0]["ip"]
    ro = _rollout_of_seed(pc)
    if not ro:
        return
    if status == "success" and pc in ro.ready:
        # Süresi dolmuş eski bir tohum da geç kalsa hazır eştir; o sırada denenen tohum kendi kurulumunu sürdürür
        if ro.seed and ro.seed != pc:
            ro.tried.append(ro.seed)
        if pc in ro.tried:
            ro.tried.remove(pc)
        ro.seed, ro.seed_stage = pc, "ready"
        await _release(ro, "tohum hazır")
    elif ro.seed == pc:
        await _next_seed(ro, "sonuç: %s" % (status or "?"))


async def tick() -> None:
    now = time.time()
    for lab, ro in list(_rollouts.items()):
        if ro.state == "seeding" and now > ro.deadline:
            await _next_seed(ro, "süre doldu (%s)" % (ro.seed_stage or "adım yok"))
        elif ro.state != "seeding" and now - (ro.released_at or now) > PEER_TTL:
            _rollouts.pop(lab, None)


async def loop() -> None:
    while True:
        await asyncio.sleep(TICK_SECONDS)
        try:
            await tick()
        except asyncio.CancelledError:
            raise
        except Exception:
            log.warning("eş gönderimi denetlenemedi", exc_info=True)


def status_for(pcs: Iterable[str]) -> dict:
    """update-progress için: bilgisayar başına rol ve sınıf başına özet (panel)."""
    pcs = set(pcs)
    items, labs = {}, []
    for lab, ro in sorted(_rollouts.items()):
        members = {ro.seed} | ro.waiting | ro.via_peers | ro.without_peers | set(ro.tried)
        if not members & pcs:
            continue
        labs.append({
            "lab": lab,
            "version": ro.version,
            "state": ro.state,
            "seed": ro.seed,
            "seed_stage": ro.seed_stage,
            "seed_online": bool(ro.seed and ro.seed in manager.active_agents),
            "tried": list(ro.tried),
            "waiting": len(ro.waiting),
            "via_peers": len(ro.via_peers),
            "without_peers": len(ro.without_peers),
            "peers": len(_live_peers(ro)),
        })
        for pc in pcs & members:
            if pc == ro.seed:
                role = "seed"
            elif pc in ro.waiting:
                role = "waiting"
            elif pc in ro.via_peers:
                role = "via_peers"
            elif pc in ro.without_peers:
                role = "without_peers"
            else:
                role = "former_seed"
            items[pc] = {"lab": lab, "role": role}
    return {"items": items, "labs": labs}


def reset() -> None:
    """Testler için."""
    _announce.clear()
    _rollouts.clear()
