"""GLPI'ye dışa aktarım — entegrasyon testi (CI 'security' job'ı).

Test kendi sahte GLPI REST sunucusunu 127.0.0.1'de açar (apirest.php: initSession, killSession, getFullSession,
Computer, Software, SoftwareVersion, Manufacturer, Item_SoftwareVersion, Ticket, Item_Ticket, ITILFollowup) ve
gelen istekleri kaydeder. Sunucu bu adrese GLPI_ALLOW_PRIVATE=1 ile bağlanır (CI ortamında tanımlı).

- Ayarlar yalnız superadmin'in; jetonlar şifreli saklanır, hiçbir yanıtta ve denetim kaydında görünmez.
- İlk eşitleme bilgisayarları, yazılımları ve talebi oluşturur; seri numarasıyla eşleşen bilgisayar bağlanır, birden
  çok eşleşmede tahmin yapılmaz; ikinci eşitleme çoğaltmaz, yalnızca değişeni günceller.
- Talepler bir kez gider, açık yanıtlar takip olur, iç notlar gitmez; bildiren kişi yalnızca ayar açıksa yazılır.
- Yanlış jeton anlaşılır bir hata verir; kayıtlı jetonlar başka bir adrese gönderilmez.

Ortam: POPS_TEST_HTTP + DB_* + JWT_SECRET.
"""

import asyncio
import json
import os
import sys
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

sys.path.insert(0, os.path.join(os.path.dirname(__file__), os.pardir))
import asyncpg  # noqa: E402

import server  # noqa: E402  (create_jwt)

HTTP = os.environ["POPS_TEST_HTTP"]
APP_TOKEN = "gl-app-token-5f2c9a"
USER_TOKEN = "gl-user-token-81d7e4"
PCS = {
    "HW-GL0001": ("gl-pc-1", "GL-Lab1", "GL-BIOS-1", "GL-UUID-1"),
    "HW-GL0002": ("gl-pc-2", "GL-Lab2", "GL-BIOS-2", None),
    "HW-GL0003": ("gl-pc-3", "GL-Lab2", "GL-BIOS-3", None),   # GLPI'de zaten var: bağlanır
    "HW-GL0004": ("gl-pc-4", "GL-Lab2", "GL-DUP", None),      # GLPI'de iki tane: tahmin yok
    "HW-GL0005": ("gl-pc-5", "GL-Lab2", "To be filled by O.E.M.", None),   # kullanılamaz seri: oluşturulur
}
FAILS = []


def chk(cond, msg):
    print(("  OK   " if cond else "  FAIL ") + msg)
    if not cond:
        FAILS.append(msg)


# ─── Sahte GLPI ───────────────────────────────────────────────────────────────
# GLPI 10'un davranışı (src/Api/API.php): metin &, <, > kodlanmış saklanır ve X-GLPI-Sanitized-Content: false yoksa
# öyle döner; searchText LIKE'tır, ^…$ ile sabitlenir; toplu eklemede bir kısmı olmazsa 207 ERROR_GLPI_PARTIAL_ADD,
# hiçbiri olmazsa 400 ERROR_GLPI_ADD; bulunamayan öğenin güncellenmesi ve silinmesi 400 (ERROR_GLPI_UPDATE/DELETE);
# aynı bilgisayara aynı sürüm ikinci kez bağlanamaz.
def _enc(v):
    return v.replace("&", "&#38;").replace("<", "&#60;").replace(">", "&#62;") if isinstance(v, str) else v


def _dec(v):
    return v.replace("&#60;", "<").replace("&#62;", ">").replace("&#38;", "&") if isinstance(v, str) else v


