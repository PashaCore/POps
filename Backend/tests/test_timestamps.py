"""Zaman damgaları (0031: TEXT -> TIMESTAMPTZ) — entegrasyon testi (CI 'security' job'ı).

1. Migration, eski biçimli veriyle: geçici bir şemada (search_path) 0031'den önceki migration'lar kurulur; eski kod
   gibi yerel saatli 'YYYY-AA-GG SS:DD:ss' metinleri ve bu metinlerle özetlenmiş bir denetim zinciri yazılır. Sonra
   0031 ve sonrası pops.tz = Europe/Istanbul ile uygulanır. Denetlenen: satır sayıları, sütun türleri, indeksler, bir
   değerin doğru ana çevrildiği, bozuk metnin NULL olduğu, saat diliminin sabitlendiği ve migration'dan ÖNCE kurulan
   zincirin (audit_verify.py ile aynı yolla) doğrulandığı; yeni kayıt eklenince de doğrulandığı, kurcalanınca kırıldığı.
2. Çalışan sunucu: API zamanları ofsetli ISO 8601, gün süzgeçleri ve günlük gruplar sunucunun saat diliminde, CSV'de
   okunur yerel saat, heartbeat ve görev zamanları (güç komutu dahil) gerçek zaman damgası; sonradan eklenen tablolar
   (sınav, dosya aktarımı, SSO, GLPI) dahil dilimsiz ya da metin zaman sütunu kalmadı.

Ortam: POPS_TEST_HTTP + DB_* + JWT_SECRET (sunucuyla aynı).
"""

import asyncio
import csv
import datetime
import hashlib
import io
import json
import os
import sys
import urllib.error
import urllib.request

sys.path.insert(0, os.path.join(os.path.dirname(__file__), os.pardir))
import asyncpg  # noqa: E402
import websockets  # noqa: E402

import migrate  # noqa: E402
import server  # noqa: E402  (create_jwt)
from pops import auditchain, timeutil  # noqa: E402

HTTP = os.environ["POPS_TEST_HTTP"]
WS = os.environ.get("POPS_TEST_WS", HTTP.replace("http", "ws", 1))
SCHEMA = "pops_tzmig_%d" % os.getpid()
PCS = ["HW-TS1"]
FAILS = []
IST = timeutil.tzinfo_for("Europe/Istanbul")
UTC = datetime.timezone.utc


def chk(cond, msg):
    print(("  OK   " if cond else "  FAIL ") + msg)
    if not cond:
        FAILS.append(msg)


def req(path, token=None, body=None, raw=False, method=None):
    data = json.dumps(body).encode() if body is not None else None
    r = urllib.request.Request(HTTP + path, data=data, method=method or ("POST" if body is not None else "GET"))
    if body is not None:
        r.add_header("Content-Type", "application/json")
    if token:
        r.add_header("Authorization", "Bearer " + token)
    try:
        with urllib.request.urlopen(r, timeout=40) as resp:
            payload = resp.read()
            return resp.status, (payload.decode("utf-8-sig") if raw else json.loads(payload or b"{}"))
    except urllib.error.HTTPError as e:
        return e.code, {}


async def conn():
    return await asyncpg.connect(
        host=os.environ.get("DB_HOST", "localhost"), port=int(os.environ.get("DB_PORT", "5432")),
        user=os.environ["DB_USER"], password=os.environ["DB_PASS"], database=os.environ["DB_NAME"],
    )


# ── 1. Migration ──────────────────────────────────────────────────────────────
OLD_TABLES = ("tasks", "agent_logs_v2", "agent_logs", "device_audit_logs", "clients", "hw_inventory", "agent_versions",
              "users", "enterprise_audit_logs", "agent_bypass_keys")
CONVERTED = (("tasks", "created_at"), ("agent_logs_v2", "timestamp"), ("agent_logs", "timestamp"),
             ("device_audit_logs", "timestamp"), ("clients", "last_seen"), ("hw_inventory", "last_updated"),
             ("agent_versions", "last_update"), ("users", "last_login"), ("enterprise_audit_logs", "start_time"),
             ("enterprise_audit_logs", "end_time"), ("agent_bypass_keys", "issued_at"),
             ("agent_bypass_keys", "confirmed_at"))


async def _apply(c, versions):
    for version, path in migrate._discover():
        if version in versions:
            with open(path, encoding="utf-8") as f:
                sql = f.read()
            async with c.transaction():
                await c.execute(sql)


