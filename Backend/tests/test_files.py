"""Dosya aktarımı — entegrasyon testi (CI 'security' job'ı).

Sahte ajanlar /ws/agent'a anahtarlarıyla bağlanır, capabilities iletisinde files_enabled bildirir; dosyayı HTTP ile
anahtarlarıyla indirir/yükler.

- Gönderme (push): yükleme -> ajana file_push (sözleşmedeki alanlar) -> tek kullanımlık jetonla indirme; ikinci
  indirme, süresi dolmuş jeton, yanlış jeton ve başka bilgisayarın anahtarı 404, anahtarsız 401; file_result satırı
  günceller, başka bilgisayarın sonucu güncellemez; gönderilen dosya jetonlar bitince silinir.
- Çalıştırılabilir tür (.lnk) yalnızca allow_exec ile; dosya adı temizlenir; "[REDDEDİLDİ]" ret sayılır.
- Alma (pull): istek -> file_pull -> yükleme (ham gövde ve multipart), boyut sınırı (413), SHA-256 başlığı, admin
  indirmesi her zaman ek ve nosniff; geçersiz yollar (ağ yolu, '..', akış, joker) 400.
- Roller: viewer 403, admin any_profile 403, superadmin any_profile geçer; API jetonu (admin) gönderemez ve alamaz.
- Çevrimdışı, eski ajan (files_enabled yok), bilgisayarda kapalı ve kayıtsız bilgisayar atlanır; capability_denied
  aktarımı reddeder ve yeteneği kapalı yazar; modül kapatılınca açık aktarımlar iptal olur.
- Denetim kaydı: gönderme, istek, alınan dosya, indirme ve sonuç yazılır; dosyanın içeriği yazılmaz.
- Saklama: alınan dosya 7 günden eskiyse silinir (satır kalır, indirme 404), daha yenisi kalır.

Bazı adımlar sunucunun modüllerini bu süreçte, aynı veritabanına bağlanarak çağırır (temizlik). Sunucu ile test
aynı Backend klasörünü (aynı FILES_DIR) kullanmalıdır. Ortam: POPS_TEST_HTTP + DB_* + JWT_SECRET.
"""

import asyncio
import hashlib
import http.client
import json
import os
import secrets
import sys
import urllib.error
import urllib.parse
import urllib.request

sys.path.insert(0, os.path.join(os.path.dirname(__file__), os.pardir))
import asyncpg  # noqa: E402
import websockets  # noqa: E402

import server  # noqa: E402  (create_jwt)
from pops import db, filestore  # noqa: E402
from pops.config import FILES_DIR  # noqa: E402

HTTP = os.environ["POPS_TEST_HTTP"]
WS = os.environ.get("POPS_TEST_WS", HTTP.replace("http://", "ws://").replace("https://", "wss://"))
ONLINE = ["HW-FX1", "HW-FX2", "HW-FXOLD", "HW-FXNO"]
PCS = ONLINE + ["HW-FXOFF"]
SECRET = {pc: pc.lower() + "-secret-0123456789" for pc in PCS}
MARK = b"POPS-FILE-CONTENT-MARKER-7f3a"
FAILS = []
PUSH_KEYS = {"action", "transfer_id", "name", "size", "sha256", "url", "dest", "reason", "allow_exec"}
PULL_KEYS = {"action", "transfer_id", "path", "max_size", "upload", "reason", "any_profile"}


def chk(cond, msg):
    print(("  OK   " if cond else "  FAIL ") + msg)
    if not cond:
        FAILS.append(msg)


def _sha(b):
    return hashlib.sha256(b).hexdigest()


def http_req(method, path, token=None, data=None, headers=None):
    """(durum, gövde baytları, başlıklar)."""
    r = urllib.request.Request(HTTP + path, data=data, method=method)
    for k, v in (headers or {}).items():
        r.add_header(k, v)
    if token:
        r.add_header("Authorization", "Bearer " + token)
    try:
        with urllib.request.urlopen(r, timeout=40) as resp:
            return resp.status, resp.read(), resp.headers
    except urllib.error.HTTPError as e:
        return e.code, e.read(), e.headers


def jreq(path, token=None, body=None, method=None):
    data = json.dumps(body).encode() if body is not None else None
    s, raw, _h = http_req(method or ("POST" if body is not None else "GET"), path, token, data,
                          {"Content-Type": "application/json"} if body is not None else None)
    try:
        return s, json.loads(raw or b"{}")
    except ValueError:
        return s, {}


