"""Ajan güncelleme sonucunun (update_result) bildirim ve denetim karşılığı.

Saf fonksiyon: veritabanına ya da ağa dokunmaz, bu yüzden tek başına test edilir. Başlık, yöneticinin
ne olduğunu ajan terimlerini (agent_state, rollback) bilmeden anlayacağı biçimde yazılır.
"""

# Yalnızca gerçekten kötü durumlar kritik sayılır. "rolled_back" (kurtarıldı) ve "install_failed"
# (kurulum hiç başlamadı, makine değişmedi) kritik değildir.
CRITICAL_STATUSES = ("rollback_failed", "failed", "reverted_by_freeze", "error", "rejected")


def is_critical(payload):
    if str(payload.get("agent_state") or "") == "unmanaged":
        return True
    return str(payload.get("status") or "") in CRITICAL_STATUSES


def describe(payload):
    """(olay, önem, başlık) ya da bildirim gerekmiyorsa None."""
    status = str(payload.get("status") or "")
    agent_state = str(payload.get("agent_state") or "")
    rollback = str(payload.get("rollback") or "")
    from_version = payload.get("from_version") or "?"
    to_version = payload.get("to_version") or "?"
    running = payload.get("running_version") or ""

    if agent_state == "unmanaged":
        title = "Ajan çalışmıyor: %s güncellemesinden sonra elle kurulum gerekli" % to_version
        return ("update_problem", "critical", title)
    if status == "rollback_failed":
        if agent_state == "reinstalled":
            # Updater geri dönüşte servisi bulamadı, son çare kurulumla ajanı geri getirdi: cihaz yönetiliyor
            # ama geri dönüş mekanizması bu cihazda çalışmadı.
            return (
                "update_problem",
                "critical",
                "Geri dönüş çalışmadı; son çare kurulum ajanı %s sürümünde geri getirdi" % (running or from_version),
            )
        return ("update_problem", "critical", "%s güncellemesi ve geri dönüş başarısız" % to_version)
    if status == "rejected":
        # Ajan paketi uygulamadı (imza, sürüm, indirme ya da özet tutmadı); sebep bildirimin ayrıntısında
        return ("update_problem", "critical", "Ajan %s güncellemesini reddetti" % to_version)
    if status in CRITICAL_STATUSES:
        return ("update_problem", "critical", "Ajan güncellemesi başarısız (%s): %s" % (status, to_version))
    if status == "rolled_back":
        how = " (onarımla)" if rollback == "msi_repair" else ""
        return (
            "update_rolled_back",
            "high",
            "%s sağlıklı açılmadı, geri alındı%s; çalışan sürüm %s" % (to_version, how, running or from_version),
        )
    if status == "install_failed":
        return ("update_not_started", "medium", "Güncelleme başlatılamadı, makine değişmedi")
    if status == "pending_reboot":
        return ("update_reboot", "medium", "Güncelleme yeniden başlatma bekliyor")
    if status == "rollback_pending_reboot":
        title = "%s sağlıklı açılmadı; geri dönüş yeniden başlatmada tamamlanacak" % to_version
        return ("update_reboot", "high", title)
    if status in ("ok", "success", "updated"):
        return ("update_ok", "info", "Ajan güncellendi: %s" % (running or to_version))
    return None