class FakeGlpi:
    def __init__(self):
        self.items = {}
        self.next_id = 1000
        self.calls = []
        self.sessions = set()
        self.readonly = set()     # bu Computer kimliklerinde güncelleme yetkisi yok
        self.lock = threading.Lock()

    def add(self, itemtype, fields, item_id=None):
        with self.lock:
            if item_id is None:
                self.next_id += 1
                item_id = self.next_id
            self.items.setdefault(itemtype, {})[item_id] = dict({k: _enc(v) for k, v in fields.items()}, id=item_id)
            return item_id

    def all(self, itemtype, **where):
        """Ham (kodu çözülmüş) değerlerle süzülmüş öğeler."""
        out = [{k: _dec(v) for k, v in i.items()} for i in self.items.get(itemtype, {}).values()]
        return [i for i in out if all(i.get(k) == v for k, v in where.items())]

    def count(self, method, itemtype):
        return sum(1 for c in self.calls if c[0] == method and c[1].split("/")[0] == itemtype)

    def refuse(self, itemtype, fields):
        """Eklenemeyecek öğe için ileti (GLPI'nin benzersizlik ve doğrulama kuralları)."""
        if itemtype != "Item_SoftwareVersion":
            return None
        if any(i["items_id"] == fields.get("items_id") and i["softwareversions_id"] == fields.get("softwareversions_id")
               for i in self.items.get(itemtype, {}).values()):
            return "Duplicate entry"
        version = self.items.get("SoftwareVersion", {}).get(fields.get("softwareversions_id"), {})
        return "Doğrulama hatası" if version.get("name") == "REDDET" else None


GLPI = FakeGlpi()


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def reply(self, status, data):
        if self.headers.get("X-GLPI-Sanitized-Content") == "false":
            data = json.loads(_dec(json.dumps(data)))
        raw = json.dumps(data).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(raw)))
        self.end_headers()
        self.wfile.write(raw)

    def handle_any(self, method):
        u = urllib.parse.urlsplit(self.path)
        if not u.path.startswith("/apirest.php/"):
            return self.reply(404, ["ERROR_RESOURCE_NOT_FOUND_NOR_COMMONDBTM", "not found"])
        path = u.path[len("/apirest.php/"):]
        query = urllib.parse.parse_qs(u.query)
        length = int(self.headers.get("Content-Length") or 0)
        body = json.loads(self.rfile.read(length)) if length else None
        GLPI.calls.append((method, path, query, body, dict(self.headers)))
        if self.headers.get("App-Token") != APP_TOKEN:
            return self.reply(400, ["ERROR_WRONG_APP_TOKEN_PARAMETER", "parameter app_token seems wrong"])
        if path == "initSession":
            if self.headers.get("Authorization") != "user_token " + USER_TOKEN:
                return self.reply(401, ["ERROR_GLPI_LOGIN_USER_TOKEN", "parameter user_token seems invalid"])
            token = "sess-%d" % len(GLPI.sessions)
            GLPI.sessions.add(token)
            return self.reply(200, {"session_token": token})
        if self.headers.get("Session-Token") not in GLPI.sessions:
            return self.reply(401, ["ERROR_SESSION_TOKEN_INVALID", "session_token seems invalid"])
        if path == "killSession":
            GLPI.sessions.discard(self.headers.get("Session-Token"))
            return self.reply(200, [])
        if path == "getFullSession":
            return self.reply(200, {"session": {"glpiname": "pops-export", "glpiactive_entity_name": "Okul"}})
        if path == "changeActiveEntities":
            return self.reply(200, True)
        parts = path.split("/")
        itemtype = parts[0]
        table = GLPI.items.setdefault(itemtype, {})
        if method == "GET" and len(parts) == 3:   # alt öğeler: Software/{id}/SoftwareVersion, Computer/{id}/Item_…
            key = {"SoftwareVersion": "softwares_id", "Item_SoftwareVersion": "items_id"}[parts[2]]
            return self.reply(200, [i for i in GLPI.items.get(parts[2], {}).values() if i[key] == int(parts[1])])
        if method == "GET" and len(parts) == 1:
            out = list(table.values())
            for key, values in query.items():
                if key.startswith("searchText["):
                    field, text = key[len("searchText["):-1], values[0].lower()
                    if text.startswith("^") and text.endswith("$"):
                        out = [i for i in out if str(i.get(field) or "").lower() == text[1:-1]]
                    else:
                        out = [i for i in out if text in str(i.get(field) or "").lower()]
            return self.reply(200, out)
        if method == "GET" and len(parts) == 2:
            item = table.get(int(parts[1]))
            return self.reply(200, item) if item else self.reply(404, ["ERROR_ITEM_NOT_FOUND", "Item not found"])
        if method == "POST" and len(parts) == 1:
            inputs = body["input"]
            many = isinstance(inputs, list)
            results = []
            for i in (inputs if many else [inputs]):
                why = GLPI.refuse(itemtype, i)
                results.append({"id": False, "message": why} if why else {"id": GLPI.add(itemtype, i), "message": ""})
            failed = sum(1 for r in results if not r["id"])
            if failed == len(results):
                return self.reply(400, ["ERROR_GLPI_ADD", results if many else results[0]["message"]])
            if failed:
                return self.reply(207, ["ERROR_GLPI_PARTIAL_ADD", results])
            return self.reply(201, results if many else results[0])
        if method in ("PUT", "DELETE") and len(parts) == 2:
            item_id = int(parts[1])
            code = "ERROR_GLPI_%s" % ("UPDATE" if method == "PUT" else "DELETE")
            if item_id not in table:
                return self.reply(400, [code, [{str(item_id): False, "message": None}]])
            if method == "PUT" and itemtype == "Computer" and item_id in GLPI.readonly:
                return self.reply(400, [code, [{str(item_id): False,
                                                "message": "You don't have permission to perform this action."}]])
            if method == "PUT":
                table[item_id].update({k: _enc(v) for k, v in body["input"].items()})
            else:
                del table[item_id]
            return self.reply(200, [{str(item_id): True, "message": ""}])
        return self.reply(400, ["ERROR_BAD_REQUEST", "unexpected %s %s" % (method, path)])

    def do_GET(self):
        self.handle_any("GET")

    def do_POST(self):
        self.handle_any("POST")

    def do_PUT(self):
        self.handle_any("PUT")

    def do_DELETE(self):
        self.handle_any("DELETE")


