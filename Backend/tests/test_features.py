"""Zamanlanmış görevler, bildirimler, yazılım envanteri, yama durumu ve raporlar — entegrasyon testi.

Çalışan sunucuya karşı koşar (CI 'security' job'ı). Ortam: POPS_TEST_HTTP + DB_* + JWT_SECRET.
Zamanlayıcı turu 30 sn olduğu için bir kontrol en fazla ~40 sn bekler.
"""

import asyncio
import hashlib
import json
import os
import sys
import threading
import time
import urllib.error
import urllib.request
from http.server import BaseHTTPRequestHandler, HTTPServer

sys.path.insert(0, os.path.join(os.path.dirname(__file__), os.pardir))
import asyncpg  # noqa: E402

import server  # noqa: E402  (create_jwt)

HTTP = os.environ["POPS_TEST_HTTP"]
FAILS = []


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


async def conn():
    return await asyncpg.connect(
        host=os.environ.get("DB_HOST", "localhost"),
        port=int(os.environ.get("DB_PORT", "5432")),
        user=os.environ["DB_USER"],
        password=os.environ["DB_PASS"],
        database=os.environ["DB_NAME"],
    )


def _sha(s):
    return hashlib.sha256(s.encode()).hexdigest()


PCS = ["HW-FT1", "HW-FT2"]


async def setup():
    c = await conn()
    try:
        for u, role in (("ftsuper", "superadmin"), ("ftadmin", "admin"), ("ftviewer", "viewer")):
            await c.execute("DELETE FROM users WHERE username=$1", u)
            await c.execute(
                "INSERT INTO users (username,password_hash,role,permissions,token_version) VALUES ($1,'x',$2,'[]',0)",
                u,
                role,
            )
        await c.execute("DELETE FROM agent_secrets WHERE pc_name = ANY($1::text[])", PCS)
        await c.execute("DELETE FROM clients WHERE pc_name = ANY($1::text[])", PCS)
        await c.execute("DELETE FROM tasks WHERE target_pc = ANY($1::text[])", PCS)
        await c.execute(
            "INSERT INTO clients (pc_name, hostname, lab_name, status) VALUES "
            "('HW-FT1','FT-PC1','FT-LAB','Offline'), ('HW-FT2','FT-PC2','FT-LAB','Offline')"
        )
        await c.execute("INSERT INTO agent_secrets (pc_name, secret_hash) VALUES ('HW-FT1',$1)", _sha("ft-secret-1"))
    finally:
        await c.close()


async def q(sql, *args):
    c = await conn()
    try:
        return await c.fetch(sql, *args)
    finally:
        await c.close()


class Hook(BaseHTTPRequestHandler):
    received = []

    def do_POST(self):
        n = int(self.headers.get("Content-Length") or 0)
        Hook.received.append(json.loads(self.rfile.read(n) or b"{}"))
        self.send_response(204)
        self.end_headers()

    def log_message(self, *a):
        pass