def multipart(fields, files):
    boundary = "----popstest" + secrets.token_hex(8)
    out = bytearray()
    for k, v in fields:
        out += ("--%s\r\nContent-Disposition: form-data; name=\"%s\"\r\n\r\n" % (boundary, k)).encode()
        out += str(v).encode() + b"\r\n"
    for k, fname, content in files:
        out += ("--%s\r\nContent-Disposition: form-data; name=\"%s\"; filename=\"%s\"\r\n"
                "Content-Type: application/octet-stream\r\n\r\n" % (boundary, k, fname)).encode()
        out += content + b"\r\n"
    out += ("--%s--\r\n" % boundary).encode()
    return bytes(out), "multipart/form-data; boundary=" + boundary


def push(token, pcs, content, name="Ödev 1.pdf", dest="inbox", reason="Ders materyali", allow_exec=None):
    fields = [("pcs", p) for p in pcs] + [("dest", dest), ("reason", reason)]
    if allow_exec is not None:
        fields.append(("allow_exec", "true" if allow_exec else "false"))
    body, ctype = multipart(fields, [("file", name, content)] if content is not None else [])
    s, raw, _h = http_req("POST", "/api/files/push", token, body, {"Content-Type": ctype})
    try:
        return s, json.loads(raw or b"{}")
    except ValueError:
        return s, {}


def agent_headers(pc):
    return {"X-Agent-Id": pc, "X-Agent-Secret": SECRET[pc]}


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


class Agent:
    def __init__(self, pc, ws):
        self.pc, self.ws, self.msgs = pc, ws, []
        self.reader = asyncio.ensure_future(self._read())

    async def _read(self):
        try:
            async for raw in self.ws:
                try:
                    self.msgs.append(json.loads(raw))
                except ValueError:
                    pass
        except websockets.exceptions.ConnectionClosed:
            pass

    async def wait(self, pred, timeout=8.0):
        for _ in range(int(timeout / 0.1)):
            for m in self.msgs:
                if isinstance(m, dict) and pred(m):
                    self.msgs.remove(m)
                    return m
            await asyncio.sleep(0.1)
        return None

    async def send(self, obj):
        await self.ws.send(json.dumps(obj))

    async def close(self):
        await self.ws.close()
        self.reader.cancel()


async def connect(pc, files_enabled):
    ws = await websockets.connect("%s/ws/agent/%s" % (WS, pc),
                                  additional_headers={"X-Agent-Secret": SECRET[pc], "X-Agent-Version": "0.1.30-alpha"})
    a = Agent(pc, ws)
    await a.send(json.loads(dna(pc)))
    caps = {"type": "capabilities", "terminal_enabled": True, "vision_enabled": True}
    if files_enabled is not None:
        caps["files_enabled"] = files_enabled
    a.info = await a.wait(lambda m: m.get("action") == "server_info", 5) or {}
    await a.send(caps)
    return a


async def wait_for(c, sql, *args, timeout=8):
    for _ in range(int(timeout / 0.2)):
        value = await c.fetchval(sql, *args)
        if value:
            return value
        await asyncio.sleep(0.2)
    return await c.fetchval(sql, *args)


def blob_exists(name):
    return bool(name) and os.path.exists(os.path.join(FILES_DIR, name))


async def cleanup(c):
    for r in await c.fetch("SELECT storage_path FROM file_transfers WHERE pc_name = ANY($1::text[]) "
                           "AND storage_path IS NOT NULL", PCS):
        filestore.remove_blob(r["storage_path"])
    for table, col in (("file_transfers", "pc_name"), ("agent_secrets", "pc_name"), ("agent_bypass_keys", "pc_name"),
                       ("clients", "pc_name"), ("agent_versions", "pc_name"), ("hw_inventory", "pc_name"),
                       ("tasks", "target_pc")):
        await c.execute("DELETE FROM %s WHERE %s = ANY($1::text[])" % (table, col), PCS)
    await c.execute("DELETE FROM module_settings WHERE module_id = 'files'")
    await c.execute("DELETE FROM api_tokens WHERE name = 'fx-token'")


async def conn():
    return await asyncpg.connect(
        host=os.environ.get("DB_HOST", "localhost"),
        port=int(os.environ.get("DB_PORT", "5432")),
        user=os.environ["DB_USER"],
        password=os.environ["DB_PASS"],
        database=os.environ["DB_NAME"],
    )


