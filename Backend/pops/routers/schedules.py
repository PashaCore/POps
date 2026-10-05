"""Zamanlanmış görevler: bir komutu belirli bir anda ya da her gün / seçili günlerde hedef cihazlara
kuyruklar. Çalıştırma pops.scheduler'dadır; görevler normal kuyruktan (SYSTEM 'execute') geçer, bu
yüzden yetki görev kuyruğuyla aynıdır (admin) ve oluşturma/silme hash-zincirli denetime yazılır."""

import datetime
import json
import re

from fastapi import APIRouter, Depends, HTTPException

from pops.audit import add_audit_log
from pops import modules
from pops.db import execute_query
from pops.models import ScheduledTaskInput, ScheduleToggleInput
from pops.scheduler import _now, compute_next_run, enqueue
from pops.security import require_admin
from pops.taskqueue import process_queue

router = APIRouter()

_TIME_RE = re.compile(r"^([01][0-9]|2[0-3]):[0-5][0-9]$")


def _row_out(r: dict) -> dict:
    out = dict(r)
    for k in ("run_at", "next_run", "last_run", "created_at"):
        if out.get(k):
            out[k] = out[k].astimezone().isoformat()
    out["targets"] = json.loads(out.get("targets") or "[]")
    out["weekdays"] = [int(d) for d in (out.get("weekdays") or "").split(",") if d.strip().isdigit()]
    return out


def _validate(data: ScheduledTaskInput):
    name = (data.name or "").strip()
    command = (data.command or "").strip()
    if not name or len(name) > 100:
        raise HTTPException(status_code=400, detail="Ad 1-100 karakter olmalı.")
    if not command or len(command) > 4000:
        raise HTTPException(status_code=400, detail="Komut 1-4000 karakter olmalı.")
    if data.target_mode not in ("ALL", "LAB", "PC"):
        raise HTTPException(status_code=400, detail="Geçersiz hedef türü.")
    targets = [t.strip() for t in data.targets if t and t.strip()]
    if data.target_mode != "ALL" and not targets:
        raise HTTPException(status_code=400, detail="En az bir hedef seçin.")
    run_at = None
    time_of_day = None
    weekdays = ""
    if data.schedule_type == "once":
        try:
            run_at = datetime.datetime.fromisoformat((data.run_at or "").strip())
        except ValueError:
            raise HTTPException(status_code=400, detail="Tarih/saat geçersiz.")
        if run_at.tzinfo is None:
            run_at = run_at.astimezone()  # sunucunun saat dilimi
        if run_at <= _now():
            raise HTTPException(status_code=400, detail="Tek seferlik görevin zamanı gelecekte olmalı.")
    elif data.schedule_type in ("daily", "weekly"):
        time_of_day = (data.time_of_day or "").strip()
        if not _TIME_RE.match(time_of_day):
            raise HTTPException(status_code=400, detail="Saat SS:DD biçiminde olmalı.")
        if data.schedule_type == "weekly":
            days = sorted({d for d in data.weekdays if 1 <= d <= 7})
            if not days:
                raise HTTPException(status_code=400, detail="En az bir gün seçin.")
            weekdays = ",".join(str(d) for d in days)
    else:
        raise HTTPException(status_code=400, detail="Geçersiz zamanlama türü.")
    return name, command, targets, run_at, time_of_day, weekdays


@router.get("/api/scheduled_tasks", dependencies=[modules.require("schedules")])
async def list_scheduled_tasks(auth: dict = Depends(require_admin)):
    rows = await execute_query(
        "SELECT * FROM scheduled_tasks ORDER BY enabled DESC, next_run NULLS LAST, id", fetch=True
    )
    return {"items": [_row_out(r) for r in (rows or [])], "server_time": _now().isoformat()}


