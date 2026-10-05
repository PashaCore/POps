"""Birden fazla backend süreci (REDIS_URL) — entegrasyon testi (CI 'ha' job'ı).

İki backend süreci (A ve B; POPS_HA_PORTS, varsayılan 9997 ve 9998) aynı veritabanı ve aynı Redis'le başlatılır.
Süreçler Redis'e testin açtığı küçük bir TCP aktarıcısı üzerinden bağlanır: test aktarıcıyı kapatarak "Redis durdu"
durumunu kurar ve yeniden açar. Denenenler:
- A'ya bağlı ajana B üzerinden verilen komut ulaşır (karantina, görev);
- B'deki panel A'daki ajanın heartbeat'ini ve durum değişikliklerini görür (yayın ve cihaz listesi);
- A'daki Vision tünelinin kareleri (metin ve ikili, Vision v2) ve iletileri (monitörler, pano) YALNIZCA oturum
  sahibinin B'deki paneline gider; B'deki panelin uzaktan girdisi, görüntüleyici komutu ve panosu A'daki tünele ulaşır;
- A'daki görev sonucu B'deki panele ve işlem merkezine (görev durumu) düşer;
- A'ya bildirilen güncelleme adımı B'nin /api/system/update-progress yanıtında görünür;
- cihaz listesi sürümü (ETag, ?since=) iki süreçte aynıdır, A'daki değişiklik B'nin paneline bildirilir;
- kurum birimi kapsamı süreçler arasında da geçerlidir: A'daki cihazın yayını B'deki kapsamlı panele yalnızca cihaz
  kapsamdaysa gider;
- istek sınırı süreçler arasında ortaktır; ajan B'ye yeniden bağlanınca A'daki kayıt devredilir;
- Redis durunca A kendi ajan ve paneliyle çalışmaya devam eder, Redis geri gelince süreçler arası yol yeniden açılır.

Ortam: DB_* (migrate edilmiş veritabanı) + JWT_SECRET + POPS_TEST_REDIS (ör. redis://127.0.0.1:6379/0). POPS_TEST_REDIS
yoksa test atlanır. Çalıştırma: python -m pytest -v tests/test_ha.py (ya da doğrudan python tests/test_ha.py).
"""

import asyncio
import hashlib
import json
import os
import secrets
import signal
import struct
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request

sys.path.insert(0, os.path.join(os.path.dirname(__file__), os.pardir))
import asyncpg  # noqa: E402
import pytest  # noqa: E402
import websockets  # noqa: E402

import server  # noqa: E402  (create_jwt)

BACKEND = os.path.abspath(os.path.join(os.path.dirname(__file__), os.pardir))
PORTS = [int(p) for p in os.environ.get("POPS_HA_PORTS", "9997,9998").split(",")]
REDIS = urllib.parse.urlsplit(os.environ.get("POPS_TEST_REDIS") or "redis://127.0.0.1:6379/0")
A, B = ("http://127.0.0.1:%d" % p for p in PORTS)
WA, WB = ("ws://127.0.0.1:%d" % p for p in PORTS)
PC1, PC2 = "HW-HA-0001", "HW-HA-0002"
SECRETS = {PC1: secrets.token_urlsafe(24), PC2: secrets.token_urlsafe(24)}
ADMIN_PASS = secrets.token_urlsafe(12)
FAILS = []


def chk(cond, msg):
    print(("  OK   " if cond else "  FAIL ") + msg)
    if not cond:
        FAILS.append(msg)


def req(base, path, token=None, body=None):
    data = json.dumps(body).encode() if body is not None else None
    r = urllib.request.Request(base + path, data=data, method="POST" if body is not None else "GET")
    if body is not None:
        r.add_header("Content-Type", "application/json")
    if token:
        r.add_header("Authorization", "Bearer " + token)
    try:
        with urllib.request.urlopen(r, timeout=20) as resp:
            return resp.status, json.loads(resp.read() or b"{}")
    except urllib.error.HTTPError as e:
        try:
            return e.code, json.loads(e.read() or b"{}")
        except ValueError:
            return e.code, {}


async def areq(*args, **kwargs):
    return await asyncio.to_thread(req, *args, **kwargs)