async def main():
    c = await conn()
    old_enforce = await c.fetchval("SELECT value FROM global_settings WHERE key='enforce_agent_auth'")
    await c.execute("INSERT INTO global_settings (key,value) VALUES ('enforce_agent_auth','1') "
                    "ON CONFLICT (key) DO UPDATE SET value='1'")
    await cleanup(c)
    for pc in PCS:
        await c.execute("INSERT INTO agent_secrets (pc_name, secret_hash) VALUES ($1,$2)", pc,
                        _sha(SECRET[pc].encode()))
    await c.execute("INSERT INTO clients (pc_name, hostname, lab_name, status, cap_files_enabled) "
                    "VALUES ('HW-FXOFF', 'hw-fxoff', 'Atanmamis_Cihazlar', 'Offline', TRUE)")
    for u, role in (("fxadmin", "admin"), ("fxsuper", "superadmin"), ("fxviewer", "viewer")):
        await c.execute(
            "INSERT INTO users (username,password_hash,role,permissions,token_version) VALUES ($1,'x',$2,'[]',0) "
            "ON CONFLICT (username) DO UPDATE SET role=$2, token_version=0",
            u, role,
        )
    tokens = {
        "admin": server.create_jwt("fxadmin", "admin", 0),
        "super": server.create_jwt("fxsuper", "superadmin", 0),
        "viewer": server.create_jwt("fxviewer", "viewer", 0),
    }
    db.db_pool = await asyncpg.create_pool(
        host=os.environ.get("DB_HOST", "localhost"), port=int(os.environ.get("DB_PORT", "5432")),
        user=os.environ["DB_USER"], password=os.environ["DB_PASS"], database=os.environ["DB_NAME"],
        min_size=1, max_size=4,
    )
    agents = {}
    try:
        agents["HW-FX1"] = await connect("HW-FX1", True)
        agents["HW-FX2"] = await connect("HW-FX2", True)
        agents["HW-FXOLD"] = await connect("HW-FXOLD", None)
        agents["HW-FXNO"] = await connect("HW-FXNO", False)
        await run(c, tokens, agents)
    finally:
        for a in agents.values():
            try:
                await a.close()
            except Exception:
                pass
        await asyncio.sleep(0.5)
        await cleanup(c)
        await c.execute("UPDATE global_settings SET value=$1 WHERE key='enforce_agent_auth'", old_enforce or "0")
        await db.db_pool.close()
        await c.close()
    if FAILS:
        print("\nBASARISIZ: %d kontrol" % len(FAILS))
        sys.exit(1)
    print("\nTUM DOSYA AKTARIMI TESTLERI GECTI")


