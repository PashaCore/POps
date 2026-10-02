"""Sunucu gerektirmeyen birim testleri (CI 'backend' job'ı).

Veritabanı ya da çalışan sunucu gerekmez: python Backend/tests/test_units.py
"""

import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(__file__), os.pardir))
# Router modülleri yapılandırmayı içe aktarır; veritabanına bağlanılmaz, değerler yalnızca doğrulamayı geçer
for _k in ("JWT_SECRET", "DB_USER", "DB_PASS", "DB_NAME"):
    os.environ.setdefault(_k, "unit-test")
import json  # noqa: E402
import logging  # noqa: E402

import datetime  # noqa: E402

from pops import agent_health, bypass, logs, update_notice  # noqa: E402
from pops.routers import activity  # noqa: E402

FAILS = []


def chk(cond, msg):
    print(("  OK   " if cond else "  FAIL ") + msg)
    if not cond:
        FAILS.append(msg)


def test_update_notice():
    print("== update_notice")
    # 27 Eylül tatbikatlarının gerçek sonucu: geri kurulum servisi bırakmadı, son çare kurtardı
    real = {
        "status": "rollback_failed", "from_version": "0.1.5-alpha", "to_version": "0.1.6-alpha",
        "rollback": "msi", "agent_state": "reinstalled", "msi_exit_code": 0, "running_version": "0.1.5-alpha",
    }
    ev, sev, title = update_notice.describe(real)
    chk(ev == "update_problem" and sev == "critical", "son çareyle kurtarılan geri dönüş hâlâ kritik")
    chk("son çare" in title and "0.1.5-alpha" in title, "başlık son çareyi ve çalışan sürümü söylüyor: %s" % title)
    chk("reinstalled" not in title, "başlıkta ajan terimi yok")
    chk(update_notice.is_critical(real), "denetim kaydı kritik")

    ev, sev, title = update_notice.describe(dict(real, agent_state="unmanaged", running_version=None))
    chk(sev == "critical" and "elle kurulum" in title, "yönetimsiz cihaz elle kurulum ister")

    ok = {"status": "rolled_back", "from_version": "0.1.7-alpha", "to_version": "0.1.8-alpha", "rollback": "msi",
          "agent_state": "running", "running_version": "0.1.7-alpha"}
    ev, sev, title = update_notice.describe(ok)
    chk(ev == "update_rolled_back" and sev == "high", "başarılı geri dönüş kritik değil")
    chk("0.1.7-alpha" in title and "onarım" not in title, "başlık çalışan sürümü söylüyor: %s" % title)
    chk(not update_notice.is_critical(ok), "başarılı geri dönüş kritik denetim kaydı üretmez")
    _, _, title = update_notice.describe(dict(ok, rollback="msi_repair"))
    chk("onarımla" in title, "onarımla geri dönüş ayrı yazılıyor")

    chk(update_notice.describe({"status": "install_failed"})[0] == "update_not_started", "install_failed")
    chk(update_notice.describe({"status": "pending_reboot"})[1] == "medium", "pending_reboot orta")
    chk(update_notice.describe({"status": "rollback_pending_reboot", "to_version": "0.1.8"})[1] == "high",
        "rollback_pending_reboot artık bildiriliyor")
    chk(update_notice.describe({"status": "success", "to_version": "0.1.8", "running_version": "0.1.8"})
        == ("update_ok", "info", "Ajan güncellendi: 0.1.8"), "başarı")
    chk(update_notice.describe({"status": "something-new"}) is None, "bilinmeyen durum bildirim üretmez")


def test_log_format():
    print("== log biçimi")
    rec = logging.LogRecord("uvicorn.error", logging.INFO, __file__, 1, "connection open", None, None)
    rec.websocket = object()
    rec.pc_name = "HW-1"
    out = json.loads(logs.JsonFormatter().format(rec))
    chk(out["msg"] == "connection open" and out["level"] == "INFO", "temel alanlar")
    chk(out.get("pc_name") == "HW-1", "düz ek alan yazılır")
    chk("websocket" not in out, "nesne ek alanı yazılmaz")


