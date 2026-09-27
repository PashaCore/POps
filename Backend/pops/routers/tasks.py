"""Görev kuyruğu, orkestrasyon, paket deposu ve depolama uçları."""

import datetime
import os
import shutil

from fastapi import APIRouter, Depends, File, HTTPException, Request, UploadFile
from werkzeug.utils import secure_filename

from pops.config import LOG_TABLE, UPDATES_DIR, UPLOAD_DIR
from pops.db import execute_query
from pops.models import CreatePackageInput, DeletePackageInput, OrchestrationInput, SetLimitInput, TaskActionInput
from pops.security import require_admin, require_auth
from pops.audit import add_audit_log
from pops.taskqueue import process_queue

router = APIRouter()


@router.get("/api/tasks")
async def get_tasks(limit: int = 1000, auth: dict = Depends(require_auth)):
    rows = await execute_query("SELECT * FROM tasks ORDER BY id DESC LIMIT $1", (limit,), fetch=True)
    return rows if rows else []


@router.post("/api/flush_queue")
async def flush_queue(auth: dict = Depends(require_admin)):
    # F4(b): tüm görev geçmişini silmeden ÖNCE, kimin sildiğini + kaç kayıt olduğunu hash-zincirli loga yaz.
    cnt = await execute_query("SELECT COUNT(*) AS c FROM tasks", fetch=True)
    await add_audit_log(
        "*",
        "flush_queue",
        "Görev kuyruğu/geçmişi silindi: %s" % auth.get("sub"),
        {"admin": auth.get("sub"), "deleted": (cnt[0]["c"] if cnt else None)},
    )
    await execute_query("DELETE FROM tasks")
    return {"status": "success"}


@router.post("/api/tasks/action")
async def handle_task_action(data: TaskActionInput, auth: dict = Depends(require_admin)):
    action = data.action.upper()
    mode = data.target_mode.upper()
    tid = data.target_id
    new_status = {"CANCEL": "Cancelled", "RETRY": "Pending", "PAUSE": "Paused", "RESUME": "Pending"}.get(action)
    status_condition = "1=1" if action == "RETRY" else "status IN ('Pending', 'Running', 'Paused')"

    if mode == "TASK":
        await execute_query(
            f"UPDATE tasks SET status = $1 WHERE id = $2 AND {status_condition}", (new_status, int(tid))
        )
    elif mode == "LAB":
        await execute_query(
            f"UPDATE tasks SET status = $1 WHERE target_lab = $2 AND {status_condition}", (new_status, tid)
        )
    elif mode == "PC":
        await execute_query(
            f"UPDATE tasks SET status = $1 WHERE target_pc = $2 AND {status_condition}", (new_status, tid)
        )
    elif mode == "ALL":
        await execute_query(f"UPDATE tasks SET status = $1 WHERE {status_condition}", (new_status,))
    if action in ["RESUME", "RETRY"]:
        await process_queue()
    return {"status": "success"}


@router.post("/api/set_concurrent_limit")
async def set_concurrent_limit(data: SetLimitInput, auth: dict = Depends(require_admin)):
    await execute_query(
        "INSERT INTO global_settings (key, value) "
        "VALUES ('concurrent_limit', $1) ON CONFLICT (key) DO UPDATE "
        "SET value=EXCLUDED.value",
        (str(data.limit),),
    )
    await process_queue()
    return {"status": "success"}


@router.get("/api/get_concurrent_limit")
async def get_concurrent_limit(auth: dict = Depends(require_auth)):
    row = await execute_query("SELECT value FROM global_settings WHERE key = 'concurrent_limit'", fetch=True)
    return {"limit": int(row[0]["value"]) if row else 5}


@router.post("/api/upload")
async def upload_file(request: Request, file: UploadFile = File(...), auth: dict = Depends(require_admin)):
    # Dosya adını temizle ("../", mutlak yol, ayraç vb. atılır)
    filename = secure_filename(file.filename or "")
    if not filename:
        raise HTTPException(status_code=400, detail="Geçersiz dosya adı")
    # Son yol mutlaka UPLOAD_DIR'in doğrudan içinde olmalı (path traversal / symlink engeli)
    file_path = os.path.realpath(os.path.join(UPLOAD_DIR, filename))
    if os.path.dirname(file_path) != UPLOAD_DIR:
        raise HTTPException(status_code=400, detail="Geçersiz dosya yolu")
    with open(file_path, "wb") as buffer:
        shutil.copyfileobj(file.file, buffer)
    return {"status": "success", "filename": filename, "url": f"{request.base_url}download/{filename}"}