def main():
    asyncio.run(setup())
    sa = server.create_jwt("ftsuper", "superadmin", 0)
    ad = server.create_jwt("ftadmin", "admin", 0)
    vw = server.create_jwt("ftviewer", "viewer", 0)
    agent1 = {"X-Agent-Id": "HW-FT1", "X-Agent-Secret": "ft-secret-1"}

    print("== zamanlanmış görevler")
    base = {
        "name": "FT gunluk",
        "command": "echo ft",
        "target_mode": "LAB",
        "targets": ["FT-LAB"],
        "schedule_type": "daily",
        "time_of_day": "03:00",
    }
    chk(req("/api/scheduled_tasks", body=base)[0] == 401, "kimliksiz oluşturma → 401")
    chk(req("/api/scheduled_tasks", vw, base)[0] == 403, "viewer oluşturma → 403")
    chk(req("/api/scheduled_tasks", ad, {**base, "time_of_day": "25:00"})[0] == 400, "geçersiz saat → 400")
    chk(
        req("/api/scheduled_tasks", ad, {**base, "schedule_type": "weekly", "weekdays": []})[0] == 400,
        "haftalık ama gün yok → 400",
    )
    chk(
        req("/api/scheduled_tasks", ad, {**base, "schedule_type": "once", "run_at": "2000-01-01T10:00"})[0] == 400,
        "geçmiş tarihli tek seferlik → 400",
    )
    s, t = req("/api/scheduled_tasks", ad, base)
    chk(s == 200 and t.get("next_run") and "T03:00:00" in t["next_run"], "günlük görev oluştu, sonraki çalışma 03:00")
    sid = t.get("id")
    s, w = req(
        "/api/scheduled_tasks",
        ad,
        {
            **base,
            "name": "FT haftalik",
            "schedule_type": "weekly",
            "weekdays": [1, 3, 5],
            "target_mode": "PC",
            "targets": ["HW-FT1"],
        },
    )
    chk(s == 200 and w.get("weekdays") == [1, 3, 5], "haftalık görev (Pzt/Çar/Cum)")
    s, r = req("/api/scheduled_tasks/%s/run" % sid, ad, {})
    chk(s == 200 and r.get("queued") == 2, "elle çalıştır → 2 cihaz kuyruğa (%s)" % r)
    rows = asyncio.run(
        q("SELECT created_by FROM tasks WHERE target_pc = ANY($1::text[]) AND script_path='echo ft'", PCS)
    )
    chk(len(rows) == 2 and all("ftadmin" in (x["created_by"] or "") for x in rows), "görevin sahibi kaydedildi")
    s, _ = req("/api/scheduled_tasks/%s/toggle" % w["id"], ad, {"enabled": False})
    chk(s == 200, "haftalık görev durduruldu")
    # Zamanlayıcı turu: günlük görevin zamanı geçmiş gibi yap, kuyruğa eklemesini bekle
    asyncio.run(q("DELETE FROM tasks WHERE target_pc = ANY($1::text[])", PCS))
    asyncio.run(q("UPDATE scheduled_tasks SET next_run = now() - interval '1 minute' WHERE id = $1", sid))
    deadline = time.time() + 45
    n = 0
    while time.time() < deadline:
        n = len(asyncio.run(q("SELECT 1 FROM tasks WHERE target_pc = ANY($1::text[])", PCS)))
        if n:
            break
        time.sleep(2)
    after = asyncio.run(q("SELECT next_run, last_run, last_result FROM scheduled_tasks WHERE id = $1", sid))[0]
    chk(
        n == 2 and after["last_run"] and after["next_run"] > after["last_run"],
        "zamanlayıcı vakti gelen görevi kuyruğa ekledi ve sonraki çalışmayı ileri aldı (%s)" % after["last_result"],
    )
    s, lst = req("/api/scheduled_tasks", ad)
    chk(s == 200 and len([i for i in lst["items"] if i["name"].startswith("FT ")]) >= 2, "liste")
    for i in lst["items"]:
        if i["name"].startswith("FT "):
            req("/api/scheduled_tasks/%s" % i["id"], ad, method="DELETE")
    audit = asyncio.run(
        q(
            "SELECT count(*) AS n FROM device_audit_logs WHERE action IN "
            "('schedule_create','schedule_run','schedule_run_now','schedule_delete')"
        )
    )
    chk(audit[0]["n"] >= 4, "zamanlanmış görev işlemleri hash-zincirli denetimde")

    print("== webhook SSRF koruması")
    from pops import config as pcfg
    from pops import notify as pnotify

    pcfg.NOTIFY_WEBHOOK_ALLOW_PRIVATE = False
    blocked = ["127.0.0.1", "10.1.2.3", "192.168.1.5", "169.254.169.254", "100.64.0.1", "::1", "fe80::1", "0.0.0.0"]
    chk(not any(pnotify._addr_allowed(ip) for ip in blocked), "iç/yerel/metadata adresleri reddedilir")
    chk(pnotify._addr_allowed("1.1.1.1") and pnotify._addr_allowed("2606:4700::1111"), "genel adreslere izin var")
    try:
        pnotify.resolve_webhook("http://localhost:9/hook")
        chk(False, "localhost webhook reddedilmeli")
    except ValueError:
        chk(True, "localhost adına çözülen webhook reddedildi")
    pcfg.NOTIFY_WEBHOOK_ALLOW_PRIVATE = True
    chk(
        pnotify._addr_allowed("10.1.2.3") and not pnotify._addr_allowed("224.0.0.1"), "bilerek açılınca iç ağa izin var"
    )

    print("== bildirimler")
    srv = HTTPServer(("127.0.0.1", 0), Hook)
    threading.Thread(target=srv.serve_forever, daemon=True).start()
    hook_url = "http://127.0.0.1:%d/hook" % srv.server_address[1]
    chk(req("/api/system/notify-settings", ad)[0] == 403, "admin ayarları göremez (superadmin)")
    bad = {"enabled": True, "min_severity": "high", "email_to": "yanlis-adres", "webhook_url": ""}
    chk(req("/api/system/notify-settings", sa, bad)[0] == 400, "geçersiz e-posta → 400")
    chk(
        req("/api/system/notify-settings", sa, {**bad, "email_to": "", "webhook_url": "file:///etc/passwd"})[0] == 400,
        "http(s) olmayan webhook → 400",
    )
    good = {"enabled": True, "min_severity": "high", "email_to": "", "webhook_url": hook_url}
    s, st = req("/api/system/notify-test", sa, good)
    chk(
        s == 200 and st.get("channels") == ["webhook"] and Hook.received and Hook.received[-1].get("event") == "test",
        "test bildirimi webhook'a ulaştı",
    )
    s, st = req("/api/system/notify-settings", sa, good)
    chk(s == 200 and st.get("enabled") and st.get("webhook_url") == hook_url, "ayarlar kaydedildi")
    before = len(Hook.received)
    chk(
        req(
            "/api/policy_alert",
            body={"hw_id": "HW-FT1", "domain": "ft-test.example", "category": "kumar"},
            headers=agent1,
        )[0]
        == 200,
        "kural ihlali bildirildi",
    )
    req("/api/policy_alert", body={"hw_id": "HW-FT1", "domain": "ft-test.example", "category": "kumar"}, headers=agent1)
    time.sleep(2)
    got = [h for h in Hook.received[before:] if h.get("event") == "policy_alert"]
    chk(
        len(got) == 1 and "kumar" in got[0].get("title", "") and "ft-test.example" in got[0].get("text", ""),
        "yüksek önemli olay webhook'a gitti, tekrarı süzüldü",
    )
    s, lst = req("/api/notifications", ad)
    mine = [n for n in lst.get("items", []) if n["event"] == "policy_alert" and n["pc_name"] == "HW-FT1"]
    s_anon = req("/api/policy_alert", body={"hw_id": "HW-FT2", "domain": "sahte.example", "category": "kumar"})[0]
    time.sleep(1)
    s, lst2 = req("/api/notifications", ad)
    chk(
        s_anon == 200 and not [n for n in lst2.get("items", []) if n["pc_name"] == "HW-FT2"],
        "anahtarsız ihlal bildirimi kaydedilir ama bildirim üretmez",
    )
    chk(s == 200 and mine and not mine[0]["is_read"] and lst.get("unread", 0) >= 1, "zil listesinde okunmamış")
    chk(mine and mine[0].get("channels") == "webhook", "kaydın gönderildiği kanal işlendi")
    chk(req("/api/notifications", vw)[0] == 403, "viewer bildirim listesini göremez")
    req("/api/notifications/read", ad, {"ids": []})
    s, lst = req("/api/notifications", ad)
    chk(lst.get("unread") == 0, "hepsi okundu işaretlendi")
    req("/api/system/notify-settings", sa, {**good, "enabled": False, "webhook_url": ""})
    srv.shutdown()

    print("== yazılım envanteri")
    items = {
        "items": [
            {"name": "Mozilla Firefox", "version": "130.0", "publisher": "Mozilla"},
            {"name": "=HYPERLINK(\"http://x\")", "version": "1"},
            {"name": "Mozilla Firefox", "version": "130.0", "publisher": "Mozilla"},
        ]
    }
    chk(req("/api/software/HW-FT1", body=items)[0] == 401, "anahtarsız ajan → 401 (enforce kapalı olsa bile)")
    chk(req("/api/software/HW-FT2", body=items, headers=agent1)[0] == 403, "başka cihaz adına → 403")
    s, r = req("/api/software/HW-FT1", body=items, headers=agent1)
    chk(s == 200 and r.get("count") == 2, "liste kaydedildi, tekrar eden satır birleşti")
    s, r = req("/api/software?q=firefox", vw)
    chk(s == 200 and r["items"] and r["items"][0]["devices"] == 1, "filoda yazılım arama")
    s, r = req("/api/devices/HW-FT1/software", vw)
    chk(s == 200 and len(r) == 2, "cihazın yazılım listesi")
    s, csv_text = req("/api/reports/export?kind=software", ad, raw=True)
    chk(
        s == 200 and "'=HYPERLINK" in csv_text and "\n=HYPERLINK" not in csv_text and ";=HYPERLINK" not in csv_text,
        "CSV'de formül enjeksiyonu etkisiz",
    )

    print("== yama durumu")
    patch = {
        "pending_count": 3,
        "pending_security": 2,
        "pending_critical": 1,
        "reboot_required": True,
        "last_search": "2026-09-27T10:00:00Z",
        "updates": [
            {"kb": "KB5030211", "title": "2026-09 Cumulative Update", "severity": "Critical", "is_security": True}
        ],
    }
    chk(req("/api/patches/HW-FT1", body=patch)[0] == 401, "anahtarsız ajan → 401")
    chk(req("/api/patches/HW-FT1", body=patch, headers=agent1)[0] == 200, "yama durumu kaydedildi")
    s, lst = req("/api/patches", vw)
    me = [x for x in lst if x["pc_name"] == "HW-FT1"]
    other = [x for x in lst if x["pc_name"] == "HW-FT2"]
    chk(s == 200 and me and me[0]["pending_security"] == 2 and me[0]["updates"][0]["kb"] == "KB5030211", "yama listesi")
    chk(other and other[0]["reported"] is False, "bildirmeyen cihaz 'bildirmedi' olarak listede")
    chk(req("/api/patches/install", vw, {"target_mode": "PC", "targets": ["HW-FT1"]})[0] == 403, "viewer kurduramaz")
    s, r = req("/api/patches/install", ad, {"target_mode": "LAB", "targets": ["FT-LAB"], "scope": "security"})
    chk(s == 200 and sorted(r.get("skipped_offline", [])) == PCS, "kurulum emri: çevrimdışılar atlandı")
    chk(
        req("/api/patches/install", ad, {"target_mode": "PC", "targets": ["HW-FT1"], "scope": "hepsi"})[0] == 400,
        "geçersiz kapsam → 400",
    )

    print("== raporlar")
    s, rep = req("/api/reports/summary?days=7", vw)
    chk(
        s == 200
        and rep["devices"]["total"] >= 2
        and rep["patches"]["pending_security"] >= 1
        and rep["software"]["reporting_devices"] >= 1,
        "özet rapor",
    )
    chk(any(p.get("domain") == "ft-test.example" for p in rep["events"]["top_policy"]), "en çok ihlal edilen alan adı")
    for kind in ("devices", "patches", "events"):
        s, txt = req("/api/reports/export?kind=%s" % kind, ad, raw=True)
        chk(s == 200 and txt.startswith("﻿"), "CSV: %s" % kind)
    chk(req("/api/reports/export?kind=users", ad)[0] == 400, "bilinmeyen rapor türü → 400")

    print("== ajanın bildirdiği karantina durumu")
    log_q = {
        "log_type": "Security",
        "message": "eşik",
        "event_type": "agent.auto_quarantine",
        "action": "auto_quarantine",
        "risk_level": "high",
        "reason": "3 ihlal",
    }
    req("/api/logs/HW-FT2", body=log_q)
    st = asyncio.run(q("SELECT is_quarantined FROM clients WHERE pc_name = 'HW-FT2'"))[0]["is_quarantined"]
    chk(not st, "anahtarsız 'auto_quarantine' olayı karantina bayrağını değiştirmez")
    chk(
        req("/api/logs/HW-FT1", body=log_q, headers=agent1)[0] == 200,
        "anahtarlı ajan kendini karantinaya aldığını bildirdi",
    )
    st = asyncio.run(q("SELECT is_quarantined FROM clients WHERE pc_name = 'HW-FT1'"))[0]["is_quarantined"]
    s, lst = req("/api/notifications", ad)
    chk(
        st is True and any(n["event"] == "auto_quarantine" and n["pc_name"] == "HW-FT1" for n in lst.get("items", [])),
        "panelde karantinada görünür ve bildirim üretildi",
    )
    req(
        "/api/logs/HW-FT1",
        body={**log_q, "event_type": "agent.offline_bypass", "action": "offline_bypass"},
        headers=agent1,
    )
    st = asyncio.run(q("SELECT is_quarantined FROM clients WHERE pc_name = 'HW-FT1'"))[0]["is_quarantined"]
    chk(st is False, "bypass sonrası karantina bayrağı kalktı")

    print("== politikalar ve oto-kayıt")
    pol = {"fair_use_text": "ft", "dns_categories": ["kumar"], "auto_quarantine": False, "quarantine_threshold": 3}
    s, _ = req(
        "/api/agent_policies",
        ad,
        {**pol, "dns_domains": {"kumar": ["HTTPS://WWW.Bahis.example/giris", "*.bahis.example", "bahis.example."]}},
    )
    s2, got = req("/api/agent_policies")
    chk(
        s == 200 and got.get("dns_domains", {}).get("kumar") == ["www.bahis.example", "bahis.example"],
        "alan adları temizlendi (şema/yol/joker/nokta, tekrar) %s" % got.get("dns_domains"),
    )
    req("/api/agent_policies", ad, pol)
    s2, got = req("/api/agent_policies")
    chk(
        got.get("dns_domains", {}).get("kumar") == ["www.bahis.example", "bahis.example"],
        "dns_domains göndermeyen kayıt listeyi silmedi",
    )
    req("/api/agent_policies", ad, {**pol, "dns_domains": {}})
    chk(
        req("/api/set_auto_enroll", ad, {"target_lab": "FT-LAB", "expire_date": "yarin"})[0] == 400,
        "oto-kayıt: bozuk tarih → 400",
    )
    s, _ = req("/api/set_auto_enroll", ad, {"target_lab": "FT-LAB", "expire_date": "2099-12-31"})
    row = asyncio.run(q("SELECT value FROM global_settings WHERE key='auto_enroll_lab'"))
    chk(
        s == 200 and json.loads(row[0]["value"]) == {"lab": "FT-LAB", "until": "2099-12-31"},
        "oto-kayıt tarihiyle saklandı",
    )
    asyncio.run(q("DELETE FROM global_settings WHERE key='auto_enroll_lab'"))

    # temizlik
    asyncio.run(q("DELETE FROM device_software WHERE pc_name = ANY($1::text[])", PCS))
    asyncio.run(q("DELETE FROM device_patch_status WHERE pc_name = ANY($1::text[])", PCS))
    asyncio.run(q("DELETE FROM tasks WHERE target_pc = ANY($1::text[])", PCS))

    if FAILS:
        print("BASARISIZ: %d kontrol" % len(FAILS))
        sys.exit(1)
    print("TUM OZELLIK TESTLERI GECTI")


if __name__ == "__main__":
    main()