@router.post("/api/scheduled_tasks", dependencies=[modules.require("schedules")])
async def create_scheduled_task(data: ScheduledTaskInput, auth: dict = Depends(require_admin)):
    name, command, targets, run_at, time_of_day, weekdays = _validate(data)
    next_run = compute_next_run(data.schedule_type, run_at, time_of_day, weekdays, _now()) if data.enabled else None
    rows = await execute_query(
        "INSERT INTO scheduled_tasks (name, command, target_mode, targets, schedule_type, run_at, time_of_day, "
        "weekdays, enabled, next_run, created_by) VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11) RETURNING *",
        (
            name,
            command,
            data.target_mode,
            json.dumps(targets),
            data.schedule_type,
            run_at,
            time_of_day,
            weekdays or None,
            data.enabled,
            next_run,
            auth.get("sub"),
        ),
        fetch=True,
    )
    row = rows[0]
    await add_audit_log(
        "*",
        "schedule_create",
        "Zamanlanmış görev oluşturuldu: %s" % name,
        {
            "id": row["id"],
            "by": auth.get("sub"),
            "command": command[:200],
            "target_mode": data.target_mode,
            "targets": targets,
            "schedule": data.schedule_type,
            "time_of_day": time_of_day,
            "weekdays": weekdays,
            "run_at": run_at.isoformat() if run_at else None,
        },
    )
    return _row_out(row)


@router.post("/api/scheduled_tasks/{task_id}/toggle", dependencies=[modules.require("schedules")], deprecated=True)
async def toggle_scheduled_task(task_id: int, data: ScheduleToggleInput, auth: dict = Depends(require_admin)):
    rows = await execute_query("SELECT * FROM scheduled_tasks WHERE id = $1", (task_id,), fetch=True)
    if not rows:
        raise HTTPException(status_code=404, detail="Görev bulunamadı.")
    r = rows[0]
    next_run = (
        compute_next_run(r["schedule_type"], r["run_at"], r["time_of_day"], r["weekdays"], _now())
        if data.enabled
        else None
    )
    if data.enabled and next_run is None:
        raise HTTPException(status_code=400, detail="Tek seferlik görevin zamanı geçmiş; yeni bir görev oluşturun.")
    await execute_query(
        "UPDATE scheduled_tasks SET enabled = $1, next_run = $2 WHERE id = $3", (data.enabled, next_run, task_id)
    )
    await add_audit_log(
        "*",
        "schedule_toggle",
        "Zamanlanmış görev %s: %s" % ("açıldı" if data.enabled else "durduruldu", r["name"]),
        {"id": task_id, "by": auth.get("sub"), "enabled": data.enabled},
    )
    return {"ok": True, "enabled": data.enabled, "next_run": next_run.isoformat() if next_run else None}


@router.post("/api/scheduled_tasks/{task_id}/run", dependencies=[modules.require("schedules")])
async def run_scheduled_task_now(task_id: int, auth: dict = Depends(require_admin)):
    rows = await execute_query("SELECT * FROM scheduled_tasks WHERE id = $1", (task_id,), fetch=True)
    if not rows:
        raise HTTPException(status_code=404, detail="Görev bulunamadı.")
    r = dict(rows[0])
    r["created_by"] = auth.get("sub")  # elle tetikleyen kişi görevin sahibi olarak kaydedilir
    n = await enqueue(r, "elle çalıştırılan zamanlanmış")
    await add_audit_log(
        "*",
        "schedule_run_now",
        "Zamanlanmış görev elle çalıştırıldı: %s" % r["name"],
        {"id": task_id, "by": auth.get("sub"), "queued": n},
    )
    await process_queue()
    return {"ok": True, "queued": n}


@router.delete("/api/scheduled_tasks/{task_id}")
async def delete_scheduled_task(task_id: int, auth: dict = Depends(require_admin)):
    rows = await execute_query("DELETE FROM scheduled_tasks WHERE id = $1 RETURNING name", (task_id,), fetch=True)
    if not rows:
        raise HTTPException(status_code=404, detail="Görev bulunamadı.")
    await add_audit_log(
        "*",
        "schedule_delete",
        "Zamanlanmış görev silindi: %s" % rows[0]["name"],
        {"id": task_id, "by": auth.get("sub")},
    )
    return {"ok": True}