class RedisRelay:
    """Süreçlerle Redis arasındaki TCP aktarıcısı; stop() bütün bağlantıları keser ve yenilerini reddeder."""

    def __init__(self):
        self.server = None
        self.port = None
        self.writers = set()

    async def _pipe(self, reader, writer):
        try:
            while True:
                data = await reader.read(65536)
                if not data:
                    break
                writer.write(data)
                await writer.drain()
        except Exception:
            pass
        finally:
            writer.close()

    async def _client(self, reader, writer):
        try:
            up_r, up_w = await asyncio.open_connection(REDIS.hostname, REDIS.port or 6379)
        except OSError:
            writer.close()
            return
        self.writers |= {writer, up_w}
        await asyncio.gather(self._pipe(reader, up_w), self._pipe(up_r, writer))
        self.writers -= {writer, up_w}

    async def start(self):
        self.server = await asyncio.start_server(self._client, "127.0.0.1", self.port or 0, reuse_address=True)
        self.port = self.server.sockets[0].getsockname()[1]

    async def stop(self):
        self.server.close()
        for w in list(self.writers):
            w.close()
        self.writers.clear()
        await self.server.wait_closed()


class Agent:
    """Simüle ajan: /ws/agent bağlantısı, gelen komutlar inbox'ta."""

    def __init__(self, base, pc):
        self.base, self.pc, self.inbox, self.ws, self.reader = base, pc, [], None, None

    async def connect(self):
        headers = {"X-Agent-Secret": SECRETS[self.pc], "X-Agent-Version": "0.1.21-alpha"}
        self.ws = await _connect(self.base + "/ws/agent/" + self.pc, headers)
        hw = {"uuid": self.pc + "-U", "bios_sn": self.pc + "-B", "disk_sn": self.pc + "-D",
              "mac": "AA:BB:CC:00:00:%s" % self.pc[-2:], "ram_sn": self.pc + "-R"}
        await self.send({"dna_payload": {"hardware": hw, "capabilities": {"ram_readable": True}},
                         "hostname": self.pc.lower(), "status": "Online", "active_window": "-"})
        self.reader = asyncio.ensure_future(self._read())
        return self

    async def _read(self):
        try:
            async for msg in self.ws:
                self.inbox.append(json.loads(msg))
        except Exception:
            pass

    async def send(self, msg):
        await self.ws.send(json.dumps(msg))

    async def wait(self, pred, timeout=8.0):
        return await _wait(self.inbox, pred, timeout)

    async def close(self):
        await self.ws.close()
        if self.reader:
            await asyncio.wait([self.reader], timeout=3)


class Panel:
    def __init__(self, base, token, path="/ws/panel"):
        self.base, self.token, self.path, self.inbox, self.ws, self.reader = base, token, path, [], None, None

    async def connect(self):
        self.ws = await _connect(self.base + self.path, {"Cookie": "pops_jwt=" + self.token})
        self.reader = asyncio.ensure_future(self._read())
        return self

    async def _read(self):
        try:
            async for msg in self.ws:
                # İkili mesaj: Vision v2 karesi (0x01, kimlik uzunluğu, kimlik, ajanın karesi)
                self.inbox.append({"_binary": msg} if isinstance(msg, bytes) else json.loads(msg))
        except Exception:
            pass

    async def wait(self, pred, timeout=8.0):
        return await _wait(self.inbox, pred, timeout)

    async def close(self):
        await self.ws.close()


async def _connect(uri, headers):
    try:
        return await websockets.connect(uri, additional_headers=headers, max_size=None, open_timeout=10)
    except TypeError:   # websockets < 14
        return await websockets.connect(uri, extra_headers=headers, max_size=None, open_timeout=10)


async def _wait(inbox, pred, timeout):
    end = time.monotonic() + timeout
    while time.monotonic() < end:
        for m in inbox:
            if pred(m):
                return m
        await asyncio.sleep(0.05)
    return None


async def eventually(fn, timeout=10.0):
    end = time.monotonic() + timeout
    while True:
        out = await fn()
        if out or time.monotonic() > end:
            return out
        await asyncio.sleep(0.25)