# ─── Yardımcılar ──────────────────────────────────────────────────────────────
def req(path, token=None, body=None, method=None):
    """(durum, gövde, ham metin)"""
    data = json.dumps(body).encode() if body is not None else None
    r = urllib.request.Request(HTTP + path, data=data, method=method or ("POST" if body is not None else "GET"))
    if body is not None:
        r.add_header("Content-Type", "application/json")
    if token:
        r.add_header("Authorization", "Bearer " + token)
    try:
        with urllib.request.urlopen(r, timeout=150) as resp:
            raw = resp.read().decode()
            return resp.status, json.loads(raw or "{}"), raw
    except urllib.error.HTTPError as e:
        raw = e.read().decode()
        try:
            return e.code, json.loads(raw or "{}"), raw
        except ValueError:
            return e.code, {}, raw


async def conn():
    return await asyncpg.connect(
        host=os.environ.get("DB_HOST", "localhost"), port=int(os.environ.get("DB_PORT", "5432")),
        user=os.environ["DB_USER"], password=os.environ["DB_PASS"], database=os.environ["DB_NAME"],
    )


async def cleanup(c):
    names = list(PCS)
    await c.execute("DELETE FROM tickets WHERE subject LIKE 'GL %'")
    for table, col in (("clients", "pc_name"), ("device_software", "pc_name"), ("hw_inventory", "pc_name")):
        await c.execute("DELETE FROM %s WHERE %s = ANY($1::text[])" % (table, col), names)
    await c.execute("DELETE FROM glpi_links")
    await c.execute("DELETE FROM global_settings WHERE key LIKE 'glpi_%'")
    await c.execute("DELETE FROM users WHERE username IN ('glsuper', 'gladmin')")


