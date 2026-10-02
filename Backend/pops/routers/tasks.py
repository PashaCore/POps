"""Görev kuyruğu, orkestrasyon, paket deposu ve depolama uçları."""

import asyncio
import base64
import datetime
import hashlib
import hmac
import json
import logging
import os
import secrets
import shutil
import time

from fastapi import APIRouter, Depends, File, HTTPException, Request, UploadFile
from fastapi.responses import FileResponse
from werkzeug.utils import secure_filename

from pops.config import LOG_TABLE, UPDATES_DIR, UPLOAD_DIR
from pops import db
from pops.db import execute_query
from pops.models import CreatePackageInput, DeletePackageInput, OrchestrationInput, SetLimitInput, TaskActionInput
from pops.security import require_admin, require_auth
from pops.audit import add_audit_log
from pops.manager import manager
from pops.taskqueue import process_queue, resolve_targets

log = logging.getLogger("pops.tasks")
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


# Hangi durumdaki görevlere hangi işlem uygulanır. Çalışan bir işlem duraklatılamaz (PAUSE yalnız sıradakiler);
# RETRY yalnız sonuçlanmış görevleri yeniden sıraya koyar: çalışan bir komut ikinci kez başlatılmaz.
_ACTION_STATUSES = {
    "CANCEL": ("Pending", "Running", "Paused", "Unknown"),
    "PAUSE": ("Pending",),
    "RESUME": ("Paused",),
    "RETRY": (
        "Completed", "Completed (Rebooted)", "Failed", "Error", "Cancelled", "Unknown", "Interrupted", "Timed Out",
        "Denied",
    ),
}
# Bir görevin yeniden denemesi sürüyorsa (bu durumlarda) ikinci kopya açılmaz
_ACTIVE_STATUSES = ["Pending", "Running", "Paused", "Unknown"]
_ACTION_TARGET = {"CANCEL": "Cancelled", "RETRY": "Pending", "PAUSE": "Paused", "RESUME": "Pending"}


@router.post("/api/tasks/action")
async def handle_task_action(data: TaskActionInput, auth: dict = Depends(require_admin)):
    action = data.action.upper()
    mode = data.target_mode.upper()
    tid = data.target_id
    new_status = _ACTION_TARGET.get(action)
    if new_status is None:
        raise HTTPException(status_code=400, detail="Geçersiz işlem")
    scope = {
        "TASK": ("t.id = {v}", lambda: int(tid)),
        "LAB": ("t.target_lab = {v}", lambda: tid),
        "PC": ("t.target_pc = {v}", lambda: tid),
        "ALL": ("{v}::text IS NULL", lambda: None),
    }.get(mode)
    if scope is None:
        raise HTTPException(status_code=400, detail="Geçersiz hedef")
    where, value = scope
    if action == "RETRY":
        return await _retry(where, value(), auth.get("sub"))
    # Önceki durum da döner: çalışmakta olan görev iptal edildiyse ajana da bildirilir
    changed = await execute_query(
        "WITH target AS (SELECT t.id, t.target_pc, t.status FROM tasks t "
        f"WHERE {where.format(v='$3')} AND t.status = ANY($2::text[]) FOR UPDATE) "
        "UPDATE tasks t SET status = $1, "
        "dispatched_at = CASE WHEN $1 = 'Pending' THEN NULL ELSE t.dispatched_at END, "
        "exit_code = CASE WHEN $1 = 'Pending' THEN NULL ELSE t.exit_code END "
        "FROM target WHERE t.id = target.id RETURNING target.id, target.target_pc, target.status AS old_status",
        (new_status, list(_ACTION_STATUSES[action]), value()),
        fetch=True,
    )
    if action == "CANCEL":
        for row in changed or []:
            if row["old_status"] in ("Running", "Unknown"):
                # 0.1.13+ ajan komutun işlemini (alt süreçleriyle) sonlandırır; eskiler mesajı yok sayar
                await manager.send_command({"action": "cancel_task", "task_id": row["id"]}, row["target_pc"])
    if action == "RESUME":
        await process_queue()
    return {"status": "success", "changed": len(changed or [])}