def start_worker(port, env, logdir):
    log = open(os.path.join(logdir, "worker-%d.log" % port), "w")
    return subprocess.Popen(
        [sys.executable, "-m", "uvicorn", "server:app", "--host", "127.0.0.1", "--port", str(port)],
        cwd=BACKEND, env=env, stdout=log, stderr=subprocess.STDOUT,
    )


def stop_worker(proc):
    if proc.poll() is None:
        proc.send_signal(signal.SIGTERM)
        try:
            proc.wait(15)
        except subprocess.TimeoutExpired:
            proc.kill()
            proc.wait(5)


async def wait_healthy(base):
    for _ in range(120):
        try:
            if (await areq(base, "/api/health"))[0] == 200:
                return True
        except Exception:
            pass
        await asyncio.sleep(0.5)
    return False


async def db():
    return await asyncpg.connect(
        host=os.environ.get("DB_HOST", "localhost"), port=int(os.environ.get("DB_PORT", "5432")),
        user=os.environ["DB_USER"], password=os.environ["DB_PASS"], database=os.environ["DB_NAME"],
    )


async def seed(c):
    for u in ("haadmin1", "haadmin2"):
        await c.execute(
            "INSERT INTO users (username,password_hash,role,permissions,token_version) VALUES ($1,'x','admin','[]',0) "
            "ON CONFLICT (username) DO UPDATE SET role='admin', token_version=0", u)
    for pc, secret in SECRETS.items():
        await c.execute("DELETE FROM agent_secrets WHERE pc_name = $1", pc)
        await c.execute("INSERT INTO agent_secrets (pc_name, secret_hash) VALUES ($1, $2)",
                        pc, hashlib.sha256(secret.encode()).hexdigest())
        # Donanım envanteri var: bağlantıda get_hardware istenmez (gelen kutusu yalnızca testin komutları)
        await c.execute("INSERT INTO hw_inventory (pc_name, cpu) VALUES ($1, 'ha-cpu') ON CONFLICT (pc_name) "
                        "DO UPDATE SET cpu = 'ha-cpu'", pc)


HA_LAB, HA_UNITS = "HA-Kapsam-Lab", ("HA Birim A", "HA Birim B")


async def cleanup(c):
    pcs = list(SECRETS)
    await c.execute("DELETE FROM users WHERE username IN ('hascope_in', 'hascope_out')")
    await c.execute("DELETE FROM custom_labs WHERE lab_name = $1", HA_LAB)
    await c.execute("DELETE FROM org_units WHERE name = ANY($1::text[])", list(HA_UNITS))
    for table, col in (("clients", "pc_name"), ("tasks", "target_pc"), ("agent_versions", "pc_name"),
                       ("agent_secrets", "pc_name"), ("hw_inventory", "pc_name"), ("pending_updates", "pc_name"),
                       ("agent_bypass_keys", "pc_name")):
        await c.execute("DELETE FROM %s WHERE %s = ANY($1::text[])" % (table, col), pcs)
    await c.execute("DELETE FROM users WHERE username IN ('haadmin1', 'haadmin2')")


async def main():
    relay = RedisRelay()
    await relay.start()
    tmp = tempfile.mkdtemp(prefix="pops-ha-")
    for d in ("selfupdate", "state"):
        os.makedirs(os.path.join(tmp, d))
    env = dict(os.environ)
    env.update({
        "REDIS_URL": "redis://127.0.0.1:%d/%s" % (relay.port, (REDIS.path or "/0").lstrip("/") or "0"),
        "REDIS_PREFIX": "pops-ha-test-" + secrets.token_hex(4),
        "PANEL_ADMIN_USER": "haroot", "PANEL_ADMIN_PASS": ADMIN_PASS, "CORS_ALLOWED_ORIGINS": "",
        "POPS_SELFUPDATE_DIR": os.path.join(tmp, "selfupdate"), "POPS_STATE_DIR": os.path.join(tmp, "state"),
        "POPS_SELFUPDATE_CONF": os.path.join(tmp, "selfupdate.conf"), "TLS_CERT_FILES": "", "DISK_CHECK_PATHS": "",
        "WOL_BROADCAST_ADDR": "127.0.0.1", "LOG_FORMAT": "text", "HEARTBEAT_FLUSH_SECONDS": "1",
        "POPS_FILES_DIR": os.path.join(tmp, "transfers"),
    })
    env.pop("POPS_TEST_REDIS", None)
    c = await db()
    await cleanup(c)
    await seed(c)
    procs = [start_worker(p, env, tmp) for p in PORTS]
    try:
        up = [await wait_healthy(A), await wait_healthy(B)]
        chk(all(up), "iki backend süreci ayakta (%s)" % up)
        if all(up):
            await run(c, relay)
    finally:
        for p in procs:
            stop_worker(p)
        await cleanup(c)
        await c.close()
        await relay.stop()
        if FAILS:
            for p in PORTS:
                print("---- worker %d (son satırlar)" % p)
                with open(os.path.join(tmp, "worker-%d.log" % p)) as f:
                    print("".join(f.readlines()[-25:]))
    if FAILS:
        print("\nBASARISIZ: %d kontrol" % len(FAILS))
    else:
        print("\nTUM HA TESTLERI GECTI")


