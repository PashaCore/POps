"""Modüller ve kurulum profilleri — entegrasyon testi (CI 'security' job'ı).

- Yalnız superadmin değiştirir; her değişiklik denetim kaydına yazılır, kurum ayarı profili "özel" yapar.
- Kapalı modülün ucu 409 (X-POps-Module); laboratuvar istisnası kurum ayarını ezer, cihaz kendi laboratuvarıyla
  denetlenir; bağımlı modül bağımlılığıyla kapanır.
- Uzak komut kapatılınca bekleyen görevler reddedilir; kuyruk kapalı laboratuvara görev göndermez; görev
  oluşturma kapalı laboratuvardaki hedefleri atlar.
- Zamanlayıcı kapalı laboratuvardaki hedefleri atlar; ajan politikası DNS listesini kapalıysa boş verir; kapalı
  laboratuvardan gelen DNS uyarısı ve yazılım listesi saklanmaz; uyandırma kapalı laboratuvarda reddedilir.
- Profil önizlemesi ve uygulanması.

Bazı adımlar sunucunun modüllerini bu süreçte, aynı veritabanına bağlanarak çağırır (zamanlayıcı).
Ortam: POPS_TEST_HTTP + DB_* + JWT_SECRET.
"""

import asyncio
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
from pops import db, modules, scheduler  # noqa: E402

HTTP = os.environ["POPS_TEST_HTTP"]
WS = os.environ.get("POPS_TEST_WS", HTTP.replace("http://", "ws://").replace("https://", "wss://"))
PCS = {"HW-MD1": "MD-Lab1", "HW-MD2": "MD-Lab2"}
FAILS = []


def chk(cond, msg):
    print(("  OK   " if cond else "  FAIL ") + msg)
    if not cond:
        FAILS.append(msg)


def _sha(s):
    return hashlib.sha256(s.encode()).hexdigest()


def req(path, token=None, body=None, method=None, headers=None):
    """(durum, gövde, başlıklar)"""
    data = json.dumps(body).encode() if body is not None else None
    r = urllib.request.Request(HTTP + path, data=data, method=method or ("POST" if body is not None else "GET"))
    if body is not None:
        r.add_header("Content-Type", "application/json")
    for k, v in (headers or {}).items():
        r.add_header(k, v)
    if token:
        r.add_header("Authorization", "Bearer " + token)
    try:
        with urllib.request.urlopen(r, timeout=40) as resp:
            return resp.status, json.loads(resp.read() or b"{}"), resp.headers
    except urllib.error.HTTPError as e:
        try:
            payload = json.loads(e.read() or b"{}")
        except ValueError:
            payload = {}
        return e.code, payload, e.headers


def closed(result, module_id):
    status, body, headers = result
    return status == 409 and headers.get("X-POps-Module") == module_id and "kapalı" in str(body.get("detail"))


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


async def conn():
    return await asyncpg.connect(
        host=os.environ.get("DB_HOST", "localhost"),
        port=int(os.environ.get("DB_PORT", "5432")),
        user=os.environ["DB_USER"],
        password=os.environ["DB_PASS"],
        database=os.environ["DB_NAME"],
    )


async def cleanup(c):
    names = list(PCS)
    for table, col in (("agent_secrets", "pc_name"), ("clients", "pc_name"), ("tasks", "target_pc"),
                       ("device_software", "pc_name"), ("hw_inventory", "pc_name")):
        await c.execute("DELETE FROM %s WHERE %s = ANY($1::text[])" % (table, col), names)
    await c.execute("DELETE FROM scheduled_tasks WHERE name LIKE 'md-test-%'")
    await c.execute("DELETE FROM module_settings")
    await c.execute("DELETE FROM global_settings WHERE key = 'install_profile'")


