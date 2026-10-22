"""Sınıf içi eş önbelleği (peer_cache), sunucu tarafı — entegrasyon testi (CI 'security' job'ı).

Simüle ajanlarla iki sınıf ve sınıfsız bilgisayarlar:
- Özelliği (X-Agent-Features: peer_cache) duyuran sınıfta update_agent önce tek tohuma gider; kablolu tohum önce
  seçilir; adresi bilinmeyen tohum olamaz; geri kalan tohumu bekler.
- Özelliği olmayan ajan, sınıfı olmayan bilgisayar ve özelliği olmayan sınıf bugünkü gibi hemen, peers'sız alır.
- Tohum süresinde ilerlemezse sıradaki aday denenir; "verified" panelde görünür; tohum yeni sürümle bağlanıp başarılı
  sonucu bildirince bekleyenlere "peers" ile gider (adres, port ve SHA-256'lı yol doğru).
- Sonradan hazır olan eşler sonraki gönderimde listede (en çok 3, kayarak).
- Tohum reddederse hemen sıradaki; aday kalmazsa bekleyenlere peers'sız gider.
- Ayar kapalıyken bugünkü gibi; kapatılınca tohum bekleyenler hemen gönderilir.

Sunucu PEER_CACHE_SEED_TIMEOUT_SECONDS kısa (≤ 10 sn) ile başlatılmış olmalı (CI ve run_local.sh 4 verir).
Ortam: POPS_TEST_HTTP + DB_* + JWT_SECRET.
"""

import asyncio
import datetime
import hashlib
import json
import os
import sys
import time
import urllib.error
import urllib.request

sys.path.insert(0, os.path.join(os.path.dirname(__file__), os.pardir))
import asyncpg  # noqa: E402
import websockets  # noqa: E402

import server  # noqa: E402  (create_jwt)
import system_routes  # noqa: E402  (RELEASES_DIR)
from pops.config import UPDATES_DIR  # noqa: E402

HTTP = os.environ["POPS_TEST_HTTP"]
WS = os.environ.get("POPS_TEST_WS", HTTP.replace("http://", "ws://").replace("https://", "wss://"))
TIMEOUT = float(os.environ.get("PEER_CACHE_SEED_TIMEOUT_SECONDS", "4"))
VER = "9.9.3-peertest"
SHA = hashlib.sha256(b"peer-cache-test").hexdigest()
OLD = "0.1.23-alpha"
LAB_A, LAB_B, LAB_C, LAB_D = "PEER-LAB-A", "PEER-LAB-B", "PEER-LAB-C", "PEER-LAB-D"
FEAT = {"X-Agent-Features": "winget,peer_cache"}
# ad -> (sınıf, ek başlıklar, envanterdeki adres)
AGENTS = {
    "HW-PCA1": (LAB_A, dict(FEAT, **{"X-Agent-Peer-Cache": "port=8818; ip=10.20.0.11; link=wired"}), None),
    "HW-PCA2": (LAB_A, FEAT, "10.20.0.12"),
    "HW-PCA3": (LAB_A, FEAT, None),                       # adresi yok: tohum olamaz
    "HW-PCA4": (LAB_A, {}, "10.20.0.14"),                 # eski ajan: özellik yok
    "HW-PCA5": (LAB_A, dict(FEAT, **{"X-Agent-Peer-Cache": "ip=10.20.0.15"}), None),
    "HW-PCB1": (LAB_B, {"X-Agent-Features": "winget"}, "10.30.0.1"),
    "HW-PCB2": (LAB_B, {}, "10.30.0.2"),
    "HW-PCN1": (None, FEAT, "10.40.0.1"),                 # sınıfsız
    "HW-PCC1": (LAB_C, dict(FEAT, **{"X-Agent-Peer-Cache": "ip=10.50.0.1; link=wired"}), None),
    "HW-PCC2": (LAB_C, FEAT, "10.50.0.2"),
    "HW-PCC3": (LAB_C, FEAT, None),
    "HW-PCD1": (LAB_D, FEAT, "10.60.0.1"),
    "HW-PCD2": (LAB_D, FEAT, "10.60.0.2"),
    "HW-PCD3": (LAB_D, FEAT, "10.60.0.3"),
}
LATE = "HW-PCA6"   # sonradan bağlanan, A sınıfından
PCS = list(AGENTS) + [LATE]
FAILS = []


