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
import tempfile
import time
import uuid
from typing import Optional

from fastapi import APIRouter, Depends, File, HTTPException, Query, Request, UploadFile
from fastapi.responses import FileResponse
from werkzeug.utils import secure_filename

from pops.config import LOG_TABLE, UPDATES_DIR, UPLOAD_DIR
from pops import db, modules, winget
from pops.db import execute_query
from pops.models import (
    CreatePackageInput, DeletePackageInput, OrchestrationInput, SetLimitInput, TaskActionInput, TaskStatusInput,
)
from pops.security import require_admin, require_auth
from pops.audit import add_audit_log
from pops.manager import manager
from pops.taskqueue import process_queue, resolve_targets

log = logging.getLogger("pops.tasks")
router = APIRouter()


@router.get("/api/tasks")
async def get_tasks(limit: int = 1000, auth: dict = Depends(require_auth)):
    rows = await execute_query("SELECT * FROM tasks ORDER BY id DESC LIMIT $1", (limit,), fetch=True)
    for r in rows or []:
        # winget görevinin paketi ({"id", "version"}); asyncpg JSONB'yi metin döndürür
        if isinstance(r.get("payload"), str):
            try:
                r["payload"] = json.loads(r["payload"])
            except ValueError:
                r["payload"] = None
    return rows if rows else []


@router.post("/api/flush_queue", deprecated=True)
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
        "Denied", "Expired",
    ),
}
# Bir görevin yeniden denemesi sürüyorsa (bu durumlarda) ikinci kopya açılmaz
_ACTIVE_STATUSES = ["Pending", "Running", "Paused", "Unknown"]
_ACTION_TARGET = {"CANCEL": "Cancelled", "RETRY": "Pending", "PAUSE": "Paused", "RESUME": "Pending"}


def _client_ip(request):
    # uvicorn güvenilen ters vekilin X-Forwarded-For başlığını zaten çözer; doğrudan çağrıda (testler) istek yoktur
    return request.client.host if request is not None and request.client else None


@router.post("/api/tasks/action")
async def handle_task_action(data: TaskActionInput, auth: dict = Depends(require_admin), request: Request = None):
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
        return await _retry(where, value(), auth.get("sub"), _client_ip(request))
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


async def _retry(where: str, value, creator: str, client_ip=None) -> dict:
    """Yeniden deneme YENİ bir görev kaydı açar (retry_of = eski görev); eski kayıt sonucuyla kalır. Eskiden aynı
    görev kimliği yeniden "Pending" yapılıyordu: iptal edilmiş ama hâlâ süren eski çalıştırmanın geç gelen sonucu
    yeni çalıştırmayı tamamlanmış gösterebilirdi. Aynı görevin süren bir yeniden denemesi varsa ikincisi açılmaz.
    Görevin türü (kind) ve paketi (payload) de kopyalanır: winget görevi komut olarak yeniden çalıştırılmaz."""
    now = datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S")
    created = await execute_query(
        "INSERT INTO tasks (target_pc, target_lab, script_path, status, created_at, created_by, retry_of, "
        "title, source, reason, client_ip, batch_id, kind, payload) "
        "SELECT t.target_pc, t.target_lab, t.script_path, 'Pending', $2, $3, t.id, "
        "t.title, 'tasks', t.reason, $6, $7, t.kind, t.payload FROM tasks t "
        f"WHERE {where.format(v='$1')} AND t.status = ANY($4::text[]) "
        "AND NOT EXISTS (SELECT 1 FROM tasks n WHERE n.retry_of = t.id AND n.status = ANY($5::text[])) "
        "RETURNING id",
        (value, now, creator, list(_ACTION_STATUSES["RETRY"]), _ACTIVE_STATUSES, client_ip, uuid.uuid4().hex[:16]),
        fetch=True,
    )
    await process_queue()
    ids = [r["id"] for r in created or []]
    return {"status": "success", "changed": len(ids), "task_ids": ids}


@router.post("/api/tasks/status")
async def task_status(data: TaskStatusInput, auth: dict = Depends(require_auth)):
    """Verilen görevlerin durumu (panelin işlem merkezi bir işin ilerlemesini buradan izler). Silinmiş görev listede
    yoktur."""
    rows = await execute_query(
        "SELECT id, target_pc, target_lab, status, exit_code, dispatched_at FROM tasks WHERE id = ANY($1::int[])",
        (list(dict.fromkeys(data.ids)),),
        fetch=True,
    )
    return {"items": [
        {"id": r["id"], "target_pc": r["target_pc"], "target_lab": r["target_lab"], "status": r["status"],
         "exit_code": r["exit_code"], "dispatched_at": r["dispatched_at"].isoformat() if r["dispatched_at"] else None}
        for r in rows or []
    ]}