async def main():
    c = await conn()
    old_enforce = await c.fetchval("SELECT value FROM global_settings WHERE key='enforce_agent_auth'")
    old_policies = await c.fetchval("SELECT value FROM global_settings WHERE key='agent_policies'")
    old_limit = await c.fetchval("SELECT value FROM global_settings WHERE key='concurrent_limit'")
    await cleanup(c)
    for pc, lab in PCS.items():
        await c.execute("INSERT INTO clients (pc_name, hostname, lab_name, status) VALUES ($1, $2, $3, 'Offline')",
                        pc, pc.lower(), lab)
        await c.execute("INSERT INTO agent_secrets (pc_name, secret_hash) VALUES ($1, $2)", pc, _sha(pc + "-s"))
        await c.execute("INSERT INTO hw_inventory (pc_name, mac_address) VALUES ($1, $2) ON CONFLICT DO NOTHING",
                        pc, "00:11:22:33:44:%02d" % len(pc))
    for u, role in (("mdadmin", "admin"), ("mdsuper", "superadmin")):
        await c.execute(
            "INSERT INTO users (username,password_hash,role,permissions,token_version) VALUES ($1,'x',$2,'[]',0) "
            "ON CONFLICT (username) DO UPDATE SET role=$2, token_version=0",
            u,
            role,
        )
    admin = server.create_jwt("mdadmin", "admin", 0)
    superadmin = server.create_jwt("mdsuper", "superadmin", 0)
    db.db_pool = await asyncpg.create_pool(
        host=os.environ.get("DB_HOST", "localhost"), port=int(os.environ.get("DB_PORT", "5432")),
        user=os.environ["DB_USER"], password=os.environ["DB_PASS"], database=os.environ["DB_NAME"],
        min_size=1, max_size=4,
    )
    try:
        await run(c, admin, superadmin)
    finally:
        req("/api/system/install-profile", superadmin, {"profile": "school", "reset_labs": True})
        await cleanup(c)   # ayar satırları ve profil silinir; sunucunun 5 sn'lik önbelleği aşağıdaki çağrıyla boşalır
        req("/api/modules/vision", superadmin, {"enabled": None})
        await c.execute("DELETE FROM module_settings")
        await c.execute("DELETE FROM global_settings WHERE key = 'install_profile'")
        modules.invalidate()
        for key, value in (("enforce_agent_auth", old_enforce or "0"), ("agent_policies", old_policies),
                           ("concurrent_limit", old_limit)):
            if value is None:
                await c.execute("DELETE FROM global_settings WHERE key = $1", key)
            else:
                await c.execute("INSERT INTO global_settings (key,value) VALUES ($1,$2) "
                                "ON CONFLICT (key) DO UPDATE SET value=$2", key, value)
        await db.db_pool.close()
        await c.close()
    if FAILS:
        print("\nBASARISIZ: %d kontrol" % len(FAILS))
        sys.exit(1)
    print("\nTUM MODUL TESTLERI GECTI")


def set_module(token, module_id, enabled, lab=None):
    body = {"enabled": enabled}
    if lab:
        body["lab"] = lab
    return req("/api/modules/" + module_id, token, body)