async def _retry(where: str, value, creator: str) -> dict:
    """Yeniden deneme YENİ bir görev kaydı açar (retry_of = eski görev); eski kayıt sonucuyla kalır. Eskiden aynı
    görev kimliği yeniden "Pending" yapılıyordu: iptal edilmiş ama hâlâ süren eski çalıştırmanın geç gelen sonucu
    yeni çalıştırmayı tamamlanmış gösterebilirdi. Aynı görevin süren bir yeniden denemesi varsa ikincisi açılmaz."""
    now = datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S")
    created = await execute_query(
        "INSERT INTO tasks (target_pc, target_lab, script_path, status, created_at, created_by, retry_of) "
        "SELECT t.target_pc, t.target_lab, t.script_path, 'Pending', $2, $3, t.id FROM tasks t "
        f"WHERE {where.format(v='$1')} AND t.status = ANY($4::text[]) "
        "AND NOT EXISTS (SELECT 1 FROM tasks n WHERE n.retry_of = t.id AND n.status = ANY($5::text[])) "
        "RETURNING id",
        (value, now, creator, list(_ACTION_STATUSES["RETRY"]), _ACTIVE_STATUSES),
        fetch=True,
    )
    await process_queue()
    return {"status": "success", "changed": len(created or [])}


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


# Dağıtım paketlerinin indirme adresi imzalıdır: dosya adını tahmin eden biri paketi indiremez. İmza anahtarı
# sunucuda üretilir (global_settings.download_link_key); paket komutları haftalar sonra da kuyruğa girebildiği için
# adresin süresi yoktur. Betik indirdiği dosyanın SHA-256 özetini de doğrular (deploy.php).
_download_key_cache = None


async def _download_key() -> bytes:
    global _download_key_cache
    if _download_key_cache is None:
        await execute_query(
            "INSERT INTO global_settings (key, value) VALUES ('download_link_key', $1) ON CONFLICT (key) DO NOTHING",
            (secrets.token_hex(32),),
        )
        rows = await execute_query("SELECT value FROM global_settings WHERE key = 'download_link_key'", fetch=True)
        _download_key_cache = bytes.fromhex(rows[0]["value"])
    return _download_key_cache


def _download_sig(key: bytes, filename: str) -> str:
    digest = hmac.new(key, ("download|" + filename).encode("utf-8"), hashlib.sha256).digest()
    return base64.urlsafe_b64encode(digest[:18]).decode("ascii")


def _file_sha256(path: str) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()


@router.post("/api/upload")
async def upload_file(request: Request, file: UploadFile = File(...), auth: dict = Depends(require_admin)):
    # Dosya adını temizle ("../", mutlak yol, ayraç vb. atılır)
    filename = secure_filename(file.filename or "")
    if not filename:
        raise HTTPException(status_code=400, detail="Geçersiz dosya adı")
    # Son yol mutlaka UPLOAD_DIR'in doğrudan içinde olmalı (path traversal / symlink engeli)
    file_path = os.path.realpath(os.path.join(UPLOAD_DIR, filename))
    if not file_path.startswith(UPLOAD_DIR + os.sep) or os.path.dirname(file_path) != UPLOAD_DIR:
        raise HTTPException(status_code=400, detail="Geçersiz dosya yolu")
    with open(file_path, "wb") as buffer:
        shutil.copyfileobj(file.file, buffer)
    sig = _download_sig(await _download_key(), filename)
    return {
        "status": "success",
        "filename": filename,
        "url": f"{request.base_url}download/{filename}?sig={sig}",
        "sig": sig,
        "sha256": _file_sha256(file_path),
    }