@router.post("/api/set_concurrent_limit", deprecated=True)
async def set_concurrent_limit(data: SetLimitInput, auth: dict = Depends(require_admin)):
    await execute_query(
        "INSERT INTO global_settings (key, value) "
        "VALUES ('concurrent_limit', $1) ON CONFLICT (key) DO UPDATE "
        "SET value=EXCLUDED.value",
        (str(data.limit),),
    )
    await process_queue()
    return {"status": "success"}


@router.get("/api/get_concurrent_limit", deprecated=True)
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


def _store_upload(src, dest: str) -> str:
    """Yüklenen dosyayı geçici ada yazarken özetini de çıkarır, bitince yerine taşır (yarım dosya indirilemez).
    Büyük paketlerde olay döngüsü tıkanmasın diye iş parçacığında çalışır (B10)."""
    h = hashlib.sha256()
    fd, tmp = tempfile.mkstemp(dir=os.path.dirname(dest), prefix=".upload-")
    try:
        with os.fdopen(fd, "wb") as out:
            for chunk in iter(lambda: src.read(1024 * 1024), b""):
                h.update(chunk)
                out.write(chunk)
        os.chmod(tmp, 0o640)
        os.replace(tmp, dest)
    except BaseException:
        try:
            os.unlink(tmp)
        except OSError:
            pass
        raise
    return h.hexdigest()


@router.post("/api/upload", dependencies=[modules.require("deploy")], deprecated=True)
async def upload_file(request: Request, file: UploadFile = File(...), auth: dict = Depends(require_admin)):
    # Dosya adını temizle ("../", mutlak yol, ayraç vb. atılır)
    filename = secure_filename(file.filename or "")
    if not filename:
        raise HTTPException(status_code=400, detail="Geçersiz dosya adı")
    # Son yol mutlaka UPLOAD_DIR'in doğrudan içinde olmalı (path traversal / symlink engeli)
    file_path = os.path.realpath(os.path.join(UPLOAD_DIR, filename))
    if not file_path.startswith(UPLOAD_DIR + os.sep) or os.path.dirname(file_path) != UPLOAD_DIR:
        raise HTTPException(status_code=400, detail="Geçersiz dosya yolu")
    sha256 = await asyncio.to_thread(_store_upload, file.file, file_path)
    sig = _download_sig(await _download_key(), filename)
    return {
        "status": "success",
        "filename": filename,
        "url": f"{request.base_url}download/{filename}?sig={sig}",
        "sig": sig,
        "sha256": sha256,
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


@router.post("/api/add_package", dependencies=[modules.require("deploy")], deprecated=True)
async def add_package(data: CreatePackageInput, auth: dict = Depends(require_admin)):
    await execute_query(
        "INSERT INTO packages (id, name, type, meta, command, icon, color) "
        "VALUES ($1, $2, $3, $4, $5, $6, $7) ON CONFLICT (id) DO UPDATE "
        "SET name=EXCLUDED.name, type=EXCLUDED.type, meta=EXCLUDED.meta, command=EXCLUDED.command, "
        "icon=EXCLUDED.icon, color=EXCLUDED.color",
        (data.id, data.name, data.type, data.meta, data.command, data.icon, data.color),
    )
    return {"status": "success"}


@router.post("/api/delete_package", dependencies=[modules.require("deploy")], deprecated=True)
async def delete_package(data: DeletePackageInput, auth: dict = Depends(require_admin)):
    await execute_query("DELETE FROM packages WHERE id = $1", (data.id,))
    return {"status": "success"}


@router.get("/api/packages", dependencies=[modules.require("deploy")])
async def get_packages(auth: dict = Depends(require_auth)):
    rows = await execute_query("SELECT * FROM packages", fetch=True)
    return rows if rows else []


@router.get("/api/deploy/winget/catalog", dependencies=[modules.require("deploy")])
async def winget_catalog(
    q: str = Query("", max_length=100),
    category: Optional[str] = Query(None, max_length=40),
    limit: int = Query(200, ge=1, le=500),
    auth: dict = Depends(require_auth),
):
    """Okul ve ofis için seçilmiş winget paketleri (pops/winget_catalog.py; kimlikler winget-pkgs deposunda
    doğrulandı). q: kimlik, ad, yayıncı, kategori ya da açıklamada geçen sözcükler (Türkçe harf ve büyük/küçük harf
    farkı yok sayılır); category: kategori kimliği. Katalogda olmayan bir paket de kimliğiyle dağıtılabilir."""
    return winget.search(q, category, limit)


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
            [creator, data.target_mode, data.targets,
             [(t.type, t.command, t.winget.model_dump() if t.winget else None) for t in data.task_sequence]],
            ensure_ascii=False,
        ).encode("utf-8")
    ).hexdigest()


