"""Sınav modu — entegrasyon testi (CI 'security' job'ı). Ajanlar WebSocket üzerinden taklit edilir.

- Yetki: izleyici durumu okur, başlatamaz ve bitiremez (403); oturumsuz 401.
- Doğrulama: bilinmeyen sınıf, atanmamışlar, gerekçe, izin girişleri (alan adı, IP, CIDR; en fazla 50), bitiş
  (gelecekte, en çok 8 saat), korunan program, tanınmayan alan.
- Başlatma: sınıfın bağlı ajanları exam_mode'u sözleşmedeki biçimde alır, başka sınıf almaz; ikinci sınav 409; denetim
  kaydı. Bilgisayar durumları: sınavda, ayrıldı, ulaşılamıyor, desteklemiyor (eski ajan), bekleniyor, reddetti.
- Kurtarma: yeniden bağlanan ajan sınavı yeniden alır; sınav sürerken sınıfa taşınan bilgisayar (bağlıysa hemen,
  değilse bağlanınca) alır; sınıftan çıkan enabled:false alır.
- Erken çıkış: sınav sürerken "sınavda değilim" bildirim + denetim kaydı (gönderimden hemen sonraki bildirim sayılmaz).
- Yetenek reddi: capability_denied (exam) bilgisayarı "reddetti" yapar ve bildirim üretir.
- Bitirme: bağlı ajanlar enabled:false alır, çevrimdışı olan bağlanınca alır, sınav yokken "sınavdayım" diyen ajan
  düzeltilir; denetim kaydı; ikinci bitirme 404.
- Süre dolunca: okuma ucu ve zamanlayıcı işlevi (exams.end_expired, bu süreçte) sınavı kapatır; exam_auto_end.
- Modül kapatılınca ve sınıf silinince süren sınav biter.

Bazı adımlar sunucunun modüllerini bu süreçte, aynı veritabanına bağlanarak çağırır (zamanlayıcı işlevi).
Ortam: POPS_TEST_HTTP + DB_* + JWT_SECRET.
"""

import asyncio
import hashlib
import json
import os
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

sys.path.insert(0, os.path.join(os.path.dirname(__file__), os.pardir))
import asyncpg  # noqa: E402
import websockets  # noqa: E402

import server  # noqa: E402  (create_jwt)
from pops import db, exams  # noqa: E402

HTTP = os.environ["POPS_TEST_HTTP"]
WS = os.environ.get("POPS_TEST_WS", HTTP.replace("http://", "ws://").replace("https://", "wss://"))
LAB = "EX-Lab 9/A"          # boşluk ve eğik çizgi: yol bölümü olarak
LAB_B = "EX-Lab-B"
LAB_C = "EX-Lab-C"
PC1, PC2, PC3, PC4, PC5 = "HW-EXAM01", "HW-EXAM02", "HW-EXAM03", "HW-EXAM04", "HW-EXAM05"
OLD, OFF = "HW-EXAMOLD", "HW-EXAMOFF"
PCS = {PC1: LAB, PC2: LAB, OLD: LAB, OFF: LAB, PC3: LAB_B, PC4: LAB_B, PC5: LAB_C}
LABS = (LAB, LAB_B, LAB_C)
VERSION = "0.1.23-alpha"
FAILS = []


def chk(cond, msg):
    print(("  OK   " if cond else "  FAIL ") + msg)
    if not cond:
        FAILS.append(msg)


def _sha(s):
    return hashlib.sha256(s.encode()).hexdigest()


def req(path, token=None, body=None, method=None):
    data = json.dumps(body).encode() if body is not None else None
    r = urllib.request.Request(HTTP + path, data=data, method=method or ("POST" if body is not None else "GET"))
    if body is not None:
        r.add_header("Content-Type", "application/json")
    if token:
        r.add_header("Authorization", "Bearer " + token)
    try:
        with urllib.request.urlopen(r, timeout=40) as resp:
            return resp.status, json.loads(resp.read() or b"{}")
    except urllib.error.HTTPError as e:
        try:
            return e.code, json.loads(e.read() or b"{}")
        except ValueError:
            return e.code, {}