async def _seed_old(c):
    """0031 öncesi şemaya eski kodun yazdığı gibi metin zamanlar ve metinle özetlenmiş bir denetim zinciri."""
    await c.execute(
        "INSERT INTO tasks (target_pc, script_path, status, created_at) VALUES "
        "('HW-OLD1', 'echo a', 'Completed', '2026-10-05 11:58:11'), ('HW-OLD1', 'echo b', 'Completed', "
        "'2026-01-15 00:30:00'), ('HW-OLD1', 'echo c', 'Pending', 'bozuk'), ('HW-OLD1', 'echo d', 'Pending', NULL)")
    await c.execute(
        "INSERT INTO agent_logs_v2 (pc_name, event_type, message, \"timestamp\") VALUES "
        "('HW-OLD1', 'auth.login', 'l1', '2026-10-04 23:59:59'), "
        "('HW-OLD1', 'auth.login', 'l2', '2026-10-05 00:00:00')")
    await c.execute("INSERT INTO agent_logs (pc_name, log_type, message, \"timestamp\") VALUES "
                    "('HW-OLD1', 'System', 'eski', '2025-06-01 12:00:00')")
    await c.execute("INSERT INTO clients (pc_name, lab_name, last_seen, status) VALUES "
                    "('HW-OLD1', 'Lab', '2026-10-05 11:58:11', 'Offline')")
    await c.execute("INSERT INTO hw_inventory (pc_name, last_updated) VALUES ('HW-OLD1', '2026-10-05 10:00:00')")
    await c.execute("INSERT INTO agent_versions (pc_name, version, last_update) VALUES "
                    "('HW-OLD1', '0.1.14-alpha', '2026-10-05 09:00:00')")
    await c.execute("INSERT INTO users (username, password_hash, last_login) VALUES "
                    "('tzold', 'x', '2026-10-05 08:00:00')")
    await c.execute("INSERT INTO enterprise_audit_logs (session_id, target_pc, start_time, end_time, status) VALUES "
                    "('SES-OLD', 'HW-OLD1', '2026-10-05 11:00:00', '2026-10-05 11:05:30', 'Ended')")
    await c.execute("INSERT INTO agent_bypass_keys (pc_name, secret, fingerprint, issued_at) VALUES "
                    "('HW-OLD1', 's', 'f', '2026-10-05 07:00:00')")
    # 0004 öncesi satır (özetsiz) + eski kuralla (metin zaman) özetlenmiş zincir
    await c.execute("INSERT INTO device_audit_logs (hw_id, action, reason, changes, \"timestamp\") VALUES "
                    "('HW-OLD1', 'legacy', 'eski', '{}', '2025-01-01 10:00:00')")
    prev = None
    for i, ts in enumerate(("2026-10-05 11:58:11", "2026-10-05 12:00:00", "2026-01-15 00:30:00")):
        changes = json.dumps({"i": i}, ensure_ascii=False)
        entry = auditchain.entry_hash(prev, "HW-OLD1", "lockdown", "neden %d" % i, changes, ts)
        await c.execute(
            "INSERT INTO device_audit_logs (hw_id, action, reason, changes, \"timestamp\", prev_hash, entry_hash) "
            "VALUES ($1, 'lockdown', $2, $3, $4, $5, $6)", "HW-OLD1", "neden %d" % i, changes, ts, prev, entry)
        prev = entry
    return prev