def computer_of(serial):
    found = GLPI.all("Computer", serial=serial)
    return found[0] if len(found) == 1 else None


async def main():
    srv = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    threading.Thread(target=srv.serve_forever, daemon=True).start()
    url = "http://127.0.0.1:%d" % srv.server_address[1]
    c = await conn()
    await cleanup(c)
    for pc, (host, lab, bios, uuid) in PCS.items():
        await c.execute("INSERT INTO clients (pc_name, hostname, lab_name, status, dna_bios, dna_uuid, logged_user) "
                        "VALUES ($1, $2, $3, 'Offline', $4, $5, 'gizli-kullanici')", pc, host, lab, bios, uuid)
    for pc, name, version, publisher, date in (
            ("HW-GL0001", "GL App A", "1.0", "GL Pub", "20260101"), ("HW-GL0001", "GL App B", "2.0", None, None),
            ("HW-GL0002", "GL App A", "1.0", "GL Pub", None), ("HW-GL0003", "GL Ar&Ge <Aracı>", "2.1", None, None)):
        await c.execute("INSERT INTO device_software (pc_name, name, version, publisher, install_date) "
                        "VALUES ($1, $2, $3, $4, $5)", pc, name, version, publisher, date)
    ticket = await c.fetchval(
        "INSERT INTO tickets (source, pc_name, reporter, category, subject, body, status, priority) "
        "VALUES ('agent', 'HW-GL0001', 'ogrenci1', 'yazici', 'GL yazıcı sorunu', 'Çıktı gelmiyor.', 'open', 'high') "
        "RETURNING id")
    await c.execute("INSERT INTO ticket_messages (ticket_id, author, body, internal) VALUES "
                    "($1, 'gladmin', 'GL açık yanıt', FALSE), ($1, 'gladmin', 'GL iç not', TRUE)", ticket)
    GLPI.add("Computer", {"name": "eski-ad", "serial": "GL-BIOS-3", "entities_id": 0}, 100)
    # GLPI'de zaten var ve bilgisayara (GLPI ajanı) bağlı: adında &, <, > var, GLPI kodlu saklar
    GLPI.add("Software", {"name": "GL Ar&Ge <Aracı>", "entities_id": 0}, 300)
    GLPI.add("SoftwareVersion", {"name": "2.1", "softwares_id": 300, "entities_id": 0}, 301)
    GLPI.add("Item_SoftwareVersion", {"items_id": 100, "itemtype": "Computer", "softwareversions_id": 301}, 302)
    GLPI.add("Computer", {"name": "kopya-1", "serial": "GL-DUP", "entities_id": 0}, 101)
    GLPI.add("Computer", {"name": "kopya-2", "serial": "GL-DUP", "entities_id": 0}, 102)
    for u, role in (("glsuper", "superadmin"), ("gladmin", "admin")):
        await c.execute("INSERT INTO users (username, password_hash, role, permissions, token_version) "
                        "VALUES ($1, 'x', $2, '[]', 0)", u, role)
    superadmin = server.create_jwt("glsuper", "superadmin", 0)
    admin = server.create_jwt("gladmin", "admin", 0)
    try:
        await run(c, url, superadmin, admin, ticket)
    finally:
        await cleanup(c)
        await c.close()
        srv.shutdown()
    if FAILS:
        print("\nBASARISIZ: %d kontrol" % len(FAILS))
        sys.exit(1)
    print("\nTUM GLPI TESTLERI GECTI")


def settings(url, **extra):
    body = {"enabled": True, "url": url, "entity": 0, "interval_hours": 0,
            "sync": {"computers": True, "software": True, "tickets": True, "ticket_reporter": False},
            "locations": {"GL-Lab1": 7}}
    body.update(extra)
    return body


def sync(token):
    s, b, raw = req("/api/system/glpi/sync?wait=true", token, {})
    return s, (b.get("result") or {}), raw