async def run(c, relay):
    admin1 = server.create_jwt("haadmin1", "admin", 0)
    admin2 = server.create_jwt("haadmin2", "admin", 0)

    print("== süreçler birbirini görüyor")
    root = server.create_jwt("haroot", "superadmin", 0)

    async def two_workers():
        s, diag = await areq(A, "/api/system/diagnostics", root)
        cl = diag.get("cluster") or {}
        return cl if s == 200 and cl.get("redis_ok") and len(cl.get("workers") or []) == 2 else None
    cl = await eventually(two_workers)
    chk(bool(cl), "A iki süreci görüyor (%s)" % ((cl or {}).get("workers")))

    print("== istek sınırı süreçler arasında ortak")
    codes = []
    for i in range(12):
        codes.append((await areq(A if i % 2 else B, "/api/admin/login",
                                 body={"username": "ha-yok-%d" % i, "password": "x"}))[0])
    chk(codes[:10] == [401] * 10 and 429 in codes[10:], "10/dk sınırı iki süreçte birlikte sayıldı (%s)" % codes)

    print("== A'daki ajana B üzerinden komut")
    agent = await Agent(WA, PC1).connect()
    # Bağlantı server_info'dan önce kaydedilir (çevrimiçi kaydı ve cihazın kanalı): ayrıca beklemek gerekmez
    chk(await agent.wait(lambda m: m.get("action") == "server_info") is not None, "ajan A'ya bağlandı")
    s, r = await areq(B, "/api/security/lockdown", admin1, {"target_pc": PC1, "reason": "HA testi"})
    chk(s == 200 and r.get("delivered") is True, "B: karantina 'iletildi' (%s)" % r)
    chk(await agent.wait(lambda m: m.get("action") == "lockdown" and m.get("reason") == "HA testi") is not None,
        "komut B'den A'daki ajana ulaştı")
    s, r = await areq(B, "/api/security/unlock", admin1, {"target_pc": PC1, "reason": "HA testi bitti"})
    chk(await agent.wait(lambda m: m.get("action") == "unlock") is not None, "karantina kaldırma da ulaştı")

    print("== B'deki panel A'daki ajanın heartbeat'ini ve durumunu görüyor")
    panel_b = await Panel(WB, admin1).connect()
    await asyncio.sleep(0.3)
    await agent.send({"status": "Online", "active_window": "ha-pencere-1", "hostname": PC1.lower()})
    row = await eventually(lambda: _device_on(B, admin1, PC1, lambda d: d.get("active_window") == "ha-pencere-1"))
    chk(bool(row) and row.get("status") == "Online", "heartbeat B'nin cihaz listesinde")
    await agent.send({"type": "capabilities", "terminal_enabled": True, "vision_enabled": True})
    cap = await panel_b.wait(lambda m: m.get("type") == "capabilities" and m.get("pc_name") == PC1)
    chk(cap is not None, "durum değişikliği (capabilities) B'deki panele yayıldı")

    print("== Vision: kareler yalnızca oturum sahibinin B'deki paneline")
    panel_b2 = await Panel(WB, admin2).connect()
    panel_a2 = await Panel(WA, admin2).connect()
    s, r = await areq(A, "/api/audit/session/start", admin1,
                      {"target_pc": PC1, "reason": "HA testi", "is_mandatory": False})
    chk(s == 200 and r.get("session_id"), "oturum A üzerinden açıldı")
    chk(await agent.wait(lambda m: m.get("action") == "start_vision_session") is not None, "ajana oturum isteği")
    vision = await _connect(WA + "/ws/vision/" + PC1, {"X-Agent-Secret": SECRETS[PC1]})
    vision_in = []
    vision_reader = asyncio.ensure_future(_collect(vision, vision_in))
    await asyncio.sleep(0.5)
    frame = {"type": "stream_frame", "hw_id": PC1, "image": "aGEtdGVzdA==", "seq": 1}
    await vision.send(json.dumps(frame))
    got = await panel_b.wait(lambda m: m.get("type") == "stream_frame")
    chk(got is not None and got.get("image") == "aGEtdGVzdA==", "kare A'dan oturum sahibinin B'deki paneline ulaştı")
    await asyncio.sleep(0.5)
    chk(not any(m.get("type") == "stream_frame" for m in panel_b2.inbox + panel_a2.inbox),
        "oturumu olmayan admin'in panellerine (A ve B) kare gitmedi (F12)")
    await panel_b.ws.send(json.dumps({"type": "remote_input", "device": PC1, "input_type": "mouse_move",
                                      "x": 5, "y": 7}))
    chk(await _wait(vision_in, lambda m: m.get("input_type") == "mouse_move", 8) is not None,
        "B'deki panelin uzaktan girdisi A'daki Vision tüneline ulaştı")
    s, r = await areq(B, "/api/audit/session/end", admin1, {"session_id": r.get("session_id"), "status": "Completed"})
    await asyncio.sleep(0.5)
    n_before = sum(1 for m in panel_b.inbox if m.get("type") == "stream_frame")
    await vision.send(json.dumps(dict(frame, seq=2)))
    await asyncio.sleep(1.0)
    n_after = sum(1 for m in panel_b.inbox if m.get("type") == "stream_frame")
    chk(s == 200 and n_after == n_before, "oturum B'de kapanınca A'daki kareler artık gitmiyor")
    vision_reader.cancel()
    await vision.close()
    await panel_b2.close()
    await panel_a2.close()

    print("== Vision v2: ikili kareler, monitörler, görüntüleyici komutu ve pano süreçler arasında")
    panel_b2 = await Panel(WB, admin2).connect()
    for p in (panel_b, panel_b2):
        await p.ws.send(json.dumps({"type": "panel_hello", "features": ["vision_binary"]}))
    s, r = await areq(B, "/api/audit/session/start", admin1,
                      {"target_pc": PC1, "reason": "HA v2", "is_mandatory": False})
    vision = await _connect(WA + "/ws/vision/" + PC1, {"X-Agent-Secret": SECRETS[PC1]})
    vision_in = []
    vision_reader = asyncio.ensure_future(_collect(vision, vision_in))
    await asyncio.sleep(0.8)
    jpeg = b"\xff\xd8ha-v2-kare"
    raw = struct.pack(">BBIHHHHHH", 1, 0, 1, 0, 0, 64, 48, 64, 48) + jpeg
    await vision.send(raw)
    prefix = bytes((1, len(PC1))) + PC1.encode()
    got = await panel_b.wait(lambda m: "_binary" in m)
    chk(got is not None and got["_binary"] == prefix + raw,
        "ikili kare A'dan oturum sahibinin B'deki paneline, tünelin kimliğiyle ulaştı")
    await vision.send(json.dumps({"type": "monitors", "list": [{"index": 0, "width": 64, "height": 48,
                                                                "primary": True}]}))
    mon = await panel_b.wait(lambda m: m.get("type") == "monitors" and m.get("hw_id") == PC1)
    chk(mon is not None and mon["list"][0]["width"] == 64, "monitör listesi oturum sahibinin B'deki paneline")
    await asyncio.sleep(0.5)
    chk(not any("_binary" in m or m.get("type") == "monitors" for m in panel_b2.inbox),
        "oturumu olmayan admin'in B'deki paneline ikili kare ve monitör gitmedi (F12)")
    await panel_b.ws.send(json.dumps({"type": "vision_control", "device": PC1, "action": "select_monitor",
                                      "index": 0}))
    chk(await _wait(vision_in, lambda m: m.get("action") == "select_monitor", 8) is not None,
        "B'deki panelin görüntüleyici komutu A'daki tünele ulaştı")
    await panel_b.ws.send(json.dumps({"type": "vision_control", "device": PC1, "action": "clipboard",
                                      "text": "ha-pano"}))
    res = await panel_b.wait(lambda m: m.get("type") == "clipboard_result")
    chk(res is not None and res.get("ok") is True, "B'deki panelin panosu: sonuç ok (%s)" % res)
    chk(await _wait(vision_in, lambda m: m.get("action") == "clipboard" and m.get("text") == "ha-pano", 8)
        is not None, "pano metni A'daki tünele ulaştı")
    await vision.send(json.dumps({"type": "clipboard", "text": "bilgisayardan"}))
    clip = await panel_b.wait(lambda m: m.get("type") == "clipboard" and m.get("text") == "bilgisayardan")
    chk(clip is not None, "bilgisayarın panosu oturum sahibinin B'deki paneline ulaştı")
    await areq(B, "/api/audit/session/end", admin1, {"session_id": r.get("session_id"), "status": "Completed"})
    vision_reader.cancel()
    await vision.close()
    await panel_b2.close()

    print("== kurum birimi kapsamı süreçler arasında")
    unit_in = await c.fetchval("INSERT INTO org_units (name) VALUES ($1) RETURNING id", HA_UNITS[0])
    unit_out = await c.fetchval("INSERT INTO org_units (name) VALUES ($1) RETURNING id", HA_UNITS[1])
    await c.execute("INSERT INTO custom_labs (lab_name, org_unit_id) VALUES ($1, $2)", HA_LAB, unit_in)
    await c.execute("UPDATE clients SET lab_name = $1 WHERE pc_name = $2", HA_LAB, PC1)
    for user, unit in (("hascope_in", unit_in), ("hascope_out", unit_out)):
        await c.execute(
            "INSERT INTO users (username,password_hash,role,permissions,token_version,org_scope) "
            "VALUES ($1,'x','admin','[]',0,$2) ON CONFLICT (username) DO UPDATE SET org_scope = $2", user, [unit])
    p_in = await Panel(WB, server.create_jwt("hascope_in", "admin", 0)).connect()
    p_out = await Panel(WB, server.create_jwt("hascope_out", "admin", 0)).connect()
    await asyncio.sleep(0.5)
    await agent.send({"type": "capabilities", "terminal_enabled": True, "vision_enabled": True})
    got_in = await p_in.wait(lambda m: m.get("type") == "capabilities" and m.get("pc_name") == PC1)
    await asyncio.sleep(0.5)
    chk(got_in is not None, "A'daki kapsamdaki cihazın yayını B'deki kapsamlı panele gitti")
    chk(not any(m.get("type") == "capabilities" for m in p_out.inbox),
        "B'deki başka birimin paneline gitmedi (süzgeç paneli tutan süreçte)")
    await p_in.close()
    await p_out.close()

    print("== cihaz listesi sürümü iki süreçte aynı")
    panel_dev = await Panel(WB, admin1, "/ws/panel?topics=devices").connect()
    await asyncio.sleep(1.5)
    s, full = await areq(B, "/api/devices?since=0", admin1)
    v0 = full.get("version")
    chk(s == 200 and full.get("full") is True and isinstance(v0, int), "B tam liste ve sürüm döndü (%s)" % v0)
    await agent.send({"type": "capabilities", "terminal_enabled": False, "vision_enabled": True})
    changed = await panel_dev.wait(lambda m: m.get("type") == "devices_changed" and m.get("version", 0) > v0)
    chk(changed is not None, "A'daki değişiklik B'deki cihaz listesi paneline bildirildi")
    s, d = await areq(B, "/api/devices?since=%d" % v0, admin1)
    rows = [x for x in d.get("changed") or [] if x.get("hw_id") == PC1]
    chk(s == 200 and d.get("full") is False and rows and rows[0].get("cap_terminal_enabled") is False,
        "B'nin ?since= yanıtı A'nın değişikliğini taşıyor")
    tag_a, tag_b = (_etag(x, admin1) for x in (A, B))
    chk(tag_a and tag_a == tag_b, "ETag iki süreçte aynı (%s / %s)" % (tag_a, tag_b))
    await panel_dev.close()

    print("== görev: B'de oluşturulur, A'daki ajan çalıştırır, sonuç B'ye")
    s, r = await areq(B, "/api/deploy_orchestration", admin1, {"target_mode": "PC", "targets": [PC1], "taskSequence": [
        {"name": "ha", "type": "CMD", "command": "echo ha-gorev"}]})
    task_ids = r.get("task_ids") or []
    chk(s == 200 and len(task_ids) == 1, "görev B'de oluşturuldu (%s)" % r)
    exe = await agent.wait(lambda m: m.get("action") == "execute" and m.get("task_id") in task_ids)
    chk(exe is not None, "görev B'nin kuyruğundan A'daki ajana gönderildi")
    if exe:
        await agent.send({"type": "result", "task_id": exe["task_id"], "output": "ha-cikti", "exit_code": 0,
                          "hostname": PC1.lower()})
        out = await panel_b.wait(lambda m: m.get("type") == "terminal_output" and m.get("task_id") == exe["task_id"])
        chk(out is not None and out.get("output") == "ha-cikti", "sonuç B'deki panele yayıldı")
        st = await eventually(lambda: _task_status(B, admin1, exe["task_id"], "Completed"))
        chk(st, "işlem merkezi (B: /api/tasks/status) görevi Completed gösteriyor")

    print("== güncelleme adımı A'da, B'nin update-progress yanıtında")
    await c.execute("INSERT INTO pending_updates (pc_name, version, sent_at) VALUES ($1, '9.9.9', NOW()) "
                    "ON CONFLICT (pc_name) DO UPDATE SET version = '9.9.9', sent_at = NOW(), stage = NULL", PC1)
    await agent.send({"type": "update_progress", "stage": "downloaded", "to_version": "9.9.9"})

    async def stage_on_b():
        s, r = await areq(B, "/api/system/update-progress", admin1, {"pcs": [PC1], "version": "9.9.9", "since": 0})
        items = r.get("items") or [{}]
        return items[0] if items[0].get("stage") == "downloaded" else None
    item = await eventually(stage_on_b)
    chk(bool(item) and item.get("pending") is True and item.get("online") is True,
        "B, A'ya bildirilen adımı görüyor (%s)" % item)

    print("== ajan B'ye yeniden bağlanınca A'daki kayıt devredilir")
    agent2 = await Agent(WA, PC2).connect()
    chk(await agent2.wait(lambda m: m.get("action") == "server_info") is not None, "ikinci ajan A'da")
    moved = await Agent(WB, PC2).connect()   # eski bağlantı A'da açık kalırken
    chk(await moved.wait(lambda m: m.get("action") == "server_info") is not None, "aynı cihaz B'ye bağlandı")
    await asyncio.sleep(0.5)
    s, r = await areq(A, "/api/security/lockdown", admin1, {"target_pc": PC2, "reason": "devir"})
    chk(await moved.wait(lambda m: m.get("action") == "lockdown") is not None
        and not any(m.get("action") == "lockdown" for m in agent2.inbox),
        "A'dan verilen komut cihazın B'deki yeni bağlantısına gitti, eskisine değil")
    await agent2.close()
    await asyncio.sleep(1.0)
    still = await _device_on(A, admin1, PC2, lambda d: True)
    chk(bool(still) and still.get("status") == "Online", "eski bağlantının kapanması cihazı Offline yapmadı")
    await areq(A, "/api/security/unlock", admin1, {"target_pc": PC2, "reason": "devir bitti"})
    await moved.close()

    print("== Redis durdu: A kendi ajanı ve paneliyle çalışıyor")
    panel_a = await Panel(WA, admin1).connect()
    await relay.stop()
    await asyncio.sleep(1.5)
    s, r = await areq(A, "/api/security/lockdown", admin1, {"target_pc": PC1, "reason": "redis yok"})
    chk(s == 200 and r.get("delivered") is True, "A: komut yerel ajana iletildi (%s)" % r)
    chk(await agent.wait(lambda m: m.get("action") == "lockdown" and m.get("reason") == "redis yok") is not None,
        "A'daki ajan komutu aldı")
    await agent.send({"type": "capabilities", "terminal_enabled": True, "vision_enabled": False})
    chk(await panel_a.wait(lambda m: m.get("type") == "capabilities" and m.get("vision_enabled") is False)
        is not None, "A'daki panel A'daki ajanın yayınını aldı")
    s, r = await areq(B, "/api/security/unlock", admin1, {"target_pc": PC1, "reason": "redis yok"})
    chk(s == 200 and r.get("delivered") is False, "B: başka süreçteki ajana ulaşılamadığı bildirildi, hata yok")
    late = await Agent(WA, PC2).connect()
    chk(await late.wait(lambda m: m.get("action") == "server_info") is not None, "Redis yokken yeni ajan A'ya bağlandı")
    s, r = await areq(A, "/api/admin/login", body={"username": "haroot", "password": ADMIN_PASS})
    chk(s == 200 and r.get("token"), "giriş çalışıyor (istek sınırı belleğe döndü; %s)" % s)
    await agent.send({"status": "Online", "active_window": "ha-pencere-2", "hostname": PC1.lower()})
    row = await eventually(lambda: _device_on(A, admin1, PC1, lambda d: d.get("active_window") == "ha-pencere-2"))
    chk(bool(row), "heartbeat Redis yokken de yazıldı")

    print("== Redis geri geldi: süreçler arası yol yeniden açık")
    unlocks = sum(1 for m in agent.inbox if m.get("action") == "unlock")
    await relay.start()

    async def delivered_via_b():
        s, r = await areq(B, "/api/security/unlock", admin1, {"target_pc": PC1, "reason": "redis geri"})
        return r.get("delivered") is True
    chk(await eventually(delivered_via_b, 20), "B'nin komutu yeniden A'daki ajana iletiliyor")
    got = await eventually(lambda: _count(agent.inbox, "unlock", unlocks + 1), 5)
    chk(got, "ajan B'nin komutunu aldı")
    chk(await eventually(lambda: _delivered(B, admin1, PC2), 15), "Redis yokken A'ya bağlanan ajan kayda yazıldı")
    for x in (late, agent, panel_a, panel_b):
        await x.close()
    await asyncio.sleep(1.5)
    row = await _device_on(B, admin1, PC1, lambda d: True)
    chk(bool(row) and row.get("status") == "Offline", "ajan kopunca cihaz Offline")