async def test_migration():
    print("== 0031: eski biçimli verinin migration'ı")
    versions = [v for v, _ in migrate._discover()]
    target = next(v for v in versions if v.endswith("_timestamptz"))
    before = [v for v in versions if v < target]
    after = [v for v in versions if v >= target]
    c = await conn()
    try:
        await c.execute("DROP SCHEMA IF EXISTS %s CASCADE" % SCHEMA)
        await c.execute("CREATE SCHEMA %s" % SCHEMA)
        await c.execute("SET search_path TO %s" % SCHEMA)
        await _apply(c, before)
        last_hash = await _seed_old(c)
        counts = {t: await c.fetchval("SELECT count(*) FROM %s" % t) for t in OLD_TABLES}
        index_sql = "SELECT indexname FROM pg_indexes WHERE schemaname = $1"
        old_index = {r["indexname"] for r in await c.fetch(index_sql, SCHEMA)}

        await c.execute("SET TimeZone = 'UTC'")   # veritabanının ayarı farklı: pops.tz kazanmalı
        await c.execute("SELECT set_config('pops.tz', 'Europe/Istanbul', false)")
        await _apply(c, after)

        chk({t: await c.fetchval("SELECT count(*) FROM %s" % t) for t in OLD_TABLES} == counts,
            "satır sayıları değişmedi (%s)" % counts)
        types = {(r["table_name"], r["column_name"]): r["data_type"] for r in await c.fetch(
            "SELECT table_name, column_name, data_type FROM information_schema.columns WHERE table_schema = $1",
            SCHEMA)}
        bad = [k for k in CONVERTED if types.get(k) != "timestamp with time zone"]
        chk(not bad, "sütunlar TIMESTAMPTZ (%s)" % (bad or "hepsi"))
        chk(types.get(("device_software", "install_date")) == "text", "ajanın ham install_date'i TEXT kaldı")
        new_index = {r["indexname"] for r in await c.fetch(index_sql, SCHEMA)}
        chk(old_index <= new_index, "indeksler yerinde (yeniden kuruldu)")
        idx = await c.fetchval(
            "SELECT format_type(a.atttypid, a.atttypmod) FROM pg_index i JOIN pg_class x ON x.oid = i.indexrelid "
            "JOIN pg_namespace n ON n.oid = x.relnamespace JOIN pg_attribute a ON a.attrelid = i.indrelid "
            "AND a.attnum = i.indkey[0] WHERE x.relname = 'idx_agent_logs_v2_ts' AND n.nspname = $1", SCHEMA)
        chk(idx == "timestamp with time zone", "idx_agent_logs_v2_ts yeni türde (%s)" % idx)

        rows = await c.fetch("SELECT script_path, created_at FROM tasks ORDER BY id")
        got = {r["script_path"]: r["created_at"] for r in rows}
        chk(got["echo a"] == datetime.datetime(2026, 10, 5, 8, 58, 11, tzinfo=UTC),
            "'2026-10-05 11:58:11' İstanbul saati = 08:58:11 UTC (%s)" % got["echo a"])
        chk(got["echo b"] == datetime.datetime(2026, 1, 14, 21, 30, tzinfo=UTC),
            "kış tarihi de +03 (%s)" % got["echo b"])
        chk(got["echo c"] is None and got["echo d"] is None, "bozuk ya da boş metin NULL")
        chk(await c.fetchval("SELECT last_seen FROM clients") == datetime.datetime(2026, 10, 5, 8, 58, 11, tzinfo=UTC),
            "clients.last_seen çevrildi")
        chk(await c.fetchval("SELECT end_time - start_time FROM enterprise_audit_logs")
            == datetime.timedelta(minutes=5, seconds=30), "Vision oturumu başlangıç/bitiş çevrildi")
        chk(await c.fetchval("SELECT issued_at FROM agent_bypass_keys")
            == datetime.datetime(2026, 10, 5, 7, 0, tzinfo=UTC),
            "saat dilimsiz bypass anahtarı zamanı oturumun saat diliminde (burada UTC) okundu")
        day = await c.fetch("SELECT message FROM agent_logs_v2 WHERE \"timestamp\" >= $1 ORDER BY id",
                            datetime.datetime(2026, 10, 5, tzinfo=IST))
        chk([r["message"] for r in day] == ["l2"], "gün sınırı İstanbul gece yarısında")
        chk(await c.fetchval("SELECT value FROM global_settings WHERE key = 'audit_time_zone'") == "Europe/Istanbul",
            "denetim saat dilimi sabitlendi")
        chk(await c.fetchval("SELECT column_default FROM information_schema.columns WHERE table_schema = $1 "
                             "AND table_name = 'tasks' AND column_name = 'created_at'", SCHEMA) == "now()",
            "tasks.created_at varsayılanı now()")

        # Denetim zinciri: audit_verify.py ile aynı yol (saat dilimi veritabanından)
        timeutil.configure(name="UTC")   # sunucunun dilimi başka olsa da sabit denetim dilimi kullanılır
        await timeutil.configure_from_db(c)
        res = await auditchain.verify_batched(c.fetch)
        chk(res["ok"] and res["checked"] == 3 and res["total"] == 4,
            "migration'dan önce kurulan zincir doğrulanıyor (%s)" % res)
        ts = datetime.datetime(2026, 10, 6, 9, 0, tzinfo=IST)
        entry = auditchain.entry_hash(last_hash, "HW-OLD1", "unlock", "yeni", "{}", ts)
        await c.execute("INSERT INTO device_audit_logs (hw_id, action, reason, changes, \"timestamp\", prev_hash, "
                        "entry_hash) VALUES ('HW-OLD1', 'unlock', 'yeni', '{}', $1, $2, $3)", ts, last_hash, entry)
        res = await auditchain.verify_batched(c.fetch)
        chk(res["ok"] and res["checked"] == 4, "eski ve yeni kayıtlar birlikte doğrulanıyor (%s)" % res)
        await c.execute("UPDATE device_audit_logs SET \"timestamp\" = \"timestamp\" + interval '1 second' "
                        "WHERE action = 'lockdown' AND reason = 'neden 1'")
        res = await auditchain.verify_batched(c.fetch)
        chk(not res["ok"], "zamanı kurcalanan eski kayıt zinciri kırar (%s)" % res)
    finally:
        await c.execute("RESET search_path")
        await c.execute("DROP SCHEMA IF EXISTS %s CASCADE" % SCHEMA)
        await c.close()