def exam_path(lab, v1=True):
    return "/api/%slabs/%s/exam" % ("v1/" if v1 else "", urllib.parse.quote(lab, safe="/"))


def dna(pc):
    return json.dumps({
        "hw_id": pc,
        "dna_payload": {
            "hardware": {"uuid": pc + "-U", "bios_sn": pc + "-B", "disk_sn": pc + "-D", "mac": "-", "ram_sn": "-"},
            "capabilities": {"ram_readable": True},
        },
        "hostname": pc.lower(),
        "status": "Online",
    })


async def connect(pc):
    ws = await websockets.connect("%s/ws/agent/%s" % (WS, pc),
                                  additional_headers={"X-Agent-Secret": pc + "-s", "X-Agent-Version": VERSION})
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
        except websockets.exceptions.ConnectionClosed as e:
            out.append(("closed", e.code))
            break
        try:
            out.append(json.loads(raw))
        except ValueError:
            pass
    return out


async def expect_exam(ws, seconds=3.0):
    """Gelen ilk exam_mode mesajı (yoksa None); araya giren diğer mesajlar atlanır."""
    loop = asyncio.get_event_loop()
    end = loop.time() + seconds
    while loop.time() < end:
        try:
            raw = await asyncio.wait_for(ws.recv(), timeout=max(0.05, end - loop.time()))
        except (asyncio.TimeoutError, websockets.exceptions.ConnectionClosed):
            return None
        try:
            msg = json.loads(raw)
        except ValueError:
            continue
        if isinstance(msg, dict) and msg.get("action") == "exam_mode":
            return msg
    return None


async def state_msg(ws, enabled, until=None):
    await ws.send(json.dumps({"type": "exam_state", "enabled": enabled, "since": int(time.time()),
                              "until": until}))


async def wait_for(c, sql, *args, timeout=8):
    for _ in range(int(timeout / 0.2)):
        value = await c.fetchval(sql, *args)
        if value:
            return value
        await asyncio.sleep(0.2)
    return await c.fetchval(sql, *args)


def states(admin, lab):
    s, body = req(exam_path(lab), admin)
    return {d["pc_name"]: d["state"] for d in body.get("devices", [])}, body


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
    await c.execute("DELETE FROM exam_sessions WHERE lab_name LIKE 'EX-Lab%'")
    await c.execute("DELETE FROM exam_devices WHERE pc_name = ANY($1::text[])", names)
    for table, col in (("agent_secrets", "pc_name"), ("agent_bypass_keys", "pc_name"), ("clients", "pc_name"),
                       ("agent_versions", "pc_name"), ("hw_inventory", "pc_name"), ("notifications", "pc_name"),
                       ("agent_logs_v2", "pc_name")):
        await c.execute("DELETE FROM %s WHERE %s = ANY($1::text[])" % (table, col), names)
    await c.execute("DELETE FROM custom_labs WHERE lab_name LIKE 'EX-Lab%'")
    await c.execute("DELETE FROM module_settings WHERE module_id = 'exam' AND scope_id LIKE 'EX-Lab%'")