async def run(c, admin, superadmin):
    print("== liste ve yetki")
    s, b, _ = req("/api/modules", admin)
    ids = [m["id"] for m in b.get("modules", [])]
    chk(s == 200 and "vision" in ids and "terminal" in ids and all(m["enabled"] for m in b["modules"]),
        "modül listesi, ayar yokken hepsi açık")
    chk(set(PCS.values()) <= set(b.get("labs", [])), "laboratuvarlar listede")
    chk(set_module(admin, "vision", False)[0] == 403, "admin modül ayarını değiştiremez")
    chk(set_module(superadmin, "yok", False)[0] == 404, "bilinmeyen modül 404")
    chk(set_module(superadmin, "vision", False, lab="Yok-Lab")[0] == 404, "bilinmeyen laboratuvar 404")

    print("== Vision: kurumda kapalı, bir laboratuvarda açık")
    s, b, _ = set_module(superadmin, "vision", False)
    chk(s == 200, "kurumda kapatıldı")
    chk(await c.fetchval("SELECT value FROM global_settings WHERE key='install_profile'") == "custom",
        "elle değişiklik profili 'özel' yaptı")
    chk(await c.fetchval("SELECT count(*) FROM device_audit_logs WHERE action='module_setting'") >= 1,
        "denetim kaydı")
    chk(closed(req("/api/thumbnail/HW-MD1", admin), "vision"), "önizleme 409")
    chk(closed(req("/api/audit/session/start", admin, {"target_pc": "HW-MD2", "reason": "t", "is_mandatory": True}),
               "vision"), "Vision oturumu 409")
    chk(set_module(superadmin, "vision", True, lab="MD-Lab1")[0] == 200, "MD-Lab1'de açıldı")
    s, b, _ = req("/api/thumbnail/HW-MD1", admin)
    chk(s == 200, "MD-Lab1 cihazında önizleme yetkisi var (%s)" % s)
    chk(closed(req("/api/thumbnail/HW-MD2", admin), "vision"), "MD-Lab2 cihazında hâlâ 409")
    s, b, _ = req("/api/modules", admin)
    vis = next(m for m in b["modules"] if m["id"] == "vision")
    chk(vis["enabled"] is False and vis["lab_enabled"].get("MD-Lab1") is True
        and vis["lab_overrides"].get("MD-Lab1") is True, "listede kurum ve laboratuvar durumu")

    print("== Uzak komut ve dağıtım")
    ag = await websockets.connect("%s/ws/agent/HW-MD2" % WS,
                                  additional_headers={"X-Agent-Secret": "HW-MD2-s", "X-Agent-Version": "0.1.14-alpha"})
    await ag.send(json.dumps({"hw_id": "HW-MD2", "hostname": "hw-md2", "status": "Online", "dna_payload": {
        "hardware": {"uuid": "HW-MD2-U", "bios_sn": "HW-MD2-B", "disk_sn": "-", "mac": "-", "ram_sn": "-"},
        "capabilities": {}}}))
    await collect(ag, 1)
    pend = await c.fetchval(
        "INSERT INTO tasks (target_pc, target_lab, script_path, status, created_at) VALUES "
        "('HW-MD2', 'MD-Lab2', 'echo md-bekleyen', 'Paused', to_char(now(), 'YYYY-MM-DD HH24:MI:SS')) RETURNING id")
    s, b, _ = set_module(superadmin, "terminal", False, lab="MD-Lab2")
    chk(s == 200 and b.get("tasks_denied") == 1, "MD-Lab2'de kapatılınca bekleyen görev reddedildi (%s)" % b)
    chk(await c.fetchval("SELECT status FROM tasks WHERE id=$1", pend) == "Denied", "görev 'Denied'")
    chk(closed(req("/api/deploy_orchestration", admin, {"target_mode": "PC", "targets": ["HW-MD2"],
                                                        "taskSequence": [{"name": "t", "type": "CMD",
                                                                          "command": "echo md-1"}]}), "terminal"),
        "kapalı laboratuvara komut 409")
    s, b, _ = req("/api/deploy_orchestration", admin, {"target_mode": "PC", "targets": ["HW-MD1", "HW-MD2"],
                                                       "taskSequence": [{"name": "t", "type": "CMD",
                                                                         "command": "echo md-2"}]})
    chk(s == 200 and b.get("created") == 1 and b.get("skipped_module_closed") == 1,
        "karışık hedef: açık olana görev, kapalı atlandı (%s)" % b)
    chk(await c.fetchval("SELECT count(*) FROM tasks WHERE script_path='echo md-2' AND target_pc='HW-MD2'") == 0,
        "kapalı cihaza görev yazılmadı")
    # Kapalı laboratuvara arada düşen görev (ör. eski yeniden deneme) kuyrukta reddedilir
    late = await c.fetchval(
        "INSERT INTO tasks (target_pc, target_lab, script_path, status, created_at) VALUES "
        "('HW-MD2', 'MD-Lab2', 'echo md-gec', 'Pending', to_char(now(), 'YYYY-MM-DD HH24:MI:SS')) RETURNING id")
    req("/api/set_concurrent_limit", admin, {"limit": 5})
    msgs = await collect(ag, 1.5)
    chk(not [m for m in msgs if m.get("action") == "execute" and m.get("task_id") == late], "ajana gönderilmedi")
    chk(await c.fetchval("SELECT status FROM tasks WHERE id=$1", late) == "Denied"
        and "MODÜL KAPALI" in (await c.fetchval("SELECT output FROM tasks WHERE id=$1", late) or ""),
        "kuyruk görevi reddetti")
    chk(closed(req("/api/deploy_orchestration", admin, {"target_mode": "PC", "targets": ["HW-MD2"],
                                                        "taskSequence": [{"name": "p", "type": "package",
                                                                          "command": "x.msi /qn"}]}), "deploy"),
        "uzak komut kapalıyken dağıtım da kapalı (bağımlılık)")
    chk(set_module(superadmin, "terminal", None, lab="MD-Lab2")[0] == 200, "istisna kaldırıldı")
    s, b, _ = req("/api/deploy_orchestration", admin, {"target_mode": "PC", "targets": ["HW-MD2"],
                                                       "taskSequence": [{"name": "t", "type": "CMD",
                                                                         "command": "echo md-3"}]})
    chk(s == 200 and b.get("created") == 1, "kurum ayarına döndü: komut kabul")
    await ag.close()

    print("== Zamanlanmış görevler, lisanslar, raporlar, yardım masası")
    chk(set_module(superadmin, "schedules", False, lab="MD-Lab2")[0] == 200, "MD-Lab2'de zamanlanmış görevler kapalı")
    sid = await c.fetchval(
        "INSERT INTO scheduled_tasks (name, command, target_mode, targets, schedule_type, run_at, enabled, next_run, "
        "created_by) VALUES ('md-test-once', 'echo md-sched', 'LAB', $1, 'once', now() - interval '1 minute', TRUE, "
        "now() - interval '1 minute', 'md') RETURNING id",
        json.dumps(["MD-Lab1", "MD-Lab2"]),
    )
    await scheduler.run_due()
    pcs = {r["target_pc"] for r in await c.fetch("SELECT target_pc FROM tasks WHERE schedule_id=$1", sid)}
    chk(pcs == {"HW-MD1"}, "zamanlayıcı kapalı laboratuvarı atladı (%s)" % sorted(pcs))
    chk(req("/api/scheduled_tasks", admin)[0] == 200, "bir laboratuvarda açık: liste 200")
    chk(set_module(superadmin, "software", False)[0] == 200, "yazılım envanteri kurumda kapalı")
    chk(closed(req("/api/licenses", admin), "licenses"), "lisanslar bağımlılıkla kapalı (409)")
    chk(closed(req("/api/software", admin), "software"), "yazılım listesi 409")
    s, b, _ = req("/api/software/HW-MD1", None, {"items": [{"name": "md-app", "version": "1"}]},
                  headers={"X-Agent-Id": "HW-MD1", "X-Agent-Secret": "HW-MD1-s"})
    chk(s == 200 and b.get("status") == "ignored", "ajanın listesi kabul edildi ama saklanmadı")
    chk(await c.fetchval("SELECT count(*) FROM device_software WHERE pc_name='HW-MD1'") == 0, "liste yazılmadı")
    chk(set_module(superadmin, "reports", False)[0] == 200 and closed(req("/api/reports/summary", admin), "reports"),
        "raporlar 409")
    chk(set_module(superadmin, "helpdesk", False)[0] == 200 and closed(req("/api/tickets", admin), "helpdesk"),
        "yardım masası 409")
    s, b, _ = req("/api/tickets/agent/HW-MD1", None, {"subject": "md", "body": "md", "category": "diger"},
                  headers={"X-Agent-Id": "HW-MD1", "X-Agent-Secret": "HW-MD1-s"})
    chk(s == 409, "tepsiden talep 409 (%s)" % s)

    print("== DNS politikası, karantina, uyandırma")
    chk(set_module(superadmin, "dns_policy", False)[0] == 200, "DNS politikası kurumda kapalı")
    chk(set_module(superadmin, "dns_policy", True, lab="MD-Lab1")[0] == 200, "MD-Lab1'de açık")
    req("/api/agent_policies", admin, {"fair_use_text": "md", "dns_categories": ["kumar"], "auto_quarantine": True,
                                       "quarantine_threshold": 3, "dns_domains": {"kumar": ["md.example"]}})
    s, pol, _ = req("/api/agent_policies")
    chk(s == 200 and pol.get("dns_domains") == {} and pol.get("auto_quarantine") is False and "modules" not in pol,
        "kimliksiz istek: kurum ayarı, liste boş, modül listesi yok")
    s, pol, _ = req("/api/agent_policies", headers={"X-Agent-Id": "HW-MD1", "X-Agent-Secret": "HW-MD1-s"})
    chk(pol.get("dns_domains", {}).get("kumar") == ["md.example"] and pol.get("auto_quarantine") is True
        and pol.get("modules", {}).get("dns_policy") is True and pol["modules"].get("vision") is True,
        "anahtarlı ajan: kendi laboratuvarının ayarı ve modül listesi")
    s, pol, _ = req("/api/agent_policies", headers={"X-Agent-Id": "HW-MD1", "X-Agent-Secret": "yanlis"})
    chk("modules" not in pol and pol.get("dns_domains") == {}, "yanlış anahtar: kimliksiz gibi")
    chk(set_module(superadmin, "quarantine", False, lab="MD-Lab1")[0] == 200, "MD-Lab1'de karantina kapalı")
    s, pol, _ = req("/api/agent_policies", headers={"X-Agent-Id": "HW-MD1", "X-Agent-Secret": "HW-MD1-s"})
    chk(pol.get("dns_domains", {}).get("kumar") and pol.get("auto_quarantine") is False,
        "karantina kapalı: tespit sürer, kendini karantinaya almaz")
    chk(closed(req("/api/security/lockdown", admin, {"target_pc": "HW-MD1", "reason": "md"}), "quarantine"),
        "kilitleme 409")
    before = await c.fetchval("SELECT count(*) FROM agent_logs_v2 WHERE pc_name='HW-MD2'")
    s, b, _ = req("/api/policy_alert", None, {"hw_id": "HW-MD2", "domain": "md.example", "category": "kumar"},
                  headers={"X-Agent-Id": "HW-MD2", "X-Agent-Secret": "HW-MD2-s"})
    chk(s == 200 and b.get("status") == "ignored"
        and await c.fetchval("SELECT count(*) FROM agent_logs_v2 WHERE pc_name='HW-MD2'") == before,
        "kapalı laboratuvardan DNS uyarısı yok sayıldı")
    chk(set_module(superadmin, "wol", False, lab="MD-Lab2")[0] == 200, "MD-Lab2'de uyandırma kapalı")
    chk(closed(req("/api/wake_pc/HW-MD2", admin, {}), "wol"), "uyandırma 409")
    chk(closed(req("/api/wake_lab/MD-Lab2", admin, {}), "wol"), "laboratuvarı uyandırma 409")
    chk(req("/api/wake_pc/HW-MD1", admin, {})[0] == 200, "açık laboratuvarda uyandırma çalışır")

    print("== Profiller")
    s, b, _ = req("/api/system/install-profile/org", superadmin)
    chk(s == 200 and any(ch["id"] == "vision" for ch in b.get("changes", [])) is False
        and b.get("lab_overrides", 0) >= 1, "önizleme: değişecekler ve istisna sayısı (%s)" % b)
    chk(req("/api/system/install-profile/org", admin)[0] == 403, "önizleme yalnız superadmin")
    chk(req("/api/system/install-profile", superadmin, {"profile": "yok"})[0] == 400, "bilinmeyen profil 400")
    s, b, _ = req("/api/system/install-profile", superadmin, {"profile": "school", "reset_labs": True})
    chk(s == 200 and b.get("profile") == "school", "okul profili uygulandı")
    chk(await c.fetchval("SELECT value FROM global_settings WHERE key='install_profile'") == "school"
        and await c.fetchval("SELECT count(*) FROM module_settings WHERE scope_type='lab'") == 0,
        "profil kaydedildi, laboratuvar istisnaları silindi")
    s, b, _ = req("/api/modules", admin)
    state = {m["id"]: m["enabled"] for m in b["modules"]}
    chk(state == modules.PROFILES["school"], "modül durumları profildeki gibi")
    chk(closed(req("/api/tickets", admin), "helpdesk") and req("/api/thumbnail/HW-MD2", admin)[0] == 200,
        "okul: yardım masası kapalı, Vision açık")


if __name__ == "__main__":
    started = time.time()
    asyncio.run(main())
    print("süre: %.1f sn" % (time.time() - started))