# ── 2. Çalışan sunucu ─────────────────────────────────────────────────────────
def _parse(v):
    try:
        d = datetime.datetime.fromisoformat(v)
    except (TypeError, ValueError):
        return None
    return d if d.tzinfo else None


async def test_server():
    print("== çalışan sunucu: API ve CSV zamanları")
    c = await conn()
    timeutil.configure()
    try:
        await cleanup(c)
        await c.execute("INSERT INTO users (username, password_hash, role, permissions, token_version) VALUES "
                        "('tsadmin', 'x', 'admin', '[]', 0), ('tssuper', 'x', 'superadmin', '[]', 0) "
                        "ON CONFLICT (username) DO UPDATE SET token_version = 0")
        await c.execute("INSERT INTO clients (pc_name, hostname, lab_name, status, last_seen) VALUES "
                        "('HW-TS1', 'ts1', 'TS-Lab', 'Offline', now() - interval '2 hours')")
        admin = server.create_jwt("tsadmin", "admin", 0)
        sup = server.create_jwt("tssuper", "superadmin", 0)
        now = datetime.datetime.now(UTC)

        s, b = req("/api/deploy_orchestration", admin, {"target_mode": "PC", "targets": PCS, "taskSequence": [
            {"name": "ts", "type": "CMD", "command": "echo ts"}]})
        chk(s == 200 and b.get("created") == 1, "görev oluşturuldu")
        stored = await c.fetchval("SELECT created_at FROM tasks WHERE id = $1", (b.get("task_ids") or [0])[0])
        chk(isinstance(stored, datetime.datetime) and abs((stored - now).total_seconds()) < 60,
            "tasks.created_at gerçek zaman damgası (%s)" % stored)
        s, tasks = req("/api/tasks?limit=50", admin)
        mine = [t for t in tasks if t.get("target_pc") == "HW-TS1"] if s == 200 else []
        at = _parse(mine[0]["created_at"]) if mine else None
        chk(at is not None and abs((at - now).total_seconds()) < 60 and "T" in mine[0]["created_at"],
            "API: created_at ofsetli ISO 8601 (%s)" % (mine[0]["created_at"] if mine else None))
        chk(at is not None and at.utcoffset() == datetime.datetime.now(timeutil.zone()).utcoffset(),
            "ofset sunucunun saat dilimi")
        s, devs = req("/api/devices", admin)
        dev = next((d for d in devs if d["hw_id"] == "HW-TS1"), {}) if s == 200 else {}
        seen = _parse(dev.get("last_seen"))
        chk(seen is not None and abs((now - seen).total_seconds() - 7200) < 120, "last_seen ISO 8601 (%s)"
            % dev.get("last_seen"))

        # Günler sunucunun saat diliminde: bugün 00:00:30'da yazılan kayıt bugüne, 23:59:30'da yazılan düne düşer
        today = timeutil.today()
        start = timeutil.day_start(today)
        await c.execute(
            "INSERT INTO agent_logs_v2 (pc_name, event_type, risk_level, message, \"timestamp\") VALUES "
            "('HW-TS1', 'auth.login', 'high', 'ts-bugun', $1), ('HW-TS1', 'auth.login', 'high', 'ts-dun', $2)",
            start + datetime.timedelta(seconds=30), start - datetime.timedelta(seconds=30))
        s, logs = req("/api/logs?pc=HW-TS1&since=%s&until=%s" % (today, today), admin)
        chk(s == 200 and [x["message"] for x in logs] == ["ts-bugun"], "/api/logs gün süzgeci sunucu diliminde")
        chk(s == 200 and logs and _parse(logs[0]["timestamp"]) == start + datetime.timedelta(seconds=30),
            "/api/logs timestamp ISO 8601")
        s, summary = req("/api/reports/summary?days=2", admin)
        by_day = {d["day"]: d for d in summary.get("events", {}).get("by_day", [])} if s == 200 else {}
        chk(by_day.get(today.isoformat(), {}).get("high", 0) >= 1, "rapor günleri sunucu diliminde (%s)" % by_day)
        s, text = req("/api/reports/export?kind=events&days=2", admin, raw=True)
        rows = list(csv.reader(io.StringIO(text), delimiter=";")) if s == 200 else []
        mine = [r for r in rows if len(r) > 6 and r[6] == "ts-bugun"]
        chk(mine and mine[0][0] == timeutil.local_text(start + datetime.timedelta(seconds=30)),
            "CSV: okunur yerel saat (%s)" % (mine[0][0] if mine else None))
        s, text = req("/api/reports/export?kind=devices", admin, raw=True)
        rows = list(csv.reader(io.StringIO(text), delimiter=";")) if s == 200 else []
        dev_row = next((r for r in rows if r and r[0] == "HW-TS1"), None)
        chk(dev_row is not None and len(dev_row[5]) == 19 and dev_row[5][10] == " ", "CSV last_seen yerel metin (%s)"
            % (dev_row[5] if dev_row else None))

        s, _ = req("/api/security/lockdown", admin, {"target_pc": "HW-TS1", "reason": "ts"})
        audit_ts = await c.fetchval("SELECT \"timestamp\" FROM device_audit_logs WHERE hw_id = 'HW-TS1' "
                                    "ORDER BY id DESC LIMIT 1")
        chk(s == 200 and isinstance(audit_ts, datetime.datetime) and audit_ts.microsecond == 0,
            "denetim kaydı saniye hassasiyetinde zaman damgası (%s)" % audit_ts)
        s, res = req("/api/system/audit-verify", sup)
        chk(s == 200 and res.get("ok") and res.get("checked", 0) >= 1, "çalışan sunucuda denetim zinciri sağlam (%s)"
            % res)

        # Sonradan gelen özellikler de aynı biçimde: cihaz listesi sürümü, dosya aktarımı, sınav modu
        s, full = req("/api/devices?since=0", admin)
        row = next((d for d in full.get("devices", []) if d["hw_id"] == "HW-TS1"), {}) if s == 200 else {}
        chk(_parse(row.get("last_seen")) is not None, "cihaz listesi sürümü (?since=): last_seen ISO 8601 (%s)"
            % row.get("last_seen"))
        await c.execute("INSERT INTO file_transfers (transfer_id, direction, pc_name, name, reason, status) "
                        "VALUES ('tstimestamps1', 'pull', 'HW-TS1', 'a.txt', 'ts', 'sent')")
        s, files = req("/api/files?pc=HW-TS1", admin)
        item = (files.get("items") or [{}])[0] if s == 200 else {}
        chk(_parse(item.get("created_at")) is not None, "dosya aktarımı: created_at ISO 8601 (%s)"
            % item.get("created_at"))
        await c.execute("INSERT INTO custom_labs (lab_name) VALUES ('TS-Lab') ON CONFLICT DO NOTHING")
        s, _ = req("/api/labs/TS-Lab/exam", admin, {"allow": ["sinav.meb.gov.tr"], "duration_minutes": 30,
                                                    "reason": "ts yazılı"})
        s2, exams = req("/api/exams?lab=TS-Lab", admin)
        ex = (exams.get("items") or [{}])[0] if s2 == 200 else {}
        chk(s == 200 and _parse(ex.get("started_at")) is not None and _parse(ex.get("until_at")) is not None,
            "sınav modu: started_at / until_at ISO 8601 (%s)" % ex.get("started_at"))
        req("/api/labs/TS-Lab/exam", admin, method="DELETE")

        # Güç komutu görevi (ajan bağlıyken açılır): created_at gerçek zaman damgası, API'de ISO 8601
        await c.execute("INSERT INTO agent_secrets (pc_name, secret_hash) VALUES ('HW-TS1', $1) "
                        "ON CONFLICT (pc_name) DO UPDATE SET secret_hash = $1",
                        hashlib.sha256(b"HW-TS1-s").hexdigest())
        ws = await websockets.connect("%s/ws/agent/HW-TS1" % WS, additional_headers={
            "X-Agent-Secret": "HW-TS1-s", "X-Agent-Version": "0.1.23-alpha", "X-Agent-Features": "power,message"})
        try:
            await ws.send(json.dumps({"hw_id": "HW-TS1", "hostname": "ts1", "status": "Online", "dna_payload": {
                "hardware": {"uuid": "TS1-U", "bios_sn": "TS1-B", "disk_sn": "-", "mac": "-", "ram_sn": "-"},
                "capabilities": {}}}))
            out = {}
            for _ in range(25):
                await asyncio.sleep(0.2)
                s, out = req("/api/devices/power", admin, {"target_mode": "PC", "targets": ["HW-TS1"], "op": "lock"})
                if s == 200 and out.get("created"):
                    break
            tid = (out.get("task_ids") or [0])[0]
            stored = await c.fetchval("SELECT created_at FROM tasks WHERE id = $1", tid)
            chk(isinstance(stored, datetime.datetime) and abs((stored - datetime.datetime.now(UTC)).total_seconds())
                < 60, "güç komutu: tasks.created_at gerçek zaman damgası (%s)" % stored)
            s, tasks = req("/api/tasks?limit=50", admin)
            mine = [t for t in tasks if t.get("id") == tid] if s == 200 else []
            chk(mine and _parse(mine[0]["created_at"]) is not None, "güç komutu görevi API'de ISO 8601 (%s)"
                % (mine[0]["created_at"] if mine else None))
        finally:
            await ws.close()

        # Sonradan eklenen tablolar dahil: dilimsiz timestamp ya da metin tarih sütunu kalmadı (ajanın bildirdiği
        # yazılım kurulum tarihi metin kalır: biçimi ajana göre değişir)
        left = [r["c"] for r in await c.fetch(
            "SELECT table_name || '.' || column_name AS c FROM information_schema.columns WHERE table_schema = "
            "'public' AND ((data_type LIKE 'timestamp%' AND data_type <> 'timestamp with time zone') OR (data_type "
            "IN ('text', 'character varying') AND column_name ~ '(_at|time|timestamp|date|last_seen|last_login|"
            "last_update|last_updated)$')) ORDER BY 1")]
        chk(left == ["device_software.install_date"], "bütün zaman sütunları TIMESTAMPTZ (%s)" % left)
        types = {r["c"]: r["t"] for r in await c.fetch(
            "SELECT table_name || '.' || column_name AS c, data_type AS t FROM information_schema.columns "
            "WHERE table_schema = 'public' AND table_name IN ('sso_flows', 'sso_providers', 'glpi_links', "
            "'exam_sessions', 'file_transfers') AND data_type LIKE 'timestamp%'")}
        chk(types and set(types.values()) == {"timestamp with time zone"} and "sso_flows.expires_at" in types
            and "glpi_links.synced_at" in types, "yeni tablolar TIMESTAMPTZ (%s)" % sorted(types))
    finally:
        await cleanup(c)
        await c.close()