async def run(c, tok, agents):
    admin, sup, viewer = tok["admin"], tok["super"], tok["viewer"]
    fx1, fx2 = agents["HW-FX1"], agents["HW-FX2"]

    print("== yetenek ve sunucu özelliği")
    chk(await wait_for(c, "SELECT cap_files_enabled FROM clients WHERE pc_name='HW-FX1'"), "files_enabled saklandı")
    await wait_for(c, "SELECT cap_files_enabled IS FALSE FROM clients WHERE pc_name='HW-FXNO'")
    s, devs = jreq("/api/devices", viewer)
    by = {d["hostname"]: d for d in devs} if s == 200 else {}
    chk(by.get("HW-FX1", {}).get("cap_files_enabled") is True
        and by.get("HW-FXNO", {}).get("cap_files_enabled") is False
        and "cap_files_enabled" in by.get("HW-FXOLD", {}) and by["HW-FXOLD"]["cap_files_enabled"] is None,
        "cihaz listesinde cap_files_enabled (açık / kapalı / desteklenmiyor)")
    chk("file_transfer" in (fx1.info.get("features") or []), "server_info.features: file_transfer")

    print("== gönderme: roller ve girdi denetimi")
    content = os.urandom(300_000) + MARK
    sha = _sha(content)
    chk(push(viewer, ["HW-FX1"], content)[0] == 403, "viewer gönderemez (403)")
    chk(push(admin, ["HW-FX1"], None)[0] == 400, "dosyasız istek 400")
    chk(push(admin, ["HW-FX1"], content, dest="C:\\Windows")[0] == 400, "izinli olmayan hedef 400")
    chk(push(admin, ["HW-FX1"], content, reason=" ")[0] == 400, "gerekçesiz 400")
    chk(push(admin, ["HW-FX1"], b"[InternetShortcut]", name="kisayol.lnk")[0] == 400, ".lnk allow_exec olmadan 400")
    chk(push(admin, [], content)[0] == 400, "hedefsiz 400")
    chk(push(admin, ["HW-FX1"], b"")[0] == 400, "boş dosya 400")
    # Gövdenin tamamı gönderilmeden: Content-Length sınırın üstündeyse hemen 413
    u = urllib.parse.urlparse(HTTP)
    hc = http.client.HTTPConnection(u.hostname, u.port, timeout=20)
    hc.putrequest("POST", "/api/files/push")
    hc.putheader("Authorization", "Bearer " + admin)
    hc.putheader("Content-Type", "multipart/form-data; boundary=x")
    hc.putheader("Content-Length", str(300 * 1024 * 1024))
    hc.endheaders()
    try:
        hc.send(b"--x\r\n")
        status = hc.getresponse().status
    except (ConnectionError, http.client.HTTPException):
        status = None
    hc.close()
    chk(status == 413, "200 MB'tan büyük gövde okunmadan 413 (%s)" % status)

    print("== gönderme: hedefler")
    s, r = push(admin, ["HW-FX1", "HW-FX2", "HW-FXOLD", "HW-FXOFF", "HW-FXNO", "HW-FXNONE"], content)
    chk(s == 200 and r.get("sha256") == sha and r.get("size") == len(content), "yüklendi, SHA-256 ve boyut döndü")
    sent = {t["pc_name"]: t["transfer_id"] for t in r.get("transfers", [])}
    skipped = {x["pc_name"]: x["reason"] for x in r.get("skipped", [])}
    chk(set(sent) == {"HW-FX1", "HW-FX2"}, "yalnız çevrimiçi ve özelliği açık bilgisayarlara gönderildi")
    chk(skipped == {"HW-FXOLD": "unsupported", "HW-FXOFF": "offline", "HW-FXNO": "disabled", "HW-FXNONE": "unknown"},
        "atlananlar ve sebepleri: %s" % skipped)
    m1 = await fx1.wait(lambda m: m.get("action") == "file_push")
    m2 = await fx2.wait(lambda m: m.get("action") == "file_push")
    chk(m1 is not None and set(m1) == PUSH_KEYS, "file_push sözleşmedeki alanlarla geldi")
    chk(m1 and m1["transfer_id"] == sent["HW-FX1"] and m1["name"] == "Ödev 1.pdf" and m1["size"] == len(content)
        and m1["sha256"] == sha and m1["dest"] == "inbox" and m1["reason"] == "Ders materyali"
        and m1["allow_exec"] is False, "file_push değerleri")
    url1 = m1["url"] if m1 else ""
    url2 = m2["url"] if m2 else ""
    chk(url1.startswith("/api/files/%s/download?t=" % sent.get("HW-FX1")), "indirme adresi kendi sunucusuna göreli")
    chk(url1 != url2 and m2 and m2["transfer_id"] != m1["transfer_id"], "her bilgisayarın kendi aktarımı ve jetonu")
    row = await c.fetchrow("SELECT * FROM file_transfers WHERE transfer_id=$1", sent.get("HW-FX1"))
    tok1 = urllib.parse.parse_qs(urllib.parse.urlparse(url1).query).get("t", [""])[0]
    chk(row and row["status"] == "sent" and row["token_hash"] == _sha(tok1.encode()) and tok1 not in str(dict(row)),
        "jeton yalnız özetiyle saklandı")
    chk(row and blob_exists(row["storage_path"]) and row["created_by"] == "fxadmin", "dosya sunucuda, kim gönderdi")

    print("== gönderme: tek kullanımlık indirme")
    chk(http_req("GET", url1)[0] == 401, "anahtarsız indirme 401")
    chk(http_req("GET", url1, headers=agent_headers("HW-FX2"))[0] == 404, "başka bilgisayarın anahtarıyla 404")
    bad = url1.split("?t=")[0] + "?t=" + secrets.token_urlsafe(32)
    chk(http_req("GET", bad, headers=agent_headers("HW-FX1"))[0] == 404, "yanlış jetonla 404")
    s, body, h = http_req("GET", url1, headers=agent_headers("HW-FX1"))
    chk(s == 200 and body == content, "doğru bilgisayar ve jetonla indirildi (jeton denemelerden etkilenmedi)")
    chk(h.get("X-Content-SHA256") == sha and h.get("X-Content-Type-Options") == "nosniff"
        and (h.get("Content-Type") or "").startswith("application/octet-stream"), "indirme başlıkları")
    chk(http_req("GET", url1, headers=agent_headers("HW-FX1"))[0] == 404, "ikinci indirme 404")
    chk(await c.fetchval("SELECT status FROM file_transfers WHERE transfer_id=$1", sent["HW-FX1"]) == "downloading",
        "durum: downloading")
    await fx2.send({"type": "file_result", "transfer_id": sent["HW-FX1"], "outcome": "done", "path": "C:\\x"})
    await asyncio.sleep(0.5)
    chk(await c.fetchval("SELECT status FROM file_transfers WHERE transfer_id=$1", sent["HW-FX1"]) == "downloading",
        "başka bilgisayarın file_result'ı satırı değiştirmedi")
    dest_path = "C:\\Users\\Public\\POps\\Gelen\\Ödev 1.pdf"
    await fx1.send({"type": "file_result", "transfer_id": sent["HW-FX1"], "outcome": "done", "path": dest_path})
    chk(await wait_for(c, "SELECT status='done' AND path=$2 AND finished_at IS NOT NULL FROM file_transfers "
                          "WHERE transfer_id=$1", sent["HW-FX1"], dest_path), "file_result satırı tamamladı")
    await c.execute("UPDATE file_transfers SET token_expires_at = NOW() - interval '1 second' WHERE transfer_id=$1",
                    sent["HW-FX2"])
    chk(http_req("GET", url2, headers=agent_headers("HW-FX2"))[0] == 404, "süresi dolmuş jetonla 404")
    await filestore.purge()
    chk(await c.fetchval("SELECT status FROM file_transfers WHERE transfer_id=$1", sent["HW-FX2"]) == "expired",
        "kullanılmayan jeton: expired")
    chk(blob_exists(row["storage_path"]), "son indirme yeniyken dosya duruyor")
    await c.execute("UPDATE file_transfers SET token_used_at = NOW() - interval '11 minutes' WHERE transfer_id=$1",
                    sent["HW-FX1"])
    await filestore.purge()
    chk(not blob_exists(row["storage_path"])
        and await c.fetchval("SELECT storage_path IS NULL FROM file_transfers WHERE transfer_id=$1", sent["HW-FX1"]),
        "jetonlar bitince gönderilen dosya silindi")

    print("== çalıştırılabilir tür ve dosya adı")
    s, r = push(admin, ["HW-FX1"], b"[InternetShortcut]\r\nURL=https://example.org/", name="kisayol.lnk",
                allow_exec=True)
    m = await fx1.wait(lambda m: m.get("action") == "file_push")
    chk(s == 200 and m and m["allow_exec"] is True and m["name"] == "kisayol.lnk", ".lnk allow_exec ile gönderildi")
    await fx1.send({"type": "file_result", "transfer_id": m["transfer_id"] if m else "", "outcome": "failed",
                    "detail": "[REDDEDİLDİ] Kısayol dosyası yazılmadı."})
    chk(await wait_for(c, "SELECT status='rejected' FROM file_transfers WHERE transfer_id=$1",
                       m["transfer_id"] if m else ""), "'[REDDEDİLDİ]' sonucu rejected sayıldı")
    s, r = push(admin, ["HW-FX1"], b"MZ" + MARK, name="..\\..\\Windows\\evil\u202egnp.exe")
    m = await fx1.wait(lambda m: m.get("action") == "file_push")
    chk(s == 200 and m and m["name"] == "evilgnp.exe",
        "yol parçaları ve yön karakteri atıldı (%s)" % (m or {}).get("name"))
    if m:
        await fx1.send({"type": "file_result", "transfer_id": m["transfer_id"], "outcome": "rejected",
                        "detail": "kullanıcı iptal etti"})
    s, r = push(admin, ["HW-FXOFF"], content)
    chk(s == 409 and "çevrimdışı" in str(r.get("detail")), "yalnız çevrimdışı hedef: 409")
    s, r = push(admin, ["HW-FXOLD"], content)
    chk(s == 409 and "desteklemiyor" in str(r.get("detail")), "eski ajan: 409")

    print("== alma: roller ve girdi denetimi")
    pth = "C:\\Users\\Public\\Documents\\rapor.pdf"
    good = {"pc": "HW-FX1", "path": pth, "max_size": 1000, "reason": "Sınav dosyası kontrolü"}
    chk(jreq("/api/files/pull", viewer, good)[0] == 403, "viewer isteyemez (403)")
    s, made = jreq("/api/v1/tokens", sup, {"name": "fx-token", "role": "admin"})
    api_tok = made.get("token", "")
    chk(s == 200 and jreq("/api/v1/files/pull", api_tok, good)[0] == 403
        and push(api_tok, ["HW-FX1"], content)[0] == 403, "API jetonu dosya isteyemez ve gönderemez (yalnız panel)")
    chk(jreq("/api/files/pull", admin, dict(good, any_profile=True))[0] == 403, "admin any_profile isteyemez (403)")
    for bad_path in ("\\\\sunucu\\paylasim\\a.txt", "Users\\a.txt", "C:\\a\\..\\b.txt", "C:\\a.txt:gizli",
                     "C:\\klasor\\", "C:\\a*.txt", "\\\\?\\C:\\a.txt", "C:\\a\u202e.txt"):
        chk(jreq("/api/files/pull", admin, dict(good, path=bad_path))[0] == 400, "geçersiz yol 400: %r" % bad_path)
    chk(jreq("/api/files/pull", admin, dict(good, max_size=0))[0] == 422, "boyut 0: 422")
    chk(jreq("/api/files/pull", admin, dict(good, max_size=filestore.PULL_MAX_BYTES + 1))[0] == 422, "200 MB üstü: 422")
    chk(jreq("/api/files/pull", admin, dict(good, reason="  "))[0] == 400, "gerekçesiz 400")
    chk(jreq("/api/files/pull", admin, dict(good, extra=1))[0] == 422, "bilinmeyen alan 422")
    chk(jreq("/api/files/pull", admin, dict(good, pc="HW-FXOFF"))[0] == 409, "çevrimdışı: 409")
    chk(jreq("/api/files/pull", admin, dict(good, pc="HW-FXNO"))[0] == 409, "bilgisayarda kapalı: 409")
    s, r = jreq("/api/files/pull", sup, dict(good, pc="HW-FX2", any_profile=True))
    m = await fx2.wait(lambda m: m.get("action") == "file_pull")
    chk(s == 200 and m and m["any_profile"] is True, "superadmin any_profile ile istedi")
    if m:
        await fx2.send({"type": "file_result", "transfer_id": m["transfer_id"], "outcome": "rejected",
                        "detail": "Dosya bulunamadı"})
        chk(await wait_for(c, "SELECT status='rejected' AND detail='Dosya bulunamadı' FROM file_transfers "
                              "WHERE transfer_id=$1", m["transfer_id"]), "ajanın ret sonucu kaydedildi")

    print("== alma: yükleme ve admin indirmesi")
    s, r = jreq("/api/files/pull", admin, good)
    m = await fx1.wait(lambda m: m.get("action") == "file_pull")
    chk(s == 200 and m is not None and set(m) == PULL_KEYS, "file_pull sözleşmedeki alanlarla geldi")
    chk(m and m["path"] == pth and m["max_size"] == 1000 and m["reason"] == "Sınav dosyası kontrolü"
        and m["any_profile"] is False and m["transfer_id"] == r.get("transfer_id"), "file_pull değerleri")
    up = m["upload"] if m else ""
    chk(up.startswith("/api/files/%s/upload?t=" % r.get("transfer_id")), "yükleme adresi göreli")
    data = b"%PDF-1.4\n" + MARK + os.urandom(400)
    octet = {"Content-Type": "application/octet-stream"}
    chk(http_req("POST", up, data=data, headers=octet)[0] == 401, "anahtarsız yükleme 401")
    chk(http_req("POST", up, data=data, headers=dict(octet, **agent_headers("HW-FX2")))[0] == 404,
        "başka bilgisayarın anahtarıyla 404")
    s, body, _h = http_req("POST", up, data=data,
                           headers=dict(octet, **agent_headers("HW-FX1"), **{"X-Content-SHA256": _sha(data)}))
    chk(s == 200 and json.loads(body).get("sha256") == _sha(data), "yüklendi (ham gövde, SHA-256 başlığı tuttu)")
    chk(http_req("POST", up, data=data, headers=dict(octet, **agent_headers("HW-FX1")))[0] == 404,
        "ikinci yükleme 404")
    pull_id = r.get("transfer_id")
    prow = await c.fetchrow("SELECT * FROM file_transfers WHERE transfer_id=$1", pull_id)
    chk(prow["status"] == "done" and prow["size"] == len(data) and prow["sha256"] == _sha(data)
        and blob_exists(prow["storage_path"]) and prow["name"] == "rapor.pdf", "satır tamam, dosya sunucuda")
    await fx1.send({"type": "file_result", "transfer_id": pull_id, "outcome": "failed", "detail": "geç gelen"})
    await asyncio.sleep(0.4)
    chk(await c.fetchval("SELECT status FROM file_transfers WHERE transfer_id=$1", pull_id) == "done",
        "yüklenmiş dosyanın durumu sonradan değişmez")
    s, body, h = http_req("GET", "/api/files/%s/content" % pull_id, admin)
    cd = h.get("Content-Disposition") or ""
    chk(s == 200 and body == data, "admin alınan dosyayı indirdi")
    chk(cd.startswith("attachment") and "rapor.pdf" in cd and h.get("X-Content-Type-Options") == "nosniff"
        and (h.get("Content-Type") or "").startswith("application/octet-stream")
        and "no-store" in (h.get("Cache-Control") or ""), "her zaman ek, nosniff, octet-stream, no-store (%s)" % cd)
    chk(http_req("GET", "/api/files/%s/content" % pull_id, viewer)[0] == 403, "viewer indiremez (403)")
    chk(http_req("GET", "/api/files/%s/content" % sent["HW-FX1"], admin)[0] == 404, "gönderilen dosya buradan inmez")

    s, r = jreq("/api/files/pull", admin, dict(good, max_size=100))
    m = await fx1.wait(lambda m: m.get("action") == "file_pull")
    s2 = http_req("POST", m["upload"] if m else "", data=data, headers=dict(octet, **agent_headers("HW-FX1")))[0]
    chk(s2 == 413 and await c.fetchval("SELECT status FROM file_transfers WHERE transfer_id=$1", r.get("transfer_id"))
        == "failed", "boyut sınırı: 413, aktarım failed")
    s, r = jreq("/api/files/pull", admin, good)
    m = await fx1.wait(lambda m: m.get("action") == "file_pull")
    s2 = http_req("POST", m["upload"] if m else "", data=data,
                  headers=dict(octet, **agent_headers("HW-FX1"), **{"X-Content-SHA256": "0" * 64}))[0]
    frow = await c.fetchrow("SELECT status, storage_path FROM file_transfers WHERE transfer_id=$1",
                            r.get("transfer_id"))
    chk(s2 == 400 and frow["status"] == "failed" and frow["storage_path"] is None
        and not os.path.exists(os.path.join(FILES_DIR, "pull-%s.bin" % r.get("transfer_id"))),
        "SHA-256 tutmadı: 400, dosya tutulmadı")
    s, r = jreq("/api/files/pull", admin, dict(good, path="C:\\Users\\Public\\Documents\\liste.txt"))
    m = await fx1.wait(lambda m: m.get("action") == "file_pull")
    mp_body, mp_type = multipart([], [("file", "liste.txt", b"satir\n" + MARK)])
    s2 = http_req("POST", m["upload"] if m else "", data=mp_body,
                  headers=dict({"Content-Type": mp_type}, **agent_headers("HW-FX1")))[0]
    mp_id = r.get("transfer_id")
    chk(s2 == 200 and await c.fetchval("SELECT status FROM file_transfers WHERE transfer_id=$1", mp_id) == "done",
        "multipart yükleme de kabul edildi")

    print("== capability_denied ve modül")
    s, r = jreq("/api/files/pull", admin, dict(good, path="C:\\Users\\Public\\x.txt"))
    m = await fx1.wait(lambda m: m.get("action") == "file_pull")
    await fx1.send({"type": "capability_denied", "capability": "files", "action": "file_pull",
                    "transfer_id": r.get("transfer_id")})
    chk(await wait_for(c, "SELECT status='rejected' FROM file_transfers WHERE transfer_id=$1", r.get("transfer_id")),
        "capability_denied aktarımı reddetti")
    chk(await wait_for(c, "SELECT cap_files_enabled IS FALSE FROM clients WHERE pc_name='HW-FX1'"),
        "yetenek kapalı yazıldı")
    chk(push(admin, ["HW-FX1"], content)[0] == 409, "kapalı yetenekli bilgisayara gönderilmez")
    await fx1.send({"type": "capabilities", "terminal_enabled": True, "vision_enabled": True, "files_enabled": True})
    await wait_for(c, "SELECT cap_files_enabled FROM clients WHERE pc_name='HW-FX1'")
    s, r = jreq("/api/files/pull", admin, dict(good, path="C:\\Users\\Public\\y.txt"))
    open_id = r.get("transfer_id")
    await fx1.wait(lambda m: m.get("action") == "file_pull")
    s, mr = jreq("/api/modules/files", sup, {"enabled": False})
    chk(s == 200 and mr.get("transfers_cancelled", 0) >= 1, "modül kapatılınca açık aktarım iptal (%s)" % mr)
    chk(await c.fetchval("SELECT status FROM file_transfers WHERE transfer_id=$1", open_id) == "rejected",
        "iptal edilen aktarım rejected")
    s, _r = push(admin, ["HW-FX1"], content)
    chk(s == 409, "modül kapalıyken gönderme 409")
    chk(jreq("/api/files/pull", admin, good)[0] == 409, "modül kapalıyken alma 409")
    jreq("/api/modules/files", sup, {"enabled": None})

    print("== liste ve denetim kaydı")
    s, lst = jreq("/api/files?pc=HW-FX1", viewer)
    items = lst.get("items", []) if s == 200 else []
    chk(s == 200 and any(i["transfer_id"] == pull_id and i["downloadable"] for i in items)
        and any(i["direction"] == "push" for i in items), "viewer listeyi görür; alınan dosya indirilebilir")
    chk(all("token_hash" not in i and "storage_path" not in i for i in items), "listede jeton ve depo yolu yok")
    chk(lst.get("push_max_bytes") == filestore.PUSH_MAX_BYTES, "sınırlar listede")
    audits = await c.fetch("SELECT action, changes FROM device_audit_logs WHERE hw_id='HW-FX1' "
                           "AND action LIKE 'file_%' ORDER BY id")
    actions = {a["action"] for a in audits}
    chk({"file_push", "file_pull", "file_pull_received", "file_content_download", "file_result"} <= actions,
        "denetim kaydı: %s" % sorted(actions))
    push_audit = next((json.loads(a["changes"]) for a in audits if a["action"] == "file_push"), {})
    chk(push_audit.get("sha256") == sha and push_audit.get("admin") == "fxadmin"
        and push_audit.get("reason") == "Ders materyali", "gönderme kaydı: özet, kim, gerekçe")
    pull_audit = next((json.loads(a["changes"]) for a in audits if a["action"] == "file_pull"), {})
    chk(pull_audit.get("path") == pth and pull_audit.get("reason") == "Sınav dosyası kontrolü",
        "istek kaydı: yol, gerekçe")
    chk(all(MARK.decode() not in (a["changes"] or "") for a in audits), "dosyanın içeriği denetim kaydında yok")
    chk(not await c.fetchval("SELECT count(*) FROM file_transfers WHERE pc_name = ANY($1::text[]) "
                             "AND (name LIKE '%MARKER%' OR detail LIKE '%MARKER%')", PCS), "içerik veritabanında yok")

    print("== saklama: alınan dosya 7 gün")
    old_blob = await c.fetchval("SELECT storage_path FROM file_transfers WHERE transfer_id=$1", pull_id)
    new_blob = await c.fetchval("SELECT storage_path FROM file_transfers WHERE transfer_id=$1", mp_id)
    await c.execute("UPDATE file_transfers SET created_at = NOW() - interval '8 days' WHERE transfer_id=$1", pull_id)
    await c.execute("UPDATE file_transfers SET created_at = NOW() - interval '6 days' WHERE transfer_id=$1", mp_id)
    out = await filestore.purge()
    chk(out.get("pull_files", 0) >= 1 and not blob_exists(old_blob), "8 günlük dosya silindi")
    chk(await c.fetchval("SELECT storage_path IS NULL AND purged_at IS NOT NULL FROM file_transfers "
                         "WHERE transfer_id=$1", pull_id), "satır kaldı, dosya bağı kalktı")
    chk(blob_exists(new_blob), "6 günlük dosya duruyor")
    chk(http_req("GET", "/api/files/%s/content" % pull_id, admin)[0] == 404, "silinen dosya indirilemez (404)")
    s, lst = jreq("/api/files?pc=HW-FX1", admin)
    it = next((i for i in lst.get("items", []) if i["transfer_id"] == pull_id), {})
    chk(it.get("downloadable") is False and it.get("purged") is True, "listede silindi olarak görünür")


if __name__ == "__main__":
    asyncio.run(main())
