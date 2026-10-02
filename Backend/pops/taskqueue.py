"""Görev kuyruğu: eşzamanlılık sınırına göre bekleyen görevleri çevrimiçi ajanlara dağıtır."""

import asyncio

from pops.db import execute_query
from pops.audit import add_audit_log, log_audit_event
from pops.manager import manager


async def resolve_targets(target_mode: str, targets) -> list:
    """Görev hedefleri: ALL (tüm cihazlar), LAB (lab adları), PC (HW- kimlikleri) -> [{"pc", "lab"}]."""
    out = []
    if target_mode == 'ALL':
        res = await execute_query("SELECT pc_name, lab_name FROM clients", fetch=True)
        out = [{"pc": r["pc_name"], "lab": r["lab_name"]} for r in (res or [])]
    elif target_mode == 'LAB':
        for lab in targets:
            res = await execute_query("SELECT pc_name, lab_name FROM clients WHERE lab_name = $1", (lab,), fetch=True)
            out.extend([{"pc": r["pc_name"], "lab": r["lab_name"]} for r in (res or [])])
    else:
        for pc in targets:
            res = await execute_query("SELECT lab_name FROM clients WHERE pc_name = $1", (pc,), fetch=True)
            out.append({"pc": pc, "lab": res[0]["lab_name"] if res else "Bilinmeyen Lab"})
    return out


# Eşzamanlı çağrılar birleştirilir: bir tur sürerken gelen çağrılar üst üste yığılmaz, tur bitince bir kez
# daha dönülür. Eskiden her ajan bağlantısı ayrı bir tur başlatıyor ve her tur çevrimiçi HER cihaz için ayrı sorgu
# atıyordu: 2000 ajan aynı anda bağlanınca ~2 milyon sorgu, sunucu dakikalarca meşgul (bkz. BENCHMARKS.md).
_queue_lock = asyncio.Lock()
_queue_again = False


async def process_queue():
    global _queue_again
    if _queue_lock.locked():
        _queue_again = True
        return
    async with _queue_lock:
        while True:
            _queue_again = False
            await _process_queue_once()
            if not _queue_again:
                break


async def _process_queue_once():
    # En sık durum: bekleyen görev yok. Tek ucuz sorguyla çıkılır.
    if not await execute_query("SELECT 1 FROM tasks WHERE status = 'Pending' LIMIT 1", fetch=True):
        return
    limit_row = await execute_query("SELECT value FROM global_settings WHERE key = 'concurrent_limit'", fetch=True)
    limit = int(limit_row[0]["value"]) if limit_row else 5
    running_row = await execute_query(
        "SELECT COUNT(DISTINCT target_pc) as c FROM tasks WHERE status = 'Running'", fetch=True
    )
    running_pcs_count = running_row[0]["c"] if running_row else 0
    available_slots = limit - running_pcs_count
    if not (available_slots > 0 or limit == 0):
        return
    online_pcs = list(manager.active_agents.keys())
    if not online_pcs:
        return
    # Çevrimiçi ve şu an görev çalıştırmayan her cihazın en eski bekleyen görevi, tek sorguda; en eski görev önce
    tasks = await execute_query(
        """
        SELECT * FROM (
            SELECT DISTINCT ON (t.target_pc) t.* FROM tasks t
            WHERE t.status = 'Pending' AND t.target_pc = ANY($1::text[])
              AND NOT EXISTS (SELECT 1 FROM tasks r WHERE r.status = 'Running' AND r.target_pc = t.target_pc)
            ORDER BY t.target_pc, t.id ASC
        ) oldest ORDER BY id ASC
        """,
        (online_pcs,),
        fetch=True,
    )
    for task in tasks or []:
        if limit > 0 and available_slots <= 0:
            break
        pc = task["target_pc"]
        await execute_query("UPDATE tasks SET status = 'Running', dispatched_at = NOW() WHERE id = $1", (task["id"],))
        # F4(a): komutu KİMİN kuyrukladığını göster (eskiden 'System/Queue' idi, iz yoktu).
        actor = task.get("created_by") or "System/Queue"
        # requested_by: ajan komutu kimin istediğini yerel denetim izine (Windows Olay Günlüğü) yazar (0.1.12+)
        sent = await manager.send_command(
            {"action": "execute", "task_id": task["id"], "script_path": task["script_path"], "requested_by": actor}, pc
        )
        if not sent:
            # Bağlantı bu arada koptu: görev ajana ulaşmadı, sıraya geri döner (yeniden bağlanınca gönderilir)
            await execute_query(
                "UPDATE tasks SET status = 'Pending', dispatched_at = NULL WHERE id = $1 AND status = 'Running'",
                (task["id"],),
            )
            continue
        await log_audit_event(
            pc,
            "Deploy",
            f"Görev: {task['script_path'][:50]}",
            actor_id=actor,
            event_type="deploy.execution",
            category="system_maintenance",
            action="execute_queue",
            risk_level="info",
            meta_data={"raw_command": task["script_path"], "created_by": task.get("created_by")},
        )
        # SYSTEM olarak komut çalıştırma yüksek-değerli olay → hash-zincirli,
        # ajanların yazamadığı loga da düş.
        await add_audit_log(
            pc,
            "execute",
            "SYSTEM komutu çalıştırıldı (kuyruk: %s)" % actor,
            {
                "task_id": task["id"],
                "created_by": task.get("created_by"),
                "command": (task["script_path"] or "")[:200],
            },
        )
        available_slots -= 1
