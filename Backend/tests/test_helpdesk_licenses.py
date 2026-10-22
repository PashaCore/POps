"""Lisans takibi ve yardım masası — entegrasyon testi (CI 'security' job'ı).

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

import server  # noqa: E402  (create_jwt)

HTTP = os.environ["POPS_TEST_HTTP"]
FAILS = []
PCS = ["HW-HD1", "HW-HD2"]


def chk(cond, msg):
    print(("  OK   " if cond else "  FAIL ") + msg)
    if not cond:
        FAILS.append(msg)


def req(path, token=None, body=None, method=None, headers=None, raw=False):
    data = json.dumps(body).encode() if body is not None else None
    r = urllib.request.Request(HTTP + path, data=data, method=method or ("POST" if body is not None else "GET"))
    r.add_header("Content-Type", "application/json")
    if token:
        r.add_header("Authorization", "Bearer " + token)
    for k, v in (headers or {}).items():
        r.add_header(k, v)
    try:
        with urllib.request.urlopen(r, timeout=30) as resp:
            payload = resp.read()
            return resp.status, (payload.decode("utf-8") if raw else json.loads(payload or b"{}"))
    except urllib.error.HTTPError as e:
        return e.code, {}


async def q(sql, *args):
    c = await asyncpg.connect(
        host=os.environ.get("DB_HOST", "localhost"),
        port=int(os.environ.get("DB_PORT", "5432")),
        user=os.environ["DB_USER"],
        password=os.environ["DB_PASS"],
        database=os.environ["DB_NAME"],
    )
    try:
        return await c.fetch(sql, *args)
    finally:
        await c.close()


async def setup():
    for u, role in (("hdsuper", "superadmin"), ("hdadmin", "admin"), ("hdviewer", "viewer")):
        await q("DELETE FROM users WHERE username=$1", u)
        await q(
            "INSERT INTO users (username,password_hash,role,permissions,token_version) VALUES ($1,'x',$2,'[]',0)",
            u,
            role,
        )
    await q("DELETE FROM tickets WHERE pc_name = ANY($1::text[])", PCS)
    await q("DELETE FROM licenses WHERE name LIKE 'HD %'")
    await q("DELETE FROM device_software WHERE pc_name = ANY($1::text[])", PCS)
    await q("DELETE FROM agent_secrets WHERE pc_name = ANY($1::text[])", PCS)
    await q("DELETE FROM clients WHERE pc_name = ANY($1::text[])", PCS)
    await q(
        "INSERT INTO clients (pc_name, hostname, lab_name, status) VALUES "
        "('HW-HD1','HD-PC1','HD-LAB','Online'), ('HW-HD2','HD-PC2','HD-LAB','Offline')"
    )
    await q(
        "INSERT INTO agent_secrets (pc_name, secret_hash) VALUES ('HW-HD1',$1)",
        hashlib.sha256(b"hd-secret").hexdigest(),
    )
    await q(
        "INSERT INTO device_software (pc_name, name, version, publisher) VALUES "
        "('HW-HD1','Microsoft Office LTSC Professional Plus 2021','16.0','Microsoft Corporation'),"
        "('HW-HD2','Microsoft Office LTSC Standard 2021','16.0','Microsoft Corporation'),"
        "('HW-HD1','Mozilla Firefox','130.0','Mozilla')"
    )


def main():
    asyncio.run(setup())
    ad = server.create_jwt("hdadmin", "admin", 0)
    vw = server.create_jwt("hdviewer", "viewer", 0)
    agent = {"X-Agent-Id": "HW-HD1", "X-Agent-Secret": "hd-secret"}

    print("== lisanslar")
    lic = {
        "name": "HD Office 2021",
        "match_pattern": "Office LTSC",
        "publisher": "Microsoft",
        "seats": 1,
        "license_type": "per_device",
        "expires_at": "2099-01-01",
    }
    chk(req("/api/licenses", vw, lic)[0] == 403, "viewer lisans ekleyemez")
    chk(req("/api/licenses", ad, {**lic, "match_pattern": "%"})[0] == 400, "joker karakterli desen reddedildi")
    chk(req("/api/licenses", ad, {**lic, "expires_at": "yarin"})[0] == 400, "bozuk tarih reddedildi")
    s, r = req("/api/licenses", ad, lic)
    lid = r.get("id")
    chk(s == 200 and lid, "lisans eklendi")
    s, lst = req("/api/licenses", vw)
    mine = [x for x in lst.get("items", []) if x["id"] == lid]
    chk(
        s == 200 and mine and mine[0]["installed"] == 2 and mine[0]["state"] == "over" and mine[0]["free"] == -1,
        "2 kurulum / 1 koltuk → aşım (%s)" % (mine[0] if mine else None),
    )
    req("/api/licenses/%s" % lid, ad, {**lic, "seats": 5, "expires_at": "2000-01-01"})
    s, lst = req("/api/licenses", vw)
    mine = [x for x in lst.get("items", []) if x["id"] == lid]
    chk(mine and mine[0]["state"] == "expired", "süresi dolmuş lisans 'expired'")
    req("/api/licenses/%s" % lid, ad, {**lic, "seats": None, "expires_at": None})
    s, lst = req("/api/licenses", vw)
    mine = [x for x in lst.get("items", []) if x["id"] == lid]
    chk(mine and mine[0]["state"] == "ok" and mine[0]["free"] is None, "sınırsız lisans 'ok'")
    s, devs = req("/api/licenses/%s/devices" % lid, vw)
    chk(s == 200 and sorted(d["pc_name"] for d in devs) == PCS, "lisansın kurulu olduğu cihazlar")
    s, csv_text = req("/api/reports/export?kind=licenses", ad, raw=True)
    chk(s == 200 and "HD Office 2021" in csv_text, "lisans CSV")
    chk(req("/api/licenses/%s" % lid, ad, method="DELETE")[0] == 200, "lisans silindi")

    print("== yardım masası (ajan)")
    t = {
        "subject": "Yazıcı çalışmıyor",
        "body": "Kat 2 yazıcısı kağıt sıkıştı diyor",
        "category": "yazici",
        "reporter": "ogrenci1",
    }
    chk(req("/api/tickets/agent/HW-HD1", body=t)[0] == 401, "anahtarsız ajan talep açamaz")
    chk(req("/api/tickets/agent/HW-HD2", body=t, headers=agent)[0] == 403, "başka cihaz adına talep açılamaz")
    chk(req("/api/tickets/agent/HW-HD1", body={**t, "subject": "a"}, headers=agent)[0] == 400, "kısa konu reddedildi")
    time.sleep(5.1)  # geçersiz istek de hız sınırına sayılır
    s, r = req("/api/tickets/agent/HW-HD1", body=t, headers=agent)
    tid = r.get("id")
    chk(s == 200 and tid, "ajan talep açtı (#%s)" % tid)
    chk(
        req("/api/tickets/agent/HW-HD1", body={**t, "subject": "Hemen tekrar"}, headers=agent)[0] == 429,
        "5 sn içinde ikinci istek hız sınırına takıldı",
    )
    for _ in range(4):
        time.sleep(5.1)
        req("/api/tickets/agent/HW-HD1", body={**t, "subject": "Tekrar talep"}, headers=agent)
    time.sleep(5.1)
    chk(req("/api/tickets/agent/HW-HD1", body=t, headers=agent)[0] == 429, "cihaz başına açık talep sınırı (5)")
    s, lst = req("/api/notifications", ad)
    chk(
        any(n["event"] == "ticket_new" and n["pc_name"] == "HW-HD1" for n in lst.get("items", [])),
        "yeni talep bildirimi",
    )

    print("== yardım masası (panel)")
    chk(req("/api/tickets", vw)[0] == 403, "viewer talepleri göremez")
    s, lst = req("/api/tickets?q=" + urllib.parse.quote("yazıcı"), ad)
    chk(s == 200 and any(x["id"] == tid for x in lst["items"]), "arama ile bulundu")
    s, _ = req("/api/tickets/%s/messages" % tid, ad, {"body": "Kağıdı çıkarıp tekrar deneyin", "internal": False})
    req("/api/tickets/%s/messages" % tid, ad, {"body": "Toner de azalmış, sipariş ver", "internal": True})
    s, one = req("/api/tickets/%s" % tid, ad)
    chk(s == 200 and one["status"] == "waiting" and len(one["messages"]) == 2, "yanıt verilince 'yanıt bekleniyor'")
    s, _ = req("/api/tickets/%s/update" % tid, ad, {"status": "resolved", "assignee": "hdadmin", "priority": "high"})
    s, one = req("/api/tickets/%s" % tid, ad)
    chk(one["status"] == "resolved" and one["resolved_at"] and one["assignee"] == "hdadmin", "çözüldü, atandı")
    chk(
        any(m["internal"] and "durum: çözüldü" in m["body"] for m in one["messages"]),
        "değişiklik iç not olarak kaydedildi",
    )
    chk(req("/api/tickets/%s/update" % tid, ad, {"status": "bitti"})[0] == 400, "geçersiz durum reddedildi")
    s, mine = req("/api/tickets/agent/HW-HD1", headers=agent)
    first = [x for x in mine if x["id"] == tid]
    chk(
        s == 200
        and first
        and first[0]["status_text"] == "çözüldü"
        and len(first[0]["replies"]) == 1
        and "Toner" not in json.dumps(first[0]),
        "ajan yanıtı görür, iç notları görmez",
    )
    s, r = req("/api/tickets", ad, {"subject": "Projeksiyon bozuk", "category": "donanim", "pc_name": "HW-HD2"})
    chk(s == 200 and r.get("id"), "panelden talep açıldı")

    asyncio.run(q("DELETE FROM tickets WHERE pc_name = ANY($1::text[])", PCS))
    asyncio.run(q("DELETE FROM device_software WHERE pc_name = ANY($1::text[])", PCS))
    if FAILS:
        print("BASARISIZ: %d kontrol" % len(FAILS))
        sys.exit(1)
    print("TUM LISANS VE YARDIM MASASI TESTLERI GECTI")


if __name__ == "__main__":
    main()