def chk(cond, msg):
    print(("  OK   " if cond else "  FAIL ") + msg)
    if not cond:
        FAILS.append(msg)


def _sha(s):
    return hashlib.sha256(s.encode()).hexdigest()


def req(path, token=None, body=None):
    data = json.dumps(body).encode() if body is not None else None
    r = urllib.request.Request(HTTP + path, data=data, method="POST" if body is not None else "GET")
    if body is not None:
        r.add_header("Content-Type", "application/json")
    if token:
        r.add_header("Authorization", "Bearer " + token)
    try:
        with urllib.request.urlopen(r, timeout=40) as resp:
            return resp.status, json.loads(resp.read() or b"{}")
    except urllib.error.HTTPError as e:
        return e.code, {}


def dna(pc):
    return json.dumps({
        "hw_id": pc,
        "dna_payload": {"hardware": {"uuid": pc + "-U", "bios_sn": pc + "-B", "disk_sn": pc + "-D", "mac": "-",
                                     "ram_sn": "-"}, "capabilities": {"ram_readable": True}},
        "hostname": pc.lower(),
        "status": "Online",
    })


async def connect(pc, extra, version=OLD):
    headers = dict({"X-Agent-Secret": pc + "-s", "X-Agent-Version": version}, **extra)
    ws = await websockets.connect("%s/ws/agent/%s" % (WS, pc), additional_headers=headers)
    await ws.send(dna(pc))
    return ws


async def collect(ws, seconds):
    out = []
    loop = asyncio.get_event_loop()
    end = loop.time() + seconds
    while loop.time() < end:
        try:
            raw = await asyncio.wait_for(ws.recv(), timeout=max(0.05, end - loop.time()))
        except asyncio.TimeoutError:
            break
        except websockets.exceptions.ConnectionClosed:
            break
        try:
            out.append(json.loads(raw))
        except ValueError:
            pass
    return out


def updates(msgs):
    return [m for m in msgs if isinstance(m, dict) and m.get("action") == "update_agent"]


async def inbox(socks, seconds=1.2):
    """Her ajanın aldığı update_agent'ler (aynı anda okunur)."""
    names = list(socks)
    got = await asyncio.gather(*[collect(socks[n], seconds) for n in names])
    return {n: updates(m) for n, m in zip(names, got)}


def url(ip, port=8817):
    return "http://%s:%d/pops-cache/%s" % (ip, port, SHA)


async def conn():
    return await asyncpg.connect(host=os.environ.get("DB_HOST", "localhost"),
                                 port=int(os.environ.get("DB_PORT", "5432")), user=os.environ["DB_USER"],
                                 password=os.environ["DB_PASS"], database=os.environ["DB_NAME"])


async def cleanup(c):
    for table, col in (("agent_secrets", "pc_name"), ("clients", "pc_name"), ("agent_versions", "pc_name"),
                       ("hw_inventory", "pc_name"), ("pending_updates", "pc_name"), ("update_results", "pc_name"),
                       ("notifications", "pc_name")):
        await c.execute("DELETE FROM %s WHERE %s = ANY($1::text[])" % (table, col), PCS)


async def stage_release(c):
    msi = "POps-Agent-%s-win-x64.msi" % VER
    manifest = {"version": VER, "tag": "v" + VER, "artifacts": [{"name": msi, "sha256": SHA, "size": 15}]}
    rel = os.path.join(system_routes.RELEASES_DIR, VER)
    os.makedirs(rel, exist_ok=True)
    with open(os.path.join(rel, "manifest.json"), "w", encoding="utf-8") as f:
        json.dump(manifest, f)
    with open(os.path.join(rel, "manifest.json.sig"), "w", encoding="utf-8") as f:
        f.write("peer-test-sig")
    with open(os.path.join(rel, msi), "wb") as f:
        f.write(b"peer-cache-test")
    old = await c.fetchval("SELECT value FROM global_settings WHERE key='verified_release_manifest'")
    await c.execute("INSERT INTO global_settings (key, value) VALUES ('verified_release_manifest', $1) "
                    "ON CONFLICT (key) DO UPDATE SET value = $1", json.dumps(manifest))
    return old, rel, os.path.join(UPDATES_DIR, msi)