async def main():
    c = await conn()
    old_enforce = await c.fetchval("SELECT value FROM global_settings WHERE key='enforce_agent_auth'")
    await c.execute(
        "INSERT INTO global_settings (key,value) VALUES ('enforce_agent_auth','1') "
        "ON CONFLICT (key) DO UPDATE SET value='1'"
    )
    await cleanup(c)
    for lab in LABS:
        await c.execute("INSERT INTO custom_labs (lab_name) VALUES ($1) ON CONFLICT DO NOTHING", lab)
    for pc, lab in PCS.items():
        await c.execute("INSERT INTO agent_secrets (pc_name, secret_hash) VALUES ($1,$2)", pc, _sha(pc + "-s"))
        await c.execute("INSERT INTO clients (pc_name, hostname, lab_name, status) VALUES ($1, $2, $3, 'Offline')",
                        pc, pc.lower(), lab)
        # Envanter var: bağlanınca get_hardware istenmez (mesaj akışı sade kalır)
        await c.execute("INSERT INTO hw_inventory (pc_name, cpu) VALUES ($1, 'test') ON CONFLICT DO NOTHING", pc)
    for u, role in (("exadmin", "admin"), ("exsuper", "superadmin"), ("exviewer", "viewer")):
        await c.execute(
            "INSERT INTO users (username,password_hash,role,permissions,token_version) VALUES ($1,'x',$2,'[]',0) "
            "ON CONFLICT (username) DO UPDATE SET role=$2, token_version=0",
            u,
            role,
        )
    admin = server.create_jwt("exadmin", "admin", 0)
    superadmin = server.create_jwt("exsuper", "superadmin", 0)
    viewer = server.create_jwt("exviewer", "viewer", 0)
    db.db_pool = await asyncpg.create_pool(
        host=os.environ.get("DB_HOST", "localhost"), port=int(os.environ.get("DB_PORT", "5432")),
        user=os.environ["DB_USER"], password=os.environ["DB_PASS"], database=os.environ["DB_NAME"],
        min_size=1, max_size=4,
    )
    sockets = {}
    try:
        await run(c, admin, superadmin, viewer, sockets)
    finally:
        for ws in sockets.values():
            try:
                await ws.close()
            except Exception:
                pass
        await cleanup(c)
        await c.execute("UPDATE global_settings SET value=$1 WHERE key='enforce_agent_auth'", old_enforce or "0")
        await db.db_pool.close()
        await c.close()
    if FAILS:
        print("\nBASARISIZ: %d kontrol" % len(FAILS))
        sys.exit(1)
    print("\nTUM SINAV MODU TESTLERI GECTI")


GOOD = {"allow": ["sinav.meb.gov.tr", "10.0.0.5", "10.1.0.0/24"], "duration_minutes": 40,
        "message": "Sınav modu: yalnızca sınav sitesi açık", "block_apps": ["cmd.exe", "powershell"],
        "reason": "Matematik yazılısı"}