async def cleanup(c):
    await c.execute("DELETE FROM tasks WHERE target_pc = ANY($1::text[])", PCS)
    await c.execute("DELETE FROM file_transfers WHERE pc_name = ANY($1::text[])", PCS)
    await c.execute("DELETE FROM exam_devices WHERE pc_name = ANY($1::text[])", PCS)
    await c.execute("DELETE FROM exam_sessions WHERE lab_name = 'TS-Lab'")
    await c.execute("DELETE FROM custom_labs WHERE lab_name = 'TS-Lab'")
    await c.execute("DELETE FROM agent_logs_v2 WHERE pc_name = ANY($1::text[])", PCS)
    await c.execute("DELETE FROM clients WHERE pc_name = ANY($1::text[])", PCS)
    await c.execute("DELETE FROM agent_secrets WHERE pc_name = ANY($1::text[])", PCS)
    await c.execute("DELETE FROM agent_versions WHERE pc_name = ANY($1::text[])", PCS)
    await c.execute("DELETE FROM hw_inventory WHERE pc_name = ANY($1::text[])", PCS)
    await c.execute("DELETE FROM notifications WHERE pc_name = ANY($1::text[])", PCS)
    await c.execute("DELETE FROM users WHERE username IN ('tsadmin', 'tssuper')")


async def main():
    await test_migration()
    await test_server()
    if FAILS:
        print("\nBASARISIZ: %d kontrol" % len(FAILS))
        sys.exit(1)
    print("\nTUM ZAMAN DAMGASI TESTLERI GECTI")


if __name__ == "__main__":
    asyncio.run(main())