@router.post("/api/add_package")
async def add_package(data: CreatePackageInput, auth: dict = Depends(require_admin)):
    await execute_query(
        "INSERT INTO packages (id, name, type, meta, command, icon, color) "
        "VALUES ($1, $2, $3, $4, $5, $6, $7) ON CONFLICT (id) DO UPDATE "
        "SET name=EXCLUDED.name, type=EXCLUDED.type, meta=EXCLUDED.meta, command=EXCLUDED.command, "
        "icon=EXCLUDED.icon, color=EXCLUDED.color",
        (data.id, data.name, data.type, data.meta, data.command, data.icon, data.color),
    )
    return {"status": "success"}


@router.post("/api/delete_package")
async def delete_package(data: DeletePackageInput, auth: dict = Depends(require_admin)):
    await execute_query("DELETE FROM packages WHERE id = $1", (data.id,))
    return {"status": "success"}


@router.get("/api/packages")
async def get_packages(auth: dict = Depends(require_auth)):
    rows = await execute_query("SELECT * FROM packages", fetch=True)
    return rows if rows else []


def get_folder_size(folder):
    total = 0
    if os.path.exists(folder):
        for dirpath, _, filenames in os.walk(folder):
            for f in filenames:
                fp = os.path.join(dirpath, f)
                if not os.path.islink(fp):
                    total += os.path.getsize(fp)
    return total


@router.get("/api/storage")
async def api_storage(auth: dict = Depends(require_auth)):
    upload_size = get_folder_size(UPLOAD_DIR)
    updates_size = get_folder_size(UPDATES_DIR)
    used_bytes = upload_size + updates_size
    total_bytes = 20 * 1024 * 1024 * 1024  # 20 GB

    try:
        size_row = await execute_query(f"SELECT pg_total_relation_size('{LOG_TABLE}') as size", fetch=True)
        log_bytes = size_row[0]['size'] if size_row else 0

        trend_rows = await execute_query(
            f"""
            SELECT SUBSTRING(timestamp FROM 1 FOR 10) as day, COUNT(*) as c
            FROM {LOG_TABLE}
            WHERE timestamp >= to_char(current_date - interval '6 days', 'YYYY-MM-DD')
            GROUP BY SUBSTRING(timestamp FROM 1 FOR 10)
            ORDER BY day ASC
        """,
            fetch=True,
        )
        log_trend = [{"day": r['day'], "count": r['c']} for r in trend_rows] if trend_rows else []
    except Exception as e:
        print(f"Log stat error: {e}")
        log_bytes = 0
        log_trend = []

    return {
        "status": "success",
        "used_bytes": used_bytes,
        "total_bytes": total_bytes,
        "free_bytes": max(0, total_bytes - used_bytes),
        "log_bytes": log_bytes,
        "log_trend": log_trend,
    }


@router.post("/api/deploy_orchestration")
async def deploy_orchestration(data: OrchestrationInput, auth: dict = Depends(require_admin)):
    target_pcs = []
    if data.target_mode == 'ALL':
        res = await execute_query("SELECT pc_name, lab_name FROM clients", fetch=True)
        target_pcs = [{"pc": r["pc_name"], "lab": r["lab_name"]} for r in (res or [])]
    elif data.target_mode == 'LAB':
        for lab in data.targets:
            res = await execute_query("SELECT pc_name, lab_name FROM clients WHERE lab_name = $1", (lab,), fetch=True)
            target_pcs.extend([{"pc": r["pc_name"], "lab": r["lab_name"]} for r in (res or [])])
    else:
        for pc in data.targets:
            res = await execute_query("SELECT lab_name FROM clients WHERE pc_name = $1", (pc,), fetch=True)
            target_pcs.append({"pc": pc, "lab": res[0]["lab_name"] if res else "Bilinmeyen Lab"})

    now = datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S")
    creator = auth.get("sub")  # F4(a): görevi kuyruklayan admin kaydedilir
    for target in target_pcs:
        for task in data.taskSequence:
            await execute_query(
                "INSERT INTO tasks (target_pc, target_lab, script_path, status, created_at, created_by) "
                "VALUES ($1, $2, $3, 'Pending', $4, $5)",
                (target["pc"], target["lab"], task.command, now, creator),
            )
    await process_queue()
    return {"status": "success"}