async def unstage_release(c, saved):
    old, rel, copied = saved
    if old is None:
        await c.execute("DELETE FROM global_settings WHERE key='verified_release_manifest'")
    else:
        await c.execute("UPDATE global_settings SET value=$1 WHERE key='verified_release_manifest'", old)
    for name in os.listdir(rel):
        os.remove(os.path.join(rel, name))
    os.rmdir(rel)
    if os.path.exists(copied):
        os.remove(copied)


def deploy(token, pcs):
    return req("/api/system/deploy-update", token, {"target_mode": "PC", "targets": pcs})


def progress(token, pcs):
    s, p = req("/api/system/update-progress", token, {"pcs": pcs, "version": VER, "since": time.time() - 600})
    return p if s == 200 else {}


def lab_of(p, lab):
    return next((x for x in p.get("peer_labs") or [] if x["lab"] == lab), {})


async def wait_lab(token, pcs, lab, cond, timeout):
    info = {}
    for _ in range(int(timeout / 0.25)):
        info = lab_of(progress(token, pcs), lab)
        if cond(info):
            return info
        await asyncio.sleep(0.25)
    return info


async def succeed(c, socks, pc, extra, result_id):
    """Ajan güncellemeyi kurup yeni sürümle yeniden bağlanır ve başarılı sonucu bildirir."""
    await socks[pc].close()
    socks[pc] = await connect(pc, extra, version=VER)
    await collect(socks[pc], 0.8)
    await socks[pc].send(json.dumps({"type": "update_result", "status": "success", "from_version": OLD,
                                     "to_version": VER, "running_version": VER, "result_id": result_id}))


async def main():
    if TIMEOUT > 10:
        print("PEER_CACHE_SEED_TIMEOUT_SECONDS sunucuda ve testte 10 sn'den kısa olmalı (CI: 4)")
        sys.exit(1)
    c = await conn()
    await cleanup(c)
    old_setting = await c.fetchval("SELECT value FROM global_settings WHERE key='update_peer_cache'")
    await c.execute("DELETE FROM global_settings WHERE key='update_peer_cache'")
    for pc in PCS:
        await c.execute("INSERT INTO agent_secrets (pc_name, secret_hash) VALUES ($1,$2)", pc, _sha(pc + "-s"))
    for user, role in (("peersuper", "superadmin"), ("peeradmin", "admin")):
        await c.execute("INSERT INTO users (username,password_hash,role,permissions,token_version) "
                        "VALUES ($1,'x',$2,'[]',0) ON CONFLICT (username) DO UPDATE SET role=$2, token_version=0",
                        user, role)
    sup = server.create_jwt("peersuper", "superadmin", 0)
    saved = await stage_release(c)
    socks = {}
    try:
        await run(c, sup, socks)
    finally:
        for ws in socks.values():
            await ws.close()
        await unstage_release(c, saved)
        await cleanup(c)
        if old_setting is None:
            await c.execute("DELETE FROM global_settings WHERE key='update_peer_cache'")
        else:
            await c.execute("UPDATE global_settings SET value=$1 WHERE key='update_peer_cache'", old_setting)
        await c.execute("DELETE FROM users WHERE username = ANY($1::text[])", ["peersuper", "peeradmin"])
        await c.close()
    if FAILS:
        print("\nBASARISIZ: %d kontrol" % len(FAILS))
        sys.exit(1)
    print("\nTUM PEER CACHE TESTLERI GECTI")