def _etag(base, token):
    r = urllib.request.Request(base + "/api/devices", headers={"Authorization": "Bearer " + token})
    with urllib.request.urlopen(r, timeout=20) as resp:
        return resp.headers.get("ETag")


async def _count(inbox, action, at_least):
    return sum(1 for m in inbox if m.get("action") == action) >= at_least


async def _collect(ws, inbox):
    try:
        async for msg in ws:
            inbox.append(json.loads(msg))
    except Exception:
        pass


async def _delivered(base, token, pc):
    """Bu süreç üzerinden cihaza komut iletilebiliyor mu (karantina kaldırma; ajan zaten kilitsiz)."""
    s, r = await areq(base, "/api/security/unlock", token, {"target_pc": pc, "reason": "erişim denemesi"})
    return s == 200 and r.get("delivered") is True


async def _device_on(base, token, pc, pred):
    s, devices = await areq(base, "/api/devices", token)
    for d in devices or []:
        if d.get("hw_id") == pc and pred(d):
            return d
    return None


async def _task_status(base, token, task_id, status):
    s, r = await areq(base, "/api/tasks/status", token, {"ids": [task_id]})
    return any(i.get("status") == status for i in r.get("items", []))


def test_ha():
    if not os.environ.get("POPS_TEST_REDIS"):
        pytest.skip("POPS_TEST_REDIS tanımlı değil: bir Redis gerekir (docs/testing.md)")
    asyncio.run(main())
    assert not FAILS, "%d kontrol düştü: %s" % (len(FAILS), "; ".join(FAILS[:5]))


if __name__ == "__main__":
    if not os.environ.get("POPS_TEST_REDIS"):
        sys.exit("POPS_TEST_REDIS tanımlı değil: bir Redis gerekir (docs/testing.md)")
    asyncio.run(main())
    sys.exit(1 if FAILS else 0)