async def run(c, admin, superadmin, viewer, sockets):
    print("== yetki")
    chk(req(exam_path(LAB), None)[0] == 401, "oturumsuz okuma 401")
    chk(req(exam_path(LAB), viewer, GOOD)[0] == 403, "izleyici başlatamaz (403)")
    chk(req(exam_path(LAB), viewer, method="DELETE")[0] == 403, "izleyici bitiremez (403)")
    s, body = req(exam_path(LAB), viewer)
    chk(s == 200 and body.get("active") is False and body.get("module_enabled") is True,
        "izleyici durumu okur; sınav yok (%s %s)" % (s, body))
    chk(req("/api/v1/exams", viewer)[0] == 200, "izleyici geçmişi okur")
    s, body = req(exam_path("EX-Lab-Yok"), viewer)
    chk(s == 200 and body.get("active") is False, "bilinmeyen sınıfın durumu: sınav yok (404 değil)")

    print("== doğrulama")
    chk(req(exam_path("EX-Lab-Yok"), admin, GOOD)[0] == 404, "bilinmeyen sınıf 404")
    chk(req(exam_path("Atanmamis_Cihazlar"), admin, GOOD)[0] == 400, "atanmamışlarda sınav yok (400)")
    cases = [
        ({"reason": None}, 422, "gerekçe yok"),
        ({"reason": "   "}, 400, "boş gerekçe"),
        ({"allow": ["sinav.meb.gov.tr", "kötü giriş"]}, 400, "geçersiz izin girişi"),
        ({"allow": ["*.meb.gov.tr"]}, 400, "joker"),
        ({"allow": ["0.0.0.0/0"]}, 400, "çok geniş ağ"),
        ({"allow": ["h%d.example.com" % i for i in range(51)]}, 400, "51 giriş"),
        ({"duration_minutes": None, "until": int(time.time()) - 10}, 400, "geçmişte bitiş"),
        ({"duration_minutes": None, "until": int(time.time()) + 9 * 3600}, 400, "8 saatten uzun"),
        ({"duration_minutes": None}, 400, "bitiş yok"),
        ({"until": int(time.time()) + 600}, 400, "bitiş ve süre birlikte"),
        ({"duration_minutes": 481}, 422, "süre sınırı"),
        ({"block_apps": ["explorer.exe"]}, 400, "korunan program"),
        ({"lab": LAB}, 422, "tanınmayan alan"),
    ]
    for change, code, why in cases:
        body = dict(GOOD)
        for k, v in change.items():
            if v is None:
                body.pop(k, None)
            else:
                body[k] = v
        s, out = req(exam_path(LAB), admin, body)
        chk(s == code, "%s: %s (%s %s)" % (why, code, s, str(out.get("detail"))[:90]))
    s, out = req(exam_path(LAB), admin, dict(GOOD, allow=["ok.example.com", "kötü giriş"]))
    chk("kötü giriş" in str(out.get("detail")), "hata mesajı geçersiz girişi adıyla yazar")
    chk(not await c.fetchval("SELECT count(*) FROM exam_sessions WHERE lab_name = $1", LAB),
        "geçersiz isteklerden kayıt oluşmadı")

    print("== başlatma ve teslim")
    for pc in (PC1, PC2, OLD, PC3):
        sockets[pc] = await connect(pc)
    pcs = (PC1, PC2, OLD, PC3)
    first = dict(zip(pcs, await asyncio.gather(*[collect(sockets[pc], 1.5) for pc in pcs])))
    info = next((m for m in first[PC1] if isinstance(m, dict) and m.get("action") == "server_info"), {})
    chk("exam_mode" in info.get("features", []), "server_info exam_mode'u duyurur")
    chk(not any(isinstance(m, dict) and m.get("action") == "exam_mode" for msgs in first.values() for m in msgs),
        "sınav yokken bağlanan ajan exam_mode almaz")
    started_at = time.time()
    s, out = req(exam_path(LAB), admin, GOOD)
    exam = out.get("exam") or {}
    chk(s == 200 and exam.get("active") and out.get("devices") == 4 and out.get("delivered") == 3,
        "sınav başladı: 4 bilgisayar, 3'ü bağlı (%s %s)" % (s, {k: out.get(k) for k in ("devices", "delivered")}))
    chk(exam.get("allow") == GOOD["allow"] and exam.get("block_apps") == ["cmd.exe", "powershell.exe"]
        and exam.get("started_by") == "exadmin" and exam.get("reason") == GOOD["reason"], "kayıt alanları")
    got = {pc: await expect_exam(sockets[pc]) for pc in (PC1, PC2, OLD)}
    want_keys = {"action", "enabled", "allow", "until", "now", "message", "block_apps"}
    msg = got[PC1] or {}
    chk(all(got.values()), "sınıfın bağlı ajanları exam_mode aldı")
    chk(set(msg) == want_keys and msg.get("enabled") is True and msg.get("allow") == GOOD["allow"]
        and msg.get("message") == GOOD["message"] and msg.get("block_apps") == ["cmd.exe", "powershell.exe"]
        and isinstance(msg.get("until"), int) and abs(msg["until"] - (started_at + 2400)) < 10
        # now: sunucunun gönderdiği andaki saati; ajan kalan süreyi until - now ile bulur
        and isinstance(msg.get("now"), int) and abs(msg["now"] - time.time()) < 30,
        "mesaj sözleşmedeki biçimde (%s)" % msg)
    chk(await expect_exam(sockets[PC3], 1.0) is None, "başka sınıftaki ajan almadı")
    until = msg.get("until")
    s, out = req(exam_path(LAB), admin, GOOD)
    chk(s == 409, "aynı sınıfta ikinci sınav 409")
    audit = await c.fetchrow("SELECT reason, changes FROM device_audit_logs WHERE action = 'exam_start' "
                             "ORDER BY id DESC LIMIT 1")
    changes = json.loads(audit["changes"]) if audit else {}
    chk(audit is not None and changes.get("lab") == LAB and changes.get("by") == "exadmin"
        and changes.get("reason") == GOOD["reason"], "denetim kaydı: exam_start")

    print("== bilgisayar durumları")
    for pc in (PC1, PC2):
        await state_msg(sockets[pc], True, until)
    await asyncio.sleep(0.8)
    await c.execute("UPDATE exam_devices SET sent_at = NOW() - interval '30 seconds' WHERE pc_name = $1", OLD)
    st, body = states(admin, LAB)
    chk(body.get("active") and body["exam"]["id"] == exam.get("id") and 0 < body["exam"]["remaining_seconds"] <= 2400,
        "durum: süren sınav, kalan süre")
    chk(st == {PC1: "in_exam", PC2: "in_exam", OLD: "unsupported", OFF: "unreachable"},
        "sınavda / desteklemiyor (eski ajan) / ulaşılamıyor (%s)" % st)
    chk(body["counts"]["in_exam"] == 2 and body["counts"]["unreachable"] == 1, "durum sayıları")
    pc1 = next(d for d in body["devices"] if d["pc_name"] == PC1)
    chk(pc1["until"] == until and pc1["agent_version"] == VERSION and pc1["online"] is True,
        "ajanın bildirdiği bitiş ve sürüm")
    s, hist = req("/api/v1/exams?active=true", viewer)
    item = next((i for i in hist.get("items", []) if i["id"] == exam.get("id")), {})
    chk(s == 200 and item.get("active") and item.get("counts", {}).get("in_exam") == 2,
        "süren sınavlar listesi durum sayılarıyla")

    print("== yeniden bağlanma")
    await sockets[PC1].close()
    await asyncio.sleep(0.5)
    sockets[PC1] = await connect(PC1)
    again = await expect_exam(sockets[PC1])
    chk(again is not None and again.get("enabled") is True and again.get("until") == until,
        "yeniden bağlanan ajan sınavı yeniden aldı")
    # Gönderimden hemen sonraki "sınavda değilim" (ajan bağlanırken eski durumunu bildirdi) erken çıkış sayılmaz
    await state_msg(sockets[PC1], False)
    await asyncio.sleep(0.8)
    chk(not await c.fetchval("SELECT count(*) FROM notifications WHERE event = 'exam_left' AND pc_name = $1", PC1),
        "gönderimden hemen sonraki 'sınavda değil' bildirim üretmedi")
    chk(states(admin, LAB)[0].get(PC1) == "pending", "durum: bekleniyor")
    await state_msg(sockets[PC1], True, until)

    print("== sınav sürerken sınıfa taşınan bilgisayar")
    s, _ = req("/api/v1/devices/move", admin, {"pc_names": [PC3, PC4], "new_lab": LAB})
    chk(s == 200, "PC3 (bağlı) ve PC4 (çevrimdışı) sınıfa taşındı")
    moved = await expect_exam(sockets[PC3])
    chk(moved is not None and moved.get("enabled") is True and moved.get("until") == until,
        "bağlı bilgisayar taşınınca hemen aldı")
    sockets[PC4] = await connect(PC4)
    late = await expect_exam(sockets[PC4])
    chk(late is not None and late.get("enabled") is True, "çevrimdışıyken taşınan bilgisayar bağlanınca aldı")
    s, _ = req("/api/v1/devices/move", admin, {"pc_names": [PC3], "new_lab": LAB_B})
    out_msg = await expect_exam(sockets[PC3])
    chk(out_msg == {"action": "exam_mode", "enabled": False}, "sınıftan çıkan bilgisayar enabled:false aldı")

    print("== erken çıkış bildirimi")
    await c.execute("UPDATE exam_devices SET sent_at = NOW() - interval '30 seconds' WHERE pc_name = $1", PC2)
    await state_msg(sockets[PC2], False)
    n = await wait_for(c, "SELECT count(*) FROM notifications WHERE event = 'exam_left' AND pc_name = $1", PC2)
    chk(n == 1, "sınav sürerken 'sınavda değilim' bildirimi (exam_left)")
    chk(await c.fetchval("SELECT count(*) FROM device_audit_logs WHERE action = 'exam_left' AND hw_id = $1", PC2) == 1,
        "denetim kaydı: exam_left")
    await state_msg(sockets[PC2], False)
    await asyncio.sleep(0.6)
    chk(await c.fetchval("SELECT count(*) FROM notifications WHERE event = 'exam_left' AND pc_name = $1", PC2) == 1,
        "aynı sınavda ikinci bildirim yok")
    chk(states(admin, LAB)[0].get(PC2) == "left", "durum: ayrıldı")

    print("== yetenek reddi")
    await sockets[PC4].send(json.dumps({"type": "capability_denied", "capability": "exam", "action": "exam_mode",
                                        "reason": "disabled_locally"}))
    n = await wait_for(c, "SELECT count(*) FROM notifications WHERE event = 'capability_denied' AND pc_name = $1", PC4)
    chk(n == 1, "reddetme bildirimi (capability_denied)")
    chk(states(admin, LAB)[0].get(PC4) == "denied", "durum: reddetti")
    chk(await c.fetchval("SELECT count(*) FROM agent_logs_v2 WHERE pc_name = $1 AND action = 'capability_denied' "
                         "AND reason = 'exam'", PC4) == 1, "olay kaydı: capability_denied (exam)")

    print("== bitirme")
    await sockets[PC2].close()   # bitişte çevrimdışı: bağlanınca enabled:false almalı
    await asyncio.sleep(0.5)
    s, out = req(exam_path(LAB), admin, {"reason": "Sınav erken bitti"}, method="DELETE")
    chk(s == 200 and out.get("exam", {}).get("end_reason") == "admin" and out["exam"]["ended_by"] == "exadmin"
        and out.get("delivered") == 3, "sınav bitirildi; 3 bağlı ajana gönderildi (%s %s)" % (s, out.get("delivered")))
    ends = {pc: await expect_exam(sockets[pc]) for pc in (PC1, OLD, PC4)}
    chk(all(m == {"action": "exam_mode", "enabled": False} for m in ends.values()),
        "bağlı ajanlar enabled:false aldı (%s)" % ends)
    audit = await c.fetchrow("SELECT changes FROM device_audit_logs WHERE action = 'exam_end' ORDER BY id DESC LIMIT 1")
    changes = json.loads(audit["changes"]) if audit else {}
    chk(changes.get("lab") == LAB and changes.get("by") == "exadmin" and changes.get("note") == "Sınav erken bitti",
        "denetim kaydı: exam_end")
    chk(req(exam_path(LAB), admin, method="DELETE")[0] == 404, "ikinci bitirme 404")
    s, body = req(exam_path(LAB), viewer)
    chk(s == 200 and body.get("active") is False and body.get("devices") == [], "durum: sınav yok")
    sockets[PC2] = await connect(PC2)
    back = await expect_exam(sockets[PC2])
    chk(back == {"action": "exam_mode", "enabled": False}, "bitişte çevrimdışı olan bağlanınca enabled:false aldı")
    await state_msg(sockets[PC1], True, until)
    fix = await expect_exam(sockets[PC1])
    chk(fix == {"action": "exam_mode", "enabled": False}, "sınav yokken 'sınavdayım' diyen ajan düzeltildi")

    print("== süre dolunca (okuma ucu)")
    s, out = req(exam_path(LAB, v1=False), admin, dict(GOOD, reason="Süre testi"))
    exam2 = out.get("exam") or {}
    starts = {pc: await expect_exam(sockets[pc]) for pc in (PC1, PC2, OLD, PC4)}
    chk(s == 200 and all(m and m.get("enabled") for m in starts.values()), "yeni sınav başladı (/api), 4 ajan aldı")
    await c.execute("UPDATE exam_sessions SET until_at = NOW() - interval '1 second' WHERE id = $1", exam2.get("id"))
    s, body = req(exam_path(LAB), viewer)
    chk(s == 200 and body.get("active") is False, "bitişi geçen sınav okunurken kapandı")
    expired = {pc: await expect_exam(sockets[pc]) for pc in (PC1, PC2, OLD, PC4)}
    chk(all(m == {"action": "exam_mode", "enabled": False} for m in expired.values()),
        "süresi dolunca enabled:false gönderildi (%s)" % expired)
    row = await c.fetchrow("SELECT ended_by, end_reason FROM exam_sessions WHERE id = $1", exam2.get("id"))
    chk(row and row["end_reason"] == "expired" and row["ended_by"] == "system", "kayıt: expired, system")
    chk(await c.fetchval("SELECT count(*) FROM device_audit_logs WHERE action = 'exam_auto_end' "
                         "AND changes::jsonb ->> 'exam_id' = $1", str(exam2.get("id"))) == 1,
        "denetim kaydı: exam_auto_end")

    print("== süre dolunca (zamanlayıcı işlevi)")
    s, out = req(exam_path(LAB_B), admin, dict(GOOD, reason="Zamanlayıcı testi"))
    exam3 = out.get("exam") or {}
    chk(s == 200 and await expect_exam(sockets[PC3]) is not None, "B sınıfında sınav başladı, PC3 aldı")
    await c.execute("UPDATE exam_sessions SET until_at = NOW() - interval '1 second' WHERE id = $1", exam3.get("id"))
    n = await exams.end_expired()   # zamanlayıcının her turda çağırdığı işlev (bu süreçte, aynı veritabanı)
    row = await c.fetchrow("SELECT ended_at, end_reason FROM exam_sessions WHERE id = $1", exam3.get("id"))
    chk(n >= 1 and row["ended_at"] is not None and row["end_reason"] == "expired", "zamanlayıcı süresi dolanı kapattı")
    chk(await c.fetchval("SELECT count(*) FROM device_audit_logs WHERE action = 'exam_auto_end' "
                         "AND changes::jsonb ->> 'exam_id' = $1", str(exam3.get("id"))) == 1,
        "denetim kaydı: exam_auto_end (zamanlayıcı)")
    await state_msg(sockets[PC3], True, exam3.get("until"))
    chk(await expect_exam(sockets[PC3]) == {"action": "exam_mode", "enabled": False},
        "bitmiş sınavda kalan ajan bildirince düzeltildi")

    print("== modül kapatılınca")
    s, _ = req(exam_path(LAB), admin, dict(GOOD, reason="Modül testi"))
    starts = {pc: await expect_exam(sockets[pc]) for pc in (PC1, PC2, OLD, PC4)}
    chk(s == 200 and all(m and m.get("enabled") for m in starts.values()), "sınav başladı")
    s, out = req("/api/modules/exam", superadmin, {"enabled": False, "lab": LAB})
    chk(s == 200 and out.get("exams_ended") == 1, "modül sınıfta kapatıldı, süren sınav bitti (%s)" % out)
    offs = {pc: await expect_exam(sockets[pc]) for pc in (PC1, PC2, OLD, PC4)}
    chk(all(m == {"action": "exam_mode", "enabled": False} for m in offs.values()), "ajanlar enabled:false aldı")
    s, out = req(exam_path(LAB), admin, GOOD)
    chk(s == 409, "modül kapalıyken başlatılamaz (409)")
    chk(req(exam_path(LAB), viewer)[1].get("module_enabled") is False, "durum: modül kapalı")
    req("/api/modules/exam", superadmin, {"enabled": None, "lab": LAB})

    print("== sınıf silinince")
    sockets[PC5] = await connect(PC5)
    await collect(sockets[PC5], 1)
    s, _ = req(exam_path(LAB_C), admin, dict(GOOD, reason="Silme testi"))
    chk(s == 200 and await expect_exam(sockets[PC5]) is not None, "C sınıfında sınav başladı")
    s, _ = req("/api/v1/labs/%s" % LAB_C, admin, method="DELETE")
    chk(s == 200 and await expect_exam(sockets[PC5]) == {"action": "exam_mode", "enabled": False},
        "sınıf silinince bilgisayarı enabled:false aldı")
    chk(await c.fetchval("SELECT end_reason FROM exam_sessions WHERE lab_name = $1 ORDER BY id DESC LIMIT 1", LAB_C)
        == "lab_deleted", "kayıt: lab_deleted")

    print("== geçmiş")
    s, hist = req("/api/v1/exams?lab=%s&limit=10" % urllib.parse.quote(LAB), viewer)
    items = hist.get("items", [])
    chk(s == 200 and [i["end_reason"] for i in items] == ["module_off", "expired", "admin"],
        "sınıfın geçmişi yeniden eskiye (%s)" % [i.get("end_reason") for i in items])
    first_exam = items[-1] if items else {}
    chk(first_exam.get("devices_sent", 0) >= 5 and first_exam.get("devices_entered") == 2
        and first_exam.get("devices_left") == 1, "gönderilen, giren, ayrılan sayıları (%s)"
        % {k: first_exam.get(k) for k in ("devices_sent", "devices_entered", "devices_left")})
    chk(req("/api/v1/exams?limit=0", viewer)[0] == 422, "geçersiz limit 422")


if __name__ == "__main__":
    asyncio.run(main())