def test_activity():
    print("== etkinlik geçmişi")
    dev = "HW-A"
    items = activity.build_items(
        dev,
        [{"start_time": "2026-09-27 19:22:38", "end_time": "2026-09-27 19:22:42", "admin_name": "Pasha",
          "reason": "sınav", "is_mandatory": False}],
        [{"action": "lockdown", "timestamp": "2026-09-27 20:08:48", "changes": '{"admin": "Pasha", "reason": "s"}'},
         {"action": "update_result", "timestamp": "2026-09-29 20:31:29",
          "changes": '{"status": "rolled_back", "to_version": "0.1.8-alpha", "running_version": "0.1.7-alpha"}'},
         {"action": "NEW_DEVICE", "timestamp": "2026-09-20 10:00:00", "changes": '{"bios_sn": "GIZLI-SERI"}'},
         {"action": "set_capabilities", "timestamp": "2026-09-21 10:00:00",
          "changes": '{"vision_enabled": false, "by": "Pasha"}'}],
        [{"action": "install_updates", "timestamp": "2026-09-27 18:00:05",
          "changes": '{"by": "Pasha", "dispatched": ["HW-A"]}'},
         {"action": "install_updates", "timestamp": "2026-09-27 18:00:06",
          "changes": '{"by": "Pasha", "dispatched": ["HW-AB"]}'}],
        [{"created_at": "2026-09-26 19:44:40", "created_by": "Pasha", "status": "Completed"}],
    )
    chk([i["at"] for i in items] == sorted([i["at"] for i in items], reverse=True), "en yeni başta")
    chk(len(items) == 7, "başka cihaza gönderilen işlem dahil değil (%d kayıt)" % len(items))
    chk(items[0]["title"] == "0.1.8-alpha güncellemesi geri alındı, 0.1.7-alpha çalışıyor", "güncelleme başlığı")
    chk("GIZLI-SERI" not in json.dumps(items, ensure_ascii=False), "donanım seri numarası sızmıyor")
    kinds = {i["kind"] for i in items}
    chk(kinds == {"remote_session", "quarantine", "update", "enroll", "capability", "windows_update", "command"},
        "tüm türler: %s" % sorted(kinds))
    cap = [i for i in items if i["kind"] == "capability"][0]
    chk(cap["title"] == "Uzaktan izleme kapatıldı" and cap["actor"] == "Pasha", "yetenek değişikliği ve yapan")
    cmd = [i for i in items if i["kind"] == "command"][0]
    chk(cmd["detail"] == "Durum: Completed" and cmd["actor"] == "Pasha", "komut içeriği yok, yapan var")


def test_bypass():
    print("== bypass")
    # Ajanla ortak vektör (POps.Tests OfflineBypassTests): anahtar 32 x 0x01, HW-TEST, 2026-10-01
    import base64
    key = base64.urlsafe_b64encode(bytes([1] * 32)).rstrip(b"=").decode()
    chk(bypass.device_code(key, "HW-TEST", datetime.date(2026, 10, 1)) == "BA258E", "cihaz kodu test vektörü")
    chk(bypass.fingerprint(key) == "72cd6e8422c407fb", "parmak izi test vektörü")
    # Günün sonraki kodları (n=1..9): "HW-TEST|2026-10-01|n"; ajan testleriyle ortak
    chk(bypass.device_code(key, "HW-TEST", datetime.date(2026, 10, 1), 1) == "D31B3C", "günün 2. kodu (n=1)")
    chk(bypass.device_code(key, "HW-TEST", datetime.date(2026, 10, 1), 9) == "ACA17F", "günün 10. kodu (n=9)")
    fresh = bypass.new_key()
    chk(len(fresh) == 43 and "=" not in fresh and len(base64.urlsafe_b64decode(fresh + "=")) == 32,
        "yeni anahtar 32 bayt base64url (dolgusuz)")
    chk(bypass.supports_device_key("0.1.12-alpha") and bypass.supports_device_key("v0.2.0"), "0.1.12+ destekler")
    chk(not bypass.supports_device_key("0.1.11-alpha") and not bypass.supports_device_key("test")
        and not bypass.supports_device_key(None), "eski/bilinmeyen sürüme gönderilmez")


