"""Tanınmayan alan — entegrasyon testi (CI 'security' job'ı).

- Panelin ve entegrasyonların gövdeleri tanınmayan alanı 422 ile reddeder (extra_forbidden, alanın adıyla) ve hiçbir
  şey yazmaz; aynı gövde fazlalık olmadan kabul edilir.
- Ajanın gönderdiği gövdeler bugünkü ajanın alanlarıyla (envanterdeki "dna" dahil) ve yeni bir sürümün ekleyebileceği
  bir alanla kabul edilir: tanınmayan alan yok sayılır, veri kaybolmaz.

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

import server  # noqa: E402  (create_jwt)

HTTP = os.environ["POPS_TEST_HTTP"]
PC = "HW-SI0001"
SECRET = "si-secret-0001"
LAB = "SI-Lab"
FAILS = []


def chk(cond, msg):
    print(("  OK   " if cond else "  FAIL ") + msg)
    if not cond:
        FAILS.append(msg)


def req(path, token=None, body=None, method=None, headers=None):
    """(durum, gövde)"""
    data = json.dumps(body).encode() if body is not None else None
    r = urllib.request.Request(HTTP + path, data=data, method=method or ("POST" if body is not None else "GET"))
    if body is not None:
        r.add_header("Content-Type", "application/json")
    for k, v in (headers or {}).items():
        r.add_header(k, v)
    if token:
        r.add_header("Authorization", "Bearer " + token)
    try:
        with urllib.request.urlopen(r, timeout=30) as resp:
            return resp.status, json.loads(resp.read() or b"{}")
    except urllib.error.HTTPError as e:
        try:
            return e.code, json.loads(e.read() or b"{}")
        except ValueError:
            return e.code, {}


def rejected(result, field):
    """422 ve hata listesinde bu alan için extra_forbidden."""
    status, body = result
    errors = body.get("detail") if isinstance(body.get("detail"), list) else []
    return status == 422 and any(e.get("type") == "extra_forbidden" and e.get("loc", [])[-1:] == [field]
                                 for e in errors)


AGENT = {"X-Agent-Id": PC, "X-Agent-Secret": SECRET}

# Bugünkü Windows ajanının gönderdiği gövdeler (alan adları Agent/POps.Agent'tan: Worker.BuildInventoryInternal,
# SoftwareInventory, PatchStatus, Helpdesk, AgentHttp.AgentLogPayload, SessionWatch, DnsPolicyMonitor), her birine
# yeni bir sürümün ekleyebileceği bir alan ("future_field") eklenmiş olarak.
INVENTORY = {
    "hw_id": PC, "hostname": "si-pc-1", "cpu": "Intel i5-8500", "ram": "8 GB", "motherboard": "H310M",
    "gpu": "Intel UHD 630", "os_version": "Windows 10 Pro 22H2", "ip_address": "10.9.0.11",
    "mac_address": "00:11:22:33:44:55", "disk_info": "C: 237 GB",
    "dna": {"os": "Windows 10", "capabilities": {"ram_readable": True, "disk_serial_real": True, "wmi_healthy": True},
            "hardware": {"uuid": "SI-UUID", "bios_sn": "SI-BIOS", "disk_sn": "SI-DISK", "mac": "001122334455",
                         "ram_sn": "SI-RAM"}},
    "future_field": 1,
}
SOFTWARE = {"items": [{"name": "SI Uygulama", "version": "1.2", "publisher": "SI", "install_date": "20260101",
                       "future_field": "x"}], "future_field": True}
PATCHES = {
    "pending_count": 1, "pending_security": 1, "pending_critical": 0, "reboot_required": False,
    "last_search": "2026-10-01T08:00:00Z", "last_install": None, "last_result": None,
    "updates": [{"kb": "KB5000001", "title": "SI güncelleme", "severity": "Important", "categories": ["Security"],
                 "is_security": True, "future_field": 3}],
    "future_field": "wua",
}
TICKET = {"subject": "SI yazıcı", "body": "Yazıcı çıktı vermiyor.", "category": "yazici", "reporter": "ogrenci",
          "future_field": None}
LOG = {"log_type": "System", "message": "SI olay", "actor_id": "Agent", "event_type": "agent.log",
       "category": "legacy", "action": "unknown", "risk_level": "info", "reason": "", "meta_data": {},
       "future_field": "x"}
AUTH = {"hw_id": PC, "hostname": "si-pc-1", "student_id": "si-ogrenci", "message": "", "future_field": "OKUL"}
ALERT = {"hw_id": PC, "domain": "si.example", "category": "kumar", "future_field": 2}


async def conn():
    return await asyncpg.connect(
        host=os.environ.get("DB_HOST", "localhost"), port=int(os.environ.get("DB_PORT", "5432")),
        user=os.environ["DB_USER"], password=os.environ["DB_PASS"], database=os.environ["DB_NAME"],
    )


async def cleanup(c):
    for table, col in (("agent_secrets", "pc_name"), ("clients", "pc_name"), ("hw_inventory", "pc_name"),
                       ("device_software", "pc_name"), ("device_patch_status", "pc_name"), ("tickets", "pc_name")):
        await c.execute("DELETE FROM %s WHERE %s = $1" % (table, col), PC)
    await c.execute("DELETE FROM custom_labs WHERE lab_name LIKE 'SI-%'")
    await c.execute("DELETE FROM users WHERE username IN ('si-super', 'si-yeni')")
    await c.execute("DELETE FROM licenses WHERE name LIKE 'SI %'")


async def main():
    c = await conn()
    await cleanup(c)
    await c.execute("INSERT INTO clients (pc_name, hostname, lab_name, status) VALUES ($1, 'si-pc-1', $2, 'Offline')",
                    PC, LAB)
    await c.execute("INSERT INTO agent_secrets (pc_name, secret_hash) VALUES ($1, $2)", PC,
                    hashlib.sha256(SECRET.encode()).hexdigest())
    await c.execute("INSERT INTO users (username, password_hash, role, permissions, token_version) "
                    "VALUES ('si-super', 'x', 'superadmin', '[]', 0)")
    token = server.create_jwt("si-super", "superadmin", 0)
    try:
        await panel(c, token)
        await agent(c)
    finally:
        await cleanup(c)
        await c.close()
    if FAILS:
        print("\nBASARISIZ: %d kontrol" % len(FAILS))
        sys.exit(1)
    print("\nTUM TANINMAYAN ALAN TESTLERI GECTI")


async def panel(c, token):
    print("== panel ve entegrasyon gövdeleri: tanınmayan alan 422")
    chk(rejected(req("/api/create_lab", token, {"lab_name": "SI-Yeni", "lab": "x"}), "lab")
        and not await c.fetchval("SELECT 1 FROM custom_labs WHERE lab_name = 'SI-Yeni'"),
        "sınıf oluşturma: 422, sınıf yazılmadı")
    chk(req("/api/create_lab", token, {"lab_name": "SI-Yeni"})[0] == 200, "aynı gövde fazlalıksız kabul")
    chk(rejected(req("/api/v1/labs", token, {"lab_name": "SI-Yeni2", "name": "x"}), "name"), "REST adı da (v1) 422")
    chk(rejected(req("/api/move_pcs", token, {"pc_names": [PC], "new_lab": "SI-Yeni", "force": True}), "force")
        and await c.fetchval("SELECT lab_name FROM clients WHERE pc_name = $1", PC) == LAB,
        "toplu taşıma: 422, cihaz yerinde")
    before = await c.fetchval("SELECT value FROM global_settings WHERE key = 'install_profile'")
    chk(rejected(req("/api/system/install-profile", token, {"profile": "org", "reset": True}), "reset")
        and await c.fetchval("SELECT value FROM global_settings WHERE key = 'install_profile'") == before,
        "kurulum profili: 422, profil uygulanmadı")
    chk(rejected(req("/api/modules/vision", token, {"enabled": False, "labb": LAB}), "labb")
        and not await c.fetchval("SELECT 1 FROM module_settings WHERE module_id = 'vision'"),
        "modül ayarı: 422 (yanlış yazılmış lab kurum genelini kapatmaz)")
    chk(rejected(req("/api/system/notify-settings", token, {"enabled": False, "min_severity": "high", "email_to": "",
                                                            "webhook_url": "", "slack": "x"}), "slack"),
        "bildirim ayarları 422")
    chk(rejected(req("/api/system/retention", token, {"retention_days_logs": 30, "retention_days_tasks": 30,
                                                      "retention_days_notifications": 30,
                                                      "retention_days_audit": 1}), "retention_days_audit"),
        "saklama süreleri 422")
    chk(rejected(req("/api/system/enforce-auth", token, {"enabled": False, "force": True}), "force"),
        "kimlik zorlaması 422")
    chk(rejected(req("/api/system/set-capabilities", token, {"pc_name": PC, "terminal_enabled": False,
                                                             "remote": False}), "remote"),
        "yetenek kapatma 422")
    chk(rejected(req("/api/tasks/action", token, {"action": "cancel", "target_mode": "job", "target_id": "x",
                                                  "reason": "y"}), "reason"), "görev işlemi 422")
    chk(rejected(req("/api/scheduled_tasks", token, {"name": "SI", "command": "echo si", "target_mode": "PC",
                                                     "targets": [PC], "schedule_type": "daily",
                                                     "time_of_day": "10:00", "every": 2}), "every"),
        "zamanlanmış görev 422")
    chk(rejected(req("/api/licenses", token, {"name": "SI Lisans", "match_pattern": "si", "seats": 1,
                                              "owner": "x"}), "owner")
        and not await c.fetchval("SELECT 1 FROM licenses WHERE name = 'SI Lisans'"), "lisans: 422, yazılmadı")
    chk(rejected(req("/api/tickets", token, {"subject": "SI talep", "pc": PC}), "pc"), "panelden talep 422")
    chk(rejected(req("/api/admin/users", token, {"username": "si-yeni", "password": "Si-Parola-12345",
                                                 "role": "viewer", "permissions": "[]", "email": "x"}), "email")
        and not await c.fetchval("SELECT 1 FROM users WHERE username = 'si-yeni'"), "kullanıcı: 422, yazılmadı")
    chk(rejected(req("/api/agent_policies", token, {"fair_use_text": "", "dns_categories": [], "auto_quarantine": False,
                                                    "quarantine_threshold": 3, "dns_domain": {}}), "dns_domain"),
        "politika 422 (dns_domains yanlış yazılınca liste silinmez)")
    chk(rejected(req("/api/admin/login", None, {"username": "si-super", "password": "x", "remember": True}),
                 "remember"), "giriş 422")
    # Dosya aktarımı, sınav modu, güç komutu ve mesaj, eş önbelleği, kimlik sağlayıcıları
    tasks_before = await c.fetchval("SELECT count(*) FROM tasks WHERE target_pc = $1", PC)
    chk(rejected(req("/api/files/pull", token, {"pc": PC, "path": "C:\\a.txt", "max_size": 10, "reason": "SI gerekçe",
                                                "overwrite": True}), "overwrite"), "dosya alma 422")
    chk(rejected(req("/api/labs/%s/exam" % LAB, token, {"duration_minutes": 30, "reason": "SI sınav", "kiosk": True}),
                 "kiosk")
        and not await c.fetchval("SELECT 1 FROM exam_sessions WHERE lab_name = $1 AND ended_at IS NULL", LAB),
        "sınav modu: 422, sınav başlamadı")
    chk(rejected(req("/api/devices/power", token, {"targets": [PC], "op": "lock", "force": True}), "force")
        and rejected(req("/api/devices/message", token,
                         {"targets": [PC], "title": "SI", "text": "SI mesaj", "color": "red"}), "color")
        and await c.fetchval("SELECT count(*) FROM tasks WHERE target_pc = $1", PC) == tasks_before,
        "güç komutu ve mesaj: 422, görev açılmadı")
    peer_before = await c.fetchval("SELECT value FROM global_settings WHERE key = 'update_peer_cache'")
    chk(rejected(req("/api/system/update-peer-cache", token, {"enabled": False, "lab": LAB}), "lab")
        and await c.fetchval("SELECT value FROM global_settings WHERE key = 'update_peer_cache'") == peer_before,
        "eş önbelleği ayarı: 422, değişmedi")
    chk(rejected(req("/api/sso/settings/ldap", token, {"enabled": False, "host": "", "domain": "x"}, method="PUT"),
                 "domain")
        and rejected(req("/api/sso/settings/oidc", token, {"enabled": False, "tenant": "x"}, method="PUT"), "tenant")
        and not await c.fetchval("SELECT 1 FROM sso_providers"), "kimlik sağlayıcıları: 422, yazılmadı")


async def agent(c):
    print("== ajan gövdeleri: bugünkü alanlar ve yeni bir alan kabul edilir")
    s, b = req("/api/inventory/" + PC, None, INVENTORY, headers=AGENT)
    chk(s == 200 and await c.fetchval("SELECT cpu FROM hw_inventory WHERE pc_name = $1", PC) == "Intel i5-8500",
        "envanter ('dna' ve yeni alanla) kabul edildi ve yazıldı (%s %s)" % (s, b))
    s, b = req("/api/software/" + PC, None, SOFTWARE, headers=AGENT)
    chk(s == 200 and await c.fetchval("SELECT version FROM device_software WHERE pc_name = $1", PC) == "1.2",
        "yazılım listesi kabul edildi (%s %s)" % (s, b))
    s, b = req("/api/patches/" + PC, None, PATCHES, headers=AGENT)
    chk(s == 200 and await c.fetchval("SELECT pending_security FROM device_patch_status WHERE pc_name = $1", PC) == 1,
        "Windows Update durumu kabul edildi (%s %s)" % (s, b))
    s, b = req("/api/tickets/agent/" + PC, None, TICKET, headers=AGENT)
    chk(s == 200 and await c.fetchval("SELECT subject FROM tickets WHERE pc_name = $1", PC) == "SI yazıcı",
        "tepsiden talep kabul edildi (%s %s)" % (s, b))
    s, b = req("/api/logs/" + PC, None, LOG, headers=AGENT)
    chk(s == 200, "olay kaydı kabul edildi (%s %s)" % (s, b))
    s, b = req("/api/auth/login", None, AUTH, headers=AGENT)
    chk(s == 200 and await c.fetchval("SELECT logged_user FROM clients WHERE pc_name = $1", PC) == "si-ogrenci",
        "oturum açma olayı kabul edildi (%s %s)" % (s, b))
    s, b = req("/api/policy_alert", None, ALERT, headers=AGENT)
    chk(s == 200, "DNS uyarısı kabul edildi (%s %s)" % (s, b))
    s, b = req("/api/auth/logout", None, dict(AUTH, message="çıkış"), headers=AGENT)
    chk(s == 200, "oturum kapama olayı kabul edildi (%s %s)" % (s, b))


if __name__ == "__main__":
    started = time.time()
    asyncio.run(main())
    print("süre: %.1f sn" % (time.time() - started))
