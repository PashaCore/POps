"""Görev kuyruğu: eşzamanlılık sınırına göre bekleyen görevleri çevrimiçi ajanlara dağıtır."""

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


async def process_queue():
    limit_row = await execute_query("SELECT value FROM global_settings WHERE key = 'concurrent_limit'", fetch=True)
    limit = int(limit_row[0]["value"]) if limit_row else 5
    running_row = await execute_query(
        "SELECT COUNT(DISTINCT target_pc) as c FROM tasks WHERE status = 'Running'", fetch=True
    )
    running_pcs_count = running_row[0]["c"] if running_row else 0
    available_slots = limit - running_pcs_count

    if available_slots > 0 or limit == 0:
        online_pcs = list(manager.active_agents.keys())
        if online_pcs:
            busy_rows = await execute_query("SELECT DISTINCT target_pc FROM tasks WHERE status = 'Running'", fetch=True)
            busy_pcs = [r["target_pc"] for r in (busy_rows or [])]
            idle_online_pcs = [pc for pc in online_pcs if pc not in busy_pcs]

            for pc in idle_online_pcs:
                if limit > 0 and available_slots <= 0:
                    break
                task_row = await execute_query(
                    "SELECT * FROM tasks WHERE status = 'Pending' AND target_pc = $1 ORDER BY id ASC LIMIT 1",
                    (pc,),
                    fetch=True,
                )
                if task_row:
                    task = task_row[0]
                    await execute_query("UPDATE tasks SET status = 'Running' WHERE id = $1", (task["id"],))
                    await manager.send_command(
                        {"action": "execute", "task_id": task["id"], "script_path": task["script_path"]}, pc
                    )
                    # F4(a): komutu KİMİN kuyrukladığını göster (eskiden 'System/Queue' idi, iz yoktu).
                    actor = task.get("created_by") or "System/Queue"
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