def _step_row(task) -> tuple:
    """Zincir adımı -> görev kaydının (script_path, title, kind, payload) alanları."""
    name = (task.name or "")[:200] or None
    if task.is_winget:
        spec = task.winget
        return (winget.command_line(spec.id, spec.version), name or "winget: %s" % spec.id, winget.KIND,
                winget.payload(spec.id, spec.version))
    return (task.command, name, None, None)


@router.post("/api/deploy_orchestration", deprecated=True)
async def deploy_orchestration(data: OrchestrationInput, auth: dict = Depends(require_admin), request: Request = None):
    creator = auth.get("sub")  # F4(a): görevi kuyruklayan admin kaydedilir
    key = _orchestration_key(creator, data)
    first = _recent_orchestrations.get(key)
    if first is not None:
        # Aynı istek az önce geldi: ilkinin GERÇEK sonucu beklenir ve aynen döner (ilki başarısız olduysa bu da olur).
        # Eskiden ilki daha bitmeden "başarılı" dönülüyordu.
        try:
            ids = await asyncio.wait_for(asyncio.shield(first[1]), 30)
        except Exception:
            raise HTTPException(
                status_code=409, detail="Aynı istek az önce gönderildi ve tamamlanamadı; tekrar deneyin."
            )
        return {"status": "success", "duplicate": True, "created": len(ids), "task_ids": ids}
    outcome = asyncio.get_running_loop().create_future()
    _recent_orchestrations[key] = (time.monotonic(), outcome)
    try:
        target_pcs = await resolve_targets(data.target_mode, data.targets)
        unknown = [t["pc"] for t in target_pcs if t.get("unknown")]
        if unknown:
            # Kayıtlı olmayan bilgisayara açılan görev hiçbir zaman gönderilemez ve süresiz bekler
            raise HTTPException(
                status_code=422,
                detail="Kayıtlı olmayan bilgisayar: %s" % ", ".join(unknown[:10]) + (" …" if len(unknown) > 10 else ""),
            )
        # Kütüphaneden paket/betik ve winget adımı dosya dağıtımı modülüne, serbest komut uzak komut modülüne
        # bağlıdır; her hedef kendi laboratuvarının ayarıyla denetlenir. Hiçbirinde açık değilse istek reddedilir.
        needed = "deploy" if any((t.type or "").upper() != "CMD" for t in data.task_sequence) else "terminal"
        allowed, closed = await modules.split_pcs(needed, [t["pc"] for t in target_pcs])
        if target_pcs and not allowed:
            raise modules.closed_error(needed)
        allowed = set(allowed)
        target_pcs = [t for t in target_pcs if t["pc"] in allowed]
        now = datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S")
        # Adım -> (komut, başlık, tür, paket). winget adımının komutu ajanın çalıştıracağı komut satırının okunur
        # hâlidir (ajana gitmez); ajan paketi "winget_install" iletisiyle alır (bkz. pops/winget.py).
        steps = [_step_row(task) for task in data.task_sequence]
        rows = [(target["pc"], target["lab"]) + step for target in target_pcs for step in steps]
        batch_id = uuid.uuid4().hex[:16]
        reason = (data.reason or "").strip() or None
        ids = []
        if rows:
            # Hepsi ya da hiçbiri: yarıda kalan bir istek hedeflerin bir kısmına görev bırakmaz. Kimlikler döner: panel
            # işin ilerlemesini bu görevler üzerinden izler (POST /api/tasks/status).
            async with db.transaction() as conn:
                created = await conn.fetch(
                    "INSERT INTO tasks (target_pc, target_lab, script_path, status, created_at, created_by, "
                    "title, source, reason, client_ip, batch_id, kind, payload) "
                    "SELECT t.pc, t.lab, t.cmd, 'Pending', $5, $6, COALESCE(t.title, $7), $8, $9, $10, $11, t.kind, "
                    "t.payload::jsonb "
                    "FROM unnest($1::text[], $2::text[], $3::text[], $4::text[], $12::text[], $13::text[]) "
                    "WITH ORDINALITY AS t(pc, lab, cmd, title, kind, payload, n) ORDER BY t.n RETURNING id",
                    [r[0] for r in rows], [r[1] for r in rows], [r[2] for r in rows], [r[3] for r in rows],
                    now, creator, data.title, data.source, reason, _client_ip(request), batch_id,
                    [r[4] for r in rows], [r[5] for r in rows],
                )
            ids = [r["id"] for r in created]
    except Exception as exc:
        _recent_orchestrations.pop(key, None)   # başarısız istek yeniden denenebilsin
        outcome.set_exception(exc)
        outcome.exception()   # bekleyen yoksa "alınmamış hata" uyarısı çıkmasın
        raise
    outcome.set_result(ids)
    await process_queue()
    out = {"status": "success", "created": len(ids), "task_ids": ids}
    if closed:
        out["skipped_module_closed"] = len(closed)   # modül kapalı laboratuvardaki hedefler
    return out
