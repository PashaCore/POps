"""Sunucu gerektirmeyen birim testleri (CI 'backend' job'ı).

Veritabanı ya da çalışan sunucu gerekmez: python Backend/tests/test_units.py
"""

import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(__file__), os.pardir))
from pops import update_notice  # noqa: E402

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


def main():
    test_update_notice()
    if FAILS:
        print("BASARISIZ: %d kontrol" % len(FAILS))
        sys.exit(1)
    print("TUM BIRIM TESTLERI GECTI")


if __name__ == "__main__":
    main()