def test_hardening():
    print("== 0.1.13 sağlamlaştırma")
    import asyncio
    from pops import agent_auth, agent_version, dna
    from pops.routers import agents as agents_router
    # Donanım kimliği: okunamayan/üretici varsayılanı değerler puan kazandırmaz (F04)
    blank = {"uuid": "NULL", "bios_sn": "Default string", "disk_sn": "", "mac": "00:00:00:00:00:00", "ram_sn": "NULL"}
    rec = {"dna_uuid": "NULL", "dna_bios": "Default string", "dna_disk": "", "dna_mac": "00:00:00:00:00:00",
           "dna_ram": "NULL"}
    chk(dna.calculate_dna_score(blank, rec, {}, {}) == (0, 0), "boş/varsayılan donanım değerleri kanıt sayılmaz")
    real = {"uuid": "U-1", "bios_sn": "B-1", "disk_sn": "D-1", "mac": "AA:BB:CC:DD:EE:01", "ram_sn": "R-1"}
    same = {"dna_uuid": "u-1", "dna_bios": "B-1", "dna_disk": "D-1", "dna_mac": "aa:bb:cc:dd:ee:01", "dna_ram": "R-1"}
    chk(dna.calculate_dna_score(real, same, {}, {}) == (11, 11), "aynı donanım tam puan (büyük/küçük harf duyarsız)")
    # Yeniden başlatma komutu tanıma (görev akıbeti)
    chk(agents_router._is_reboot_command("shutdown /r /t 15") and agents_router._is_reboot_command("Restart-Computer"),
        "yeniden başlatma komutu tanınır")
    chk(not agents_router._is_reboot_command("shutdown /s") and not agents_router._is_reboot_command("echo /r"),
        "kapatma ya da ilgisiz komut yeniden başlatma sayılmaz")
    chk(agent_version.at_least("0.1.13-alpha", (0, 1, 13)) and not agent_version.at_least("0.1.12", (0, 1, 13))
        and agent_version.parse("x" * 10000) is None, "sürüm karşılaştırma (uzun girdide de)")
    # Kimlik zorlaması ayarı okunamazsa kapı kapalı (R-03)

    async def broken(*a, **k):
        raise RuntimeError("db down")
    original = agent_auth.execute_query
    agent_auth.execute_query = broken
    try:
        chk(asyncio.run(agent_auth.enforce_agent_auth_enabled()) is True, "ayar okunamazsa zorlama açık sayılır")
    finally:
        agent_auth.execute_query = original
    chk(agent_auth.hash_enroll_token("abc") == "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
        "kayıt jetonu SHA-256 ile saklanır")


def test_agent_health():
    print("== agent_health")
    chk(agent_health.clean(None) is None and agent_health.clean("x") is None, "blok yoksa NULL")
    out = json.loads(agent_health.clean({
        "started_at": 1700000000, "last_policy_sync": -5, "last_inventory_upload": True, "tray_connected": "evet",
        "vision_channel": "connected", "loop_errors_1h": 2, "last_error": "ğ" * 300, "extra": 1,
        "screen_locked": True, "network_isolated": False, "isolation_error": "x" * 300}))
    chk(out["screen_locked"] is True and out["network_isolated"] is False and len(out["isolation_error"]) == 200,
        "karantina kilit/yalıtım durumu ayrı saklanır")
    chk(out["started_at"] == 1700000000 and out["last_policy_sync"] is None and out["last_inventory_upload"] is None,
        "zaman alanları: negatif ve bool atıldı")
    chk(out["tray_connected"] is None and out["vision_channel"] == "connected", "tür denetimi")
    chk(len(out["last_error"]) == 200 and "extra" not in out, "metin kırpıldı, bilinmeyen alan yok")
    chk(agent_health.parse('{"a": 1}') == {"a": 1} and agent_health.parse("bozuk") is None, "okuma")


def main():
    test_update_notice()
    test_log_format()
    test_activity()
    test_bypass()
    test_hardening()
    test_agent_health()
    if FAILS:
        print("BASARISIZ: %d kontrol" % len(FAILS))
        sys.exit(1)
    print("TUM BIRIM TESTLERI GECTI")


if __name__ == "__main__":
    main()