async def run(c, sup, socks):
    print("== bağlantı: X-Agent-Features ve X-Agent-Peer-Cache")
    for pc, (_lab, extra, _ip) in AGENTS.items():
        socks[pc] = await connect(pc, extra)
    first = await asyncio.gather(*[collect(ws, 1.5) for ws in socks.values()])
    info = next((m for m in first[0] if m.get("action") == "server_info"), {})
    chk("peer_cache" in info.get("features", []), "server_info peer_cache'i duyurur")
    for pc, (lab, _extra, ip) in AGENTS.items():
        await c.execute("UPDATE clients SET lab_name=$2 WHERE pc_name=$1", pc, lab or "Atanmamis_Cihazlar")
        if ip:
            await c.execute("INSERT INTO hw_inventory (pc_name, cpu, ip_address) VALUES ($1, 'x', $2)", pc, ip)
    # "En son görülen" önce: ilk heartbeat'ler yazıldıktan sonra (toplu yazma 2 sn) sıra sabitlenir
    await asyncio.sleep(1.5)
    late = datetime.datetime(2099, 1, 1, tzinfo=datetime.timezone.utc)
    for pc, sec in (("HW-PCA2", 2), ("HW-PCA5", 1), ("HW-PCD1", 2), ("HW-PCD2", 1)):
        await c.execute("UPDATE clients SET last_seen=$2 WHERE pc_name=$1", pc, late + datetime.timedelta(seconds=sec))
    s, v = req("/api/system/version", sup)
    chk(s == 200 and v.get("update_peer_cache") is False, "ayar varsayılan kapalı (bilgisayarlarda port açar)")
    s, r = req("/api/system/update-peer-cache", sup, {"enabled": True})
    chk(s == 200 and r.get("update_peer_cache") is True, "süper yönetici açtı")

    print("== aşamalı gönderim: tohum önce, geri kalan bekler")
    a_lab = [pc for pc in AGENTS if AGENTS[pc][0] == LAB_A]
    targets = list(AGENTS)
    targets.remove("HW-PCC1"), targets.remove("HW-PCC2"), targets.remove("HW-PCC3")
    targets = [t for t in targets if not t.startswith("HW-PCD")]
    s, d = deploy(sup, targets)
    chk(s == 200 and d.get("seeds") == ["HW-PCA1"], "A sınıfının tohumu kablolu PCA1 (%s)" % d.get("seeds"))
    chk(d.get("waiting_for_seed") == ["HW-PCA2", "HW-PCA3", "HW-PCA5"], "özellikli üç bilgisayar bekliyor (%s)"
        % d.get("waiting_for_seed"))
    chk(sorted(d.get("dispatched") or []) == ["HW-PCA1", "HW-PCA4", "HW-PCB1", "HW-PCB2", "HW-PCN1"],
        "tohum, eski ajan, özelliksiz sınıf ve sınıfsız hemen gönderildi (%s)" % d.get("dispatched"))
    got = await inbox(socks)
    chk(len(got["HW-PCA1"]) == 1 and "peers" not in got["HW-PCA1"][0], "tohum peers'sız update_agent aldı")
    chk(got["HW-PCA1"][0].get("peer_cache") is True, "tohuma peer_cache: true (önbellek tutar)")
    chk(all(len(got[pc]) == 1 and "peers" not in got[pc][0] for pc in ("HW-PCA4", "HW-PCB1", "HW-PCB2", "HW-PCN1")),
        "özelliksiz ve sınıfsız bilgisayarlar bugünkü gibi aldı")
    chk(not any(got[pc] for pc in ("HW-PCA2", "HW-PCA3", "HW-PCA5")), "bekleyenlere henüz gitmedi")
    p = progress(sup, a_lab)
    roles = {i["pc"]: (i.get("peer") or {}).get("role") for i in p.get("items") or []}
    chk(roles.get("HW-PCA1") == "seed" and roles.get("HW-PCA2") == "waiting" and roles.get("HW-PCA4") is None,
        "update-progress rolleri döndürür (%s)" % roles)
    a = lab_of(p, LAB_A)
    chk(a.get("state") == "seeding" and a.get("seed") == "HW-PCA1" and a.get("waiting") == 3,
        "sınıf özeti: tohumlanıyor (%s)" % a)
    chk(not lab_of(p, LAB_B), "özelliksiz sınıfın özeti yok")

    print("== aynı sürüm yeniden gönderilince bekleyenler beklemeye devam eder")
    s, d = deploy(sup, a_lab)
    chk(s == 200 and d.get("dispatched") == [] and "HW-PCA1" in d.get("already_pending", [])
        and d.get("waiting_for_seed") == ["HW-PCA2", "HW-PCA3", "HW-PCA5"], "ikinci gönderim de bekler (%s)" % d)
    chk(not any((await inbox({pc: socks[pc] for pc in ("HW-PCA2", "HW-PCA3", "HW-PCA5")}, 0.8)).values()),
        "ikinci gönderim bekleyenlere gitmedi")

    print("== tohum süresinde ilerlemezse sıradaki aday")
    a = await wait_lab(sup, a_lab, LAB_A, lambda x: x.get("seed") == "HW-PCA2", TIMEOUT + 6)
    chk(a.get("seed") == "HW-PCA2" and a.get("tried") == ["HW-PCA1"] and a.get("state") == "seeding",
        "süre doldu, envanter adresli PCA2 tohum (%s)" % a)
    got = await inbox(socks, 1)
    chk(len(got["HW-PCA2"]) == 1 and "peers" not in got["HW-PCA2"][0], "yeni tohum update_agent aldı")
    chk(not got["HW-PCA3"] and not got["HW-PCA5"], "geri kalanı hâlâ bekliyor")
    await socks["HW-PCA2"].send(json.dumps({"type": "update_progress", "stage": "verified", "to_version": VER}))
    a = await wait_lab(sup, a_lab, LAB_A, lambda x: x.get("seed_stage") == "verified", 4)
    chk(a.get("seed_stage") == "verified" and a.get("state") == "seeding", "verified panelde, gönderim bekliyor")
    await asyncio.sleep(TIMEOUT * 0.75)
    a = lab_of(progress(sup, a_lab), LAB_A)
    chk(a.get("seed") == "HW-PCA2", "verified tohumun süresini yeniden başlattı (%s)" % a.get("seed"))

    print("== tohum yeni sürümle bağlanıp başarılı sonucu bildirince bekleyenler peers ile alır")
    await succeed(c, socks, "HW-PCA2", FEAT, "a2a2a2a2a2a2a2a2a2a2a2a2a2a2a2a2")
    got = await inbox(socks, 1.5)
    exp = [{"hw_id": "HW-PCA2", "url": url("10.20.0.12")}]
    chk(got["HW-PCA3"] and got["HW-PCA3"][0].get("peers") == exp, "PCA3 peers aldı: %s"
        % (got["HW-PCA3"][0].get("peers") if got["HW-PCA3"] else None))
    chk(got["HW-PCA5"] and got["HW-PCA5"][0].get("peers") == exp, "PCA5 peers aldı")
    chk(not got["HW-PCA4"] and not got["HW-PCA1"], "eski ajana ve eski tohuma yeniden gitmedi")
    m = got["HW-PCA3"][0] if got["HW-PCA3"] else {}
    chk(set(m) == {"action", "manifest", "manifest_sig", "peers", "peer_cache"},
        "update_agent alanları: %s" % sorted(m))
    a = lab_of(progress(sup, a_lab), LAB_A)
    chk(a.get("state") == "released" and a.get("via_peers") == 2 and a.get("waiting") == 0 and a.get("peers") == 1,
        "sınıf özeti: eşten dağıtılıyor (%s)" % a)
    n = await c.fetchval("SELECT count(*) FROM pending_updates WHERE pc_name = ANY($1::text[])",
                         ["HW-PCA3", "HW-PCA5"])
    chk(n == 2, "bekleyenlerin gönderimi izleniyor (pending_updates)")

    print("== geç kalan tohum ve sonradan bağlanan bilgisayar")
    await succeed(c, socks, "HW-PCA1", AGENTS["HW-PCA1"][1], "a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1")
    await collect(socks["HW-PCA1"], 0.8)
    await succeed(c, socks, "HW-PCA3", FEAT, "a3a3a3a3a3a3a3a3a3a3a3a3a3a3a3a3")   # adresi yok: eş olamaz
    await collect(socks["HW-PCA3"], 0.8)
    socks[LATE] = await connect(LATE, dict(FEAT, **{"X-Agent-Peer-Cache": "ip=10.20.0.16"}))
    await collect(socks[LATE], 1)
    await c.execute("UPDATE clients SET lab_name=$2 WHERE pc_name=$1", LATE, LAB_A)
    s, d = deploy(sup, [LATE])
    got = await inbox({LATE: socks[LATE]}, 1.2)
    peers = got[LATE][0].get("peers") if got[LATE] else None
    chk(s == 200 and d.get("with_peers") == [LATE] and d.get("waiting_for_seed") == []
        and peers == [{"hw_id": "HW-PCA2", "url": url("10.20.0.12")},
                      {"hw_id": "HW-PCA1", "url": url("10.20.0.11", 8818)}],
        "hazır eşler hemen: tohum önce, ajanın bildirdiği port; adressiz eş yok (%s)" % peers)

    print("== tohum reddederse sıradaki; aday kalmazsa peers'sız")
    c_lab = [pc for pc in AGENTS if AGENTS[pc][0] == LAB_C]
    s, d = deploy(sup, c_lab)
    chk(d.get("seeds") == ["HW-PCC1"] and d.get("waiting_for_seed") == ["HW-PCC2", "HW-PCC3"], "C tohumu PCC1")
    await collect(socks["HW-PCC1"], 0.8)
    await socks["HW-PCC1"].send(json.dumps({"type": "update_progress", "stage": "rejected", "to_version": VER,
                                            "detail": "manifest imzası geçersiz"}))
    got = await inbox({pc: socks[pc] for pc in ("HW-PCC2", "HW-PCC3")}, 1.5)
    chk(len(got["HW-PCC2"]) == 1 and not got["HW-PCC3"], "ret: PCC2 hemen tohum oldu")
    # Durum gönderimlerden önce "fallback" olur: gönderim de bitene kadar beklenir (yüklü CI'da yarış olmasın)
    c_info = await wait_lab(sup, c_lab, LAB_C,
                            lambda x: x.get("state") == "fallback" and x.get("without_peers") == 1, TIMEOUT + 6)
    chk(c_info.get("state") == "fallback" and c_info.get("without_peers") == 1 and c_info.get("tried")
        == ["HW-PCC1", "HW-PCC2"], "tohum kalmadı: eşsiz gönderildi (%s)" % c_info)
    got = await inbox({"HW-PCC3": socks["HW-PCC3"]}, 1)
    chk(len(got["HW-PCC3"]) == 1 and "peers" not in got["HW-PCC3"][0], "PCC3 bugünkü gibi aldı")

    print("== ayar kapalı: bugünkü gibi; kapatılınca bekleyenler hemen")
    d_lab = [pc for pc in AGENTS if AGENTS[pc][0] == LAB_D]
    s, d = deploy(sup, d_lab[:2])
    chk(d.get("seeds") == ["HW-PCD1"] and d.get("waiting_for_seed") == ["HW-PCD2"], "D tohumlanıyor")
    await inbox({pc: socks[pc] for pc in d_lab}, 0.6)
    s, r = req("/api/system/update-peer-cache", sup, {"enabled": False})
    chk(s == 200 and r.get("update_peer_cache") is False, "ayar kapatıldı")
    got = await inbox({pc: socks[pc] for pc in d_lab}, 1.2)
    chk(len(got["HW-PCD2"]) == 1 and "peers" not in got["HW-PCD2"][0] and not got["HW-PCD1"],
        "kapatılınca tohum bekleyen hemen ve peers'sız aldı")
    chk("peer_cache" not in got["HW-PCD2"][0], "kapatılınca bekleyene peer_cache gitmedi")
    chk(req("/api/system/version", sup)[1].get("update_peer_cache") is False, "sürüm ucunda kapalı")
    s, d = deploy(sup, d_lab)
    chk(s == 200 and d.get("dispatched") == ["HW-PCD3"] and d.get("seeds") == [] and d.get("waiting_for_seed") == []
        and d.get("with_peers") == [], "kapalıyken bugünkü gibi (%s)" % d)
    got = await inbox({"HW-PCD3": socks["HW-PCD3"]}, 1)
    chk(len(got["HW-PCD3"]) == 1 and "peers" not in got["HW-PCD3"][0], "peers yok")
    chk("peer_cache" not in got["HW-PCD3"][0], "ayar kapalıyken peer_cache yok: ajan port açmaz")
    admin_status = req("/api/system/update-peer-cache", server.create_jwt("peeradmin", "admin", 0),
                       {"enabled": True})[0]
    chk(admin_status == 403, "ayarı yalnız süper admin değiştirir (%s)" % admin_status)
    audit = await c.fetchval("SELECT count(*) FROM device_audit_logs WHERE action='update_peer_cache'")
    chk(audit >= 1, "ayar değişikliği denetim kaydında")


if __name__ == "__main__":
    asyncio.run(main())