@router.get("/download/{filename}")
async def download_file(filename: str, sig: str = ""):
    """Dağıtım paketi: yalnızca yükleme sırasında verilen imzalı adresle. Yanlış imza, olmayan dosya ve geçersiz
    ad aynı 404'ü alır (hangi dosyaların var olduğu anlaşılmasın)."""
    not_found = HTTPException(status_code=404, detail="Bulunamadı")
    if not filename or secure_filename(filename) != filename:
        raise not_found
    if not hmac.compare_digest(sig.encode("ascii", "ignore"), _download_sig(await _download_key(), filename).encode()):
        raise not_found
    file_path = os.path.realpath(os.path.join(UPLOAD_DIR, filename))
    if not file_path.startswith(UPLOAD_DIR + os.sep):
        raise not_found
    if os.path.dirname(file_path) != UPLOAD_DIR or not os.path.isfile(file_path):
        raise not_found
    return FileResponse(file_path, filename=filename)


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
    except Exception:
        log.exception("log istatistiği okunamadı")
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


# Çift tıklama / yeniden denenen istek aynı görevi iki kez oluşturmasın: aynı kullanıcının aynı hedef ve komut
# dizisiyle birkaç saniye içindeki ikinci isteği yeni görev açmaz. Tek backend süreci olduğundan bellek yeterli.
DUPLICATE_WINDOW_SECONDS = 5.0
_recent_orchestrations = {}


def _orchestration_key(creator: str, data: OrchestrationInput) -> str:
    now = time.monotonic()
    for key, (at, fut) in list(_recent_orchestrations.items()):
        if now - at > DUPLICATE_WINDOW_SECONDS and fut.done():
            _recent_orchestrations.pop(key, None)
    return hashlib.sha256(
        json.dumps(
            [creator, data.target_mode, data.targets, [(t.type, t.command) for t in data.taskSequence]],
            ensure_ascii=False,
        ).encode("utf-8")
    ).hexdigest()


@router.post("/api/deploy_orchestration")
async def deploy_orchestration(data: OrchestrationInput, auth: dict = Depends(require_admin)):
    creator = auth.get("sub")  # F4(a): görevi kuyruklayan admin kaydedilir
    key = _orchestration_key(creator, data)
    first = _recent_orchestrations.get(key)
    if first is not None:
        # Aynı istek az önce geldi: ilkinin GERÇEK sonucu beklenir ve aynen döner (ilki başarısız olduysa bu da olur).
        # Eskiden ilki daha bitmeden "başarılı" dönülüyordu.
        try:
            created = await asyncio.wait_for(asyncio.shield(first[1]), 30)
        except Exception:
            raise HTTPException(
                status_code=409, detail="Aynı istek az önce gönderildi ve tamamlanamadı; tekrar deneyin."
            )
        return {"status": "success", "duplicate": True, "created": created}
    outcome = asyncio.get_running_loop().create_future()
    _recent_orchestrations[key] = (time.monotonic(), outcome)
    try:
        target_pcs = await resolve_targets(data.target_mode, data.targets)
        now = datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S")
        rows = [
            (target["pc"], target["lab"], task.command, now, creator)
            for target in target_pcs
            for task in data.taskSequence
        ]
        if rows:
            # Hepsi ya da hiçbiri: yarıda kalan bir istek hedeflerin bir kısmına görev bırakmaz
            async with db.transaction() as conn:
                await conn.executemany(
                    "INSERT INTO tasks (target_pc, target_lab, script_path, status, created_at, created_by) "
                    "VALUES ($1, $2, $3, 'Pending', $4, $5)",
                    rows,
                )
    except Exception as exc:
        _recent_orchestrations.pop(key, None)   # başarısız istek yeniden denenebilsin
        outcome.set_exception(exc)
        outcome.exception()   # bekleyen yoksa "alınmamış hata" uyarısı çıkmasın
        raise
    outcome.set_result(len(rows))
    await process_queue()
    return {"status": "success", "created": len(rows)}