async def run(c, url, superadmin, admin, ticket):
    print("== ayarlar ve gizli değerler")
    chk(req("/api/system/glpi", admin)[0] == 403, "admin GLPI ayarlarını göremez")
    chk(req("/api/system/glpi", admin, settings(url))[0] == 403, "admin değiştiremez")
    chk(req("/api/system/glpi/sync", superadmin, {})[0] == 400, "kapalıyken eşitleme 400")
    s, b, _ = req("/api/system/glpi", superadmin, settings(url))
    chk(s == 400, "adres ve kullanıcı jetonu olmadan açılamaz (%s)" % b.get("detail"))
    s, b, raw = req("/api/system/glpi", superadmin, settings(url, app_token=APP_TOKEN, user_token=USER_TOKEN))
    chk(s == 200 and b.get("app_token_set") and b.get("user_token_set") and b.get("url") == url,
        "kaydedildi; yanıt jetonların yalnızca kayıtlı olduğunu söyler")
    chk(APP_TOKEN not in raw and USER_TOKEN not in raw and "app_token\"" not in raw.replace("app_token_set\"", ""),
        "yanıtta jeton değeri yok")
    s, b, raw = req("/api/system/glpi", superadmin)
    chk(s == 200 and APP_TOKEN not in raw and USER_TOKEN not in raw and b.get("tickets_since"),
        "GET'te jeton yok; talepler bugünden itibaren")
    stored = await c.fetchval("SELECT value FROM global_settings WHERE key = 'glpi_user_token'")
    chk(stored.startswith("v1:") and USER_TOKEN not in stored, "jeton veritabanında şifreli")
    audit = " ".join(r["changes"] or "" for r in await c.fetch(
        "SELECT changes FROM device_audit_logs WHERE action LIKE 'glpi%'"))
    chk("user_token" in audit and USER_TOKEN not in audit and APP_TOKEN not in audit,
        "denetim kaydı değişen ayarın adını tutar, değerini değil")
    chk(req("/api/system/glpi", superadmin, settings(url, labb=1))[0] == 422, "tanınmayan alan 422")
    s, b, _ = req("/api/system/glpi", superadmin, settings("http://localhost:%s" % url.rsplit(":", 1)[1]))
    chk(s == 400 and "jeton" in str(b.get("detail")), "adres değişince kayıtlı jetonlar kendiliğinden gitmez (400)")

    print("== bağlantı sınaması")
    s, b, _ = req("/api/system/glpi/test", superadmin, {})
    chk(s == 200 and b.get("ok") and b.get("user") == "pops-export", "sınama: oturum açıldı (%s)" % b)
    before = len([x for x in GLPI.calls if x[1] == "initSession"])
    s, b, _ = req("/api/system/glpi/test", superadmin, {"url": "http://localhost:%s" % url.rsplit(":", 1)[1]})
    chk(s == 200 and not b.get("ok") and len([x for x in GLPI.calls if x[1] == "initSession"]) == before,
        "başka adres sınanırken kayıtlı jeton gönderilmez")

    print("== ilk eşitleme")
    s, r, raw = sync(superadmin)
    chk(s == 200 and r.get("ok"), "eşitleme başarılı (%s)" % r)
    chk(USER_TOKEN not in raw and APP_TOKEN not in raw, "sonuçta jeton yok")
    pc1, pc2 = computer_of("GL-BIOS-1"), computer_of("GL-BIOS-2")
    chk(pc1 and pc1["name"] == "gl-pc-1" and pc1.get("uuid") == "GL-UUID-1" and pc1.get("locations_id") == 7
        and pc1.get("entities_id") == 0, "bilgisayar oluşturuldu: ad, seri, UUID, eşlenen konum (%s)" % pc1)
    chk(pc2 and "locations_id" not in pc2, "eşlenmemiş sınıfın konumu yazılmaz")
    chk(computer_of("GL-BIOS-3") and computer_of("GL-BIOS-3")["id"] == 100
        and GLPI.items["Computer"][100]["name"] == "gl-pc-3", "seri numarasıyla eşleşen bilgisayar bağlandı")
    chk(len(GLPI.all("Computer", serial="GL-DUP")) == 2, "iki eşleşme: yeni bilgisayar oluşturulmadı")
    gl5 = [i for i in GLPI.all("Computer", name="gl-pc-5")]
    chk(len(gl5) == 1 and "serial" not in gl5[0], "kullanılamayan seri numarası gönderilmez, bilgisayar oluşturulur")
    chk(not [i for i in GLPI.items["Computer"].values() if "gizli-kullanici" in json.dumps(i)],
        "oturumdaki kullanıcı gönderilmez")
    s, b, _ = req("/api/system/glpi", superadmin)
    probs = {p["pc_name"]: p for p in b.get("problems", [])}
    chk(probs.get("HW-GL0004", {}).get("state") == "ambiguous", "çoklu eşleşme Sistem'de listelenir")
    apps = GLPI.all("Software", name="GL App A")
    chk(len(apps) == 1 and apps[0].get("manufacturers_id") == GLPI.all("Manufacturer", name="GL Pub")[0]["id"],
        "yazılım bir kez, üreticisiyle oluşturuldu")
    versions = GLPI.all("SoftwareVersion", softwares_id=apps[0]["id"])
    chk(len(versions) == 1 and versions[0]["name"] == "1.0", "sürüm bir kez oluşturuldu")
    chk(len(GLPI.all("Software", name="GL Ar&Ge <Aracı>")) == 1,
        "&, <, > içeren adla var olan yazılım bulundu, kopyası oluşturulmadı")
    chk(len([i for i in GLPI.items["Item_SoftwareVersion"].values() if i["items_id"] == 100]) == 1
        and not await c.fetchval("SELECT count(*) FROM glpi_links WHERE kind = 'software_link' "
                                 "AND pops_key LIKE 'HW-GL0003|%'")
        and await c.fetchval("SELECT count(*) FROM glpi_links WHERE kind = 'software_set' AND pops_key = 'HW-GL0003'"),
        "bilgisayarda zaten bağlı sürüm yeniden eklenmedi ve POps'un sayılmadı")
    chk((r.get("counts") or {}).get("item_errors") == 0, "öğe hatası yok (%s)" % r.get("errors"))
    links = [i for i in GLPI.items.get("Item_SoftwareVersion", {}).values() if i["items_id"] in (pc1["id"], pc2["id"])]
    chk(len(links) == 3, "üç yazılım bağlantısı (%d)" % len(links))
    chk(any(i.get("date_install") == "2026-01-01" for i in links if i["items_id"] == pc1["id"]),
        "kurulum tarihi GLPI biçiminde")
    tickets = GLPI.all("Ticket", name="GL yazıcı sorunu")
    chk(len(tickets) == 1 and tickets[0]["priority"] == 4 and tickets[0]["status"] == 1
        and "Çıktı gelmiyor." in tickets[0]["content"] and "ogrenci1" not in tickets[0]["content"],
        "talep oluşturuldu: öncelik, durum, metin; bildiren yazılmadı")
    tid = tickets[0]["id"] if tickets else -1
    chk(GLPI.all("Item_Ticket", tickets_id=tid, items_id=pc1["id"]), "talep bilgisayara bağlandı")
    follow = GLPI.all("ITILFollowup", items_id=tid)
    chk(len(follow) == 1 and "GL açık yanıt" in follow[0]["content"] and follow[0]["is_private"] == 0,
        "açık yanıt takip oldu, iç not gitmedi")
    chk(GLPI.count("GET", "killSession") >= 1 and not GLPI.sessions, "oturum kapatıldı")

    print("== ikinci eşitleme: çoğaltmaz, değişeni günceller")
    kinds = (("POST", "Computer"), ("PUT", "Computer"), ("POST", "Item_SoftwareVersion"), ("POST", "Ticket"),
             ("POST", "ITILFollowup"), ("POST", "Software"))
    marks = {k: GLPI.count(*k) for k in kinds}
    s, r, _ = sync(superadmin)
    chk(s == 200 and r.get("ok") and all(GLPI.count(*k) == v for k, v in marks.items()),
        "değişiklik yokken hiçbir şey gönderilmedi (%s)" % r.get("counts"))
    # Yönetici bağlantıyı GLPI'de elle silmiş: POps programı kaldırınca 400 ERROR_GLPI_DELETE alır, bağlantıyı unutur
    app_b = await c.fetchval("SELECT glpi_id FROM glpi_links WHERE pops_key = 'HW-GL0001|GL App B|2.0'")
    del GLPI.items["Item_SoftwareVersion"][app_b]
    await c.execute("UPDATE clients SET hostname = 'gl-pc-1-yeni' WHERE pc_name = 'HW-GL0001'")
    await c.execute("DELETE FROM device_software WHERE pc_name = 'HW-GL0001' AND name = 'GL App B'")
    await c.execute("INSERT INTO device_software (pc_name, name, version) VALUES ('HW-GL0001', 'GL App C', '3')")
    await c.execute("INSERT INTO ticket_messages (ticket_id, author, body) VALUES ($1, 'gladmin', 'GL ikinci')", ticket)
    computers = len(GLPI.items["Computer"])
    s, r, _ = sync(superadmin)
    chk(s == 200 and r.get("ok") and len(GLPI.items["Computer"]) == computers
        and GLPI.items["Computer"][pc1["id"]]["name"] == "gl-pc-1-yeni"
        and GLPI.count("PUT", "Computer") == marks[("PUT", "Computer")] + 1,
        "yalnızca değişen bilgisayar güncellendi, yeni bilgisayar yok")
    names = sorted(GLPI.items["SoftwareVersion"][i["softwareversions_id"]]["softwares_id"]
                   for i in GLPI.items["Item_SoftwareVersion"].values() if i["items_id"] == pc1["id"])
    soft = {i["id"]: i["name"] for i in GLPI.items["Software"].values()}
    chk(sorted(soft[n] for n in names) == ["GL App A", "GL App C"],
        "kaldırılan programın bağlantısı silindi, yenisi eklendi")
    chk(r["counts"].get("item_errors") == 0 and not await c.fetchval(
        "SELECT count(*) FROM glpi_links WHERE pops_key = 'HW-GL0001|GL App B|2.0'"),
        "GLPI'de zaten silinmiş bağlantı hata sayılmadı, unutuldu (%s)" % r.get("errors"))
    chk(len(GLPI.all("Ticket", name="GL yazıcı sorunu")) == 1 and len(GLPI.all("ITILFollowup", items_id=tid)) == 2,
        "talep yeniden gönderilmedi, yeni açık yanıt takip oldu")

    print("== talepte bildiren (ayar açıkken)")
    s, _, _ = req("/api/system/glpi", superadmin, settings(url, sync={"computers": True, "software": True,
                                                                      "tickets": True, "ticket_reporter": True}))
    await c.execute("INSERT INTO tickets (source, pc_name, reporter, category, subject, body) VALUES "
                    "('agent', 'HW-GL0002', 'ogrenci2', 'ag', 'GL ağ yok', 'İnternet yok.')")
    s, r, _ = sync(superadmin)
    t2 = GLPI.all("Ticket", name="GL ağ yok")
    chk(len(t2) == 1 and "Bildiren: ogrenci2" in t2[0]["content"] and t2[0]["priority"] == 3,
        "bildiren ayar açıkken talep metninde")

    print("== kısmen eklenen yazılımlar ve güncelleme hataları")
    await c.execute("INSERT INTO device_software (pc_name, name, version) VALUES ('HW-GL0002', 'GL App D', '1'), "
                    "('HW-GL0002', 'GL Reddedilen', 'REDDET')")
    s, r, _ = sync(superadmin)

    def d_links():
        app_d = GLPI.all("Software", name="GL App D")[0]["id"]
        return [i for i in GLPI.items["Item_SoftwareVersion"].values() if i["items_id"] == pc2["id"]
                and GLPI.items["SoftwareVersion"][i["softwareversions_id"]]["softwares_id"] == app_d]
    owned = await c.fetchval("SELECT glpi_id FROM glpi_links WHERE pops_key = 'HW-GL0002|GL App D|1'")
    chk(r.get("ok") and r["counts"].get("item_errors") == 1 and len(d_links()) == 1 and owned == d_links()[0]["id"],
        "kısmi ekleme (207): eklenen bağlantı kaydedildi, reddedilen hata sayıldı (%s)" % r.get("errors"))
    s, r, _ = sync(superadmin)
    chk(len(d_links()) == 1 and r["counts"].get("item_errors") == 1, "yeniden denemede eklenen çoğaltılmadı")
    GLPI.readonly.add(pc2["id"])
    await c.execute("UPDATE clients SET hostname = 'gl-pc-2-yeni' WHERE pc_name = 'HW-GL0002'")
    gl5_id = gl5[0]["id"]
    del GLPI.items["Computer"][gl5_id]
    await c.execute("UPDATE clients SET hostname = 'gl-pc-5-yeni' WHERE pc_name = 'HW-GL0005'")
    s, r, _ = sync(superadmin)
    states = {row["pops_key"]: row["state"] for row in await c.fetch(
        "SELECT pops_key, state FROM glpi_links WHERE kind = 'computer' AND pops_key IN ('HW-GL0002', 'HW-GL0005')")}
    chk(states.get("HW-GL0002") == "ok" and any("permission" in e for e in r.get("errors", [])),
        "yetki hatası öğe hatasıdır, bağlantı kırık sayılmaz (%s)" % r.get("errors"))
    chk(states.get("HW-GL0005") == "broken", "GLPI'den silinmiş bilgisayar kırık; yeniden oluşturulmadı")
    chk(not GLPI.all("Computer", name="gl-pc-5-yeni"), "silinen bilgisayar yeniden oluşturulmadı")
    GLPI.readonly.discard(pc2["id"])

    print("== yanlış jeton ve bağlantı unutma")
    s, _, _ = req("/api/system/glpi", superadmin, settings(url, user_token="yanlis-jeton-123"))
    s, b, raw = req("/api/system/glpi/test", superadmin, {})
    chk(s == 200 and b.get("ok") is False and "kullanıcı jetonunu kabul etmedi" in b.get("error", "")
        and "yanlis-jeton-123" not in raw, "sınama: anlaşılır hata, jeton iletide yok (%s)" % b.get("error"))
    s, r, raw = sync(superadmin)
    chk(s == 200 and r.get("ok") is False and "kullanıcı jetonunu kabul etmedi" in (r.get("error") or "")
        and "yanlis-jeton-123" not in raw, "eşitleme: anlaşılır hata (%s)" % r.get("error"))
    s, b, _ = req("/api/system/glpi", superadmin)
    chk(b.get("last_run", {}).get("ok") is False and b["last_run"].get("failures") == 1,
        "son tur ve ardışık hata sayısı Sistem'de")
    chk(req("/api/system/glpi/links/HW-GL0004", superadmin, method="DELETE")[0] == 200
        and req("/api/system/glpi/links/HW-GL0004", superadmin, method="DELETE")[0] == 404,
        "bağlantı unutulur (sonraki eşitlemede yeniden aranır)")


if __name__ == "__main__":
    started = time.time()
    asyncio.run(main())
    print("süre: %.1f sn" % (time.time() - started))
