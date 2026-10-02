"""Kayıt saklama süreleri (KVKK: kişisel veri gerektiğinden uzun tutulmaz). Zamanlayıcı günde bir kez çalıştırır.

Silinenler (süre global_settings'te, gün; 0 = süresiz sakla):
  * retention_days_logs:          ajan olay kayıtları (agent_logs_v2 ve eski agent_logs)
  * retention_days_tasks:         sonuçlanmış görevler ve çıktıları (bekleyen/çalışan görevlere dokunulmaz)
  * retention_days_notifications: okunmuş bildirimler

Silinmeyenler: device_audit_logs (hash zincirli denetim kaydı; satır silmek zinciri kırar, arşivleme planı için bkz.
docs/decisions.md D-18) ve Vision oturum kayıtları (enterprise_audit_logs; hesap verebilirlik kaydı).

Silme 5000'erlik parçalarla yapılır: büyük bir tabloda tek DELETE uzun süre kilit tutup komut süre sınırına
takılmasın.
"""

import datetime
import logging
import time

from pops.db import execute_query

log = logging.getLogger("pops.retention")

DEFAULTS = {"retention_days_logs": 365, "retention_days_tasks": 365, "retention_days_notifications": 90}
MAX_DAYS = 3650
BATCH = 5000

# Sonuçlanmış görev durumları (bekleyen, duraklatılmış, çalışan ve sonucu belirsiz olanlar silinmez)
_FINISHED_TASKS = [
    "Completed", "Completed (Rebooted)", "Failed", "Error", "Cancelled", "Interrupted", "Timed Out", "Denied",
    "Expired",
]


async def settings() -> dict:
    rows = await execute_query(
        "SELECT key, value FROM global_settings WHERE key = ANY($1::text[])", (list(DEFAULTS),), fetch=True
    )
    out = dict(DEFAULTS)
    for r in rows or []:
        try:
            out[r["key"]] = max(0, min(MAX_DAYS, int(r["value"])))
        except (TypeError, ValueError):
            pass
    return out


async def save(values: dict) -> dict:
    for key, days in values.items():
        if key not in DEFAULTS:
            continue
        await execute_query(
            "INSERT INTO global_settings (key, value) VALUES ($1, $2) ON CONFLICT (key) DO UPDATE SET value = $2",
            (key, str(max(0, min(MAX_DAYS, int(days))))),
        )
    return await settings()


async def _delete_in_batches(table: str, where: str, params: tuple) -> int:
    total = 0
    while True:
        rows = await execute_query(
            f"DELETE FROM {table} WHERE id IN (SELECT id FROM {table} WHERE {where} LIMIT {BATCH}) RETURNING id",
            params,
            fetch=True,
        )
        n = len(rows or [])
        total += n
        if n < BATCH:
            return total


def _cutoff_text(days: int) -> str:
    # Zaman damgaları yerel saatte 'YYYY-MM-DD HH:MM:SS' metni; metin karşılaştırması tarih sırasıyla aynıdır
    return (datetime.datetime.now() - datetime.timedelta(days=days)).strftime("%Y-%m-%d %H:%M:%S")


async def apply() -> dict:
    cfg = await settings()
    removed = {}
    if cfg["retention_days_logs"]:
        cutoff = _cutoff_text(cfg["retention_days_logs"])
        removed["agent_logs"] = await _delete_in_batches("agent_logs_v2", '"timestamp" < $1', (cutoff,))
        removed["agent_logs"] += await _delete_in_batches("agent_logs", '"timestamp" < $1', (cutoff,))
    if cfg["retention_days_tasks"]:
        removed["tasks"] = await _delete_in_batches(
            "tasks",
            "created_at < $1 AND status = ANY($2::text[])",
            (_cutoff_text(cfg["retention_days_tasks"]), _FINISHED_TASKS),
        )
    if cfg["retention_days_notifications"]:
        removed["notifications"] = await _delete_in_batches(
            "notifications",
            "is_read AND created_at < NOW() - make_interval(days => $1)",
            (cfg["retention_days_notifications"],),
        )
    if any(removed.values()):
        log.info("saklama süresi dolan kayıtlar silindi", extra=removed)
    return removed


_last_attempt = [-3600.0]


async def apply_daily() -> None:
    """Günde bir kez (zamanlayıcı her turda çağırır; aynı gün ikinci kez çalışmaz)."""
    today = datetime.date.today().isoformat()
    rows = await execute_query("SELECT value FROM global_settings WHERE key = 'retention_run_date'", fetch=True)
    if rows and rows[0]["value"] == today:
        return
    # Hata veren tur her 30 sn'de bir değil, en fazla saatte bir yeniden denenir
    if time.monotonic() - _last_attempt[0] < 3600:
        return
    _last_attempt[0] = time.monotonic()
    await apply()
    # Tarih silme bittikten sonra yazılır: yarıda kalan (hata veren) tur aynı gün bir sonraki turda yeniden dener
    await execute_query(
        "INSERT INTO global_settings (key, value) VALUES ('retention_run_date', $1) "
        "ON CONFLICT (key) DO UPDATE SET value = $1",
        (today,),
    )
