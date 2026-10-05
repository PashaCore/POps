"""Dosya aktarımı: yönetici bilgisayara dosya gönderir (push) ya da bilgisayardan dosya alır (pull).

Akış (ajan mesajları ve uçlar: docs/api.md "File transfer"):
  push: admin dosyayı yükler -> sunucu saklar (SHA-256) -> her çevrimiçi ve özelliği açık bilgisayar için bir aktarım
        ve tek kullanımlık, 1 saatlik jeton -> ajana file_push -> ajan GET /api/files/<id>/download?t=… ile kendi
        anahtarıyla indirir, doğrular, izinli klasöre yazar -> file_result.
  pull: admin yol + boyut sınırı + gerekçe ister (any_profile yalnız superadmin) -> ajana file_pull -> ajan dosyayı
        POST /api/files/<id>/upload?t=… ile kendi anahtarıyla yükler -> admin GET /api/files/<id>/content ile indirir
        (her zaman ek olarak, nosniff). Alınan dosya 7 gün saklanır (pops/filestore.py).

Kurum birimi kapsamı (pops/tenancy.py): kapsamlı hesap yalnızca kendi sınıflarındaki bilgisayarlara gönderir ve
onlardan alır; kapsam dışı bilgisayar "kayıtlı değil" (unknown) sayılır, liste ve indirme de kapsamla süzülür.

Gönderme, alma ve alınan dosyayı indirme yalnızca panel oturumundaki admin içindir (API jetonu kullanılamaz: dosya
kişisel veri içerebilir, işlemi gerekçesiyle bir kişi yapar). Liste (yalnız üst veri) her oturumla okunur.

Güvenlik: ajan uçları yalnızca anahtarlı ajanı kabul eder (kimlik zorlaması kapalı olsa da) ve aktarımın kendi
bilgisayarına ait olduğunu jetonla birlikte denetler; yanlış jeton, başka bilgisayar, kullanılmış ya da süresi dolmuş
jeton aynı 404'ü alır. Her gönderim, istek, alınan dosya ve panelden indirme hash zincirli denetim kaydına yazılır
(yalnız üst veri; dosyanın içeriği asla).
"""

import asyncio
import datetime
import hashlib
import hmac
import logging
import os
import secrets
import time
from typing import Optional

from fastapi import APIRouter, Depends, HTTPException, Request
from fastapi.responses import FileResponse
from pydantic import Field
from starlette.datastructures import UploadFile
from starlette.requests import ClientDisconnect

from pops import db, filestore, modules, tenancy, timeutil
from pops.agent_auth import agent_http_auth
from pops.audit import add_audit_log
from pops.db import execute_query
from pops.manager import manager
from pops.models import StrictInput
from pops.security import require_admin_session, require_auth

log = logging.getLogger("pops.files")
router = APIRouter()

# Ajanın çalıştırmadığı isteğin sonucu bu önekle başlar (bkz. routers/agents.py REFUSED_PREFIX)
REFUSED_PREFIX = "[REDDEDİLDİ]"
DESTINATIONS = ("public_desktop", "inbox")
FINAL = ("done", "rejected", "failed", "expired")
MAX_TARGETS = 500
REASON_MIN, REASON_MAX = 3, 300

# Ajan uçları: cihaz başına dakikada en fazla bu kadar istek (diğer ajan uçlarındaki gibi cihaz başına sınır)
AGENT_RATE_PER_MINUTE = 30
_agent_hits = {}


def _agent_rate(kind: str, pc_name: str) -> None:
    now = time.monotonic()
    key = (kind, pc_name)
    hits = [t for t in _agent_hits.get(key, ()) if now - t < 60.0]
    if len(hits) >= AGENT_RATE_PER_MINUTE:
        _agent_hits[key] = hits
        raise HTTPException(status_code=429, detail="Çok sık istek; birkaç saniye sonra tekrar deneyin.")
    hits.append(now)
    _agent_hits[key] = hits
    if len(_agent_hits) > 10000:  # bellek sınırı
        for k in [k for k, v in _agent_hits.items() if not v or now - v[-1] > 60]:
            _agent_hits.pop(k, None)


def _hash_token(token: str) -> str:
    return hashlib.sha256(token.encode("utf-8")).hexdigest()


def _new_id() -> str:
    return secrets.token_urlsafe(16)


def _clean_reason(raw) -> str:
    reason = " ".join(str(raw or "").split())
    if len(reason) < REASON_MIN:
        raise HTTPException(status_code=400, detail="Gerekçe yazın (en az 3 karakter).")
    return reason[:REASON_MAX]


def _text(value, limit: int) -> Optional[str]:
    """Ajandan gelen metin: denetim/biçim karakterleri boşluk olur, boşluklar sadeleşir, uzunluk sınırlanır."""
    if not isinstance(value, str):
        return None
    clean = "".join(ch if ch.isprintable() else " " for ch in value)
    clean = " ".join(clean.split())[:limit]
    return clean or None


def _not_found() -> HTTPException:
    return HTTPException(status_code=404, detail="Bulunamadı")


async def _require_agent(agent_id: Optional[str]) -> str:
    # Dosya uçları kimliksiz (eski, anahtarsız) ajanı kimlik zorlaması kapalıyken de kabul etmez
    if agent_id is None:
        raise HTTPException(status_code=401, detail="Bu uç yalnızca kayıtlı (anahtarlı) ajanları kabul eder.")
    return agent_id


async def _broadcast(pc_name: str, transfer_id: str, direction: str, status: str) -> None:
    await manager.broadcast_to_admin_panels(
        {"type": "file_transfer", "pc_name": pc_name, "transfer_id": transfer_id, "direction": direction,
         "status": status},
        device=pc_name,
    )


async def _targets(pcs, auth: dict) -> tuple:
    """(gönderilebilir bilgisayarlar [(pc, lab)], atlananlar [{pc_name, reason}]). Sebep: unknown (kayıtlı değil ya da
    isteyenin kurum birimi kapsamı dışında),
    module_closed (dosya aktarımı modülü kapalı), offline, unsupported (ajan özelliği bilmiyor), disabled (bilgisayarda
    kapatılmış)."""
    rows = await execute_query(
        "SELECT pc_name, lab_name, cap_files_enabled FROM clients WHERE pc_name = ANY($1::text[])", (pcs,), fetch=True
    )
    scope = await tenancy.scope_of(auth)
    known = {r["pc_name"]: r for r in rows or [] if scope.allows_lab(r["lab_name"])}
    up = await manager.online_among(pcs)   # birden fazla süreçte bütün süreçlerin ajanları
    ok, skipped = [], []
    lab_on = {}
    for pc in pcs:
        r = known.get(pc)
        if r is None:
            skipped.append({"pc_name": pc, "reason": "unknown"})
            continue
        lab = r["lab_name"]
        if lab not in lab_on:
            lab_on[lab] = await modules.enabled("files", lab)
        if not lab_on[lab]:
            skipped.append({"pc_name": pc, "reason": "module_closed"})
        elif pc not in up:
            skipped.append({"pc_name": pc, "reason": "offline"})
        elif r["cap_files_enabled"] is None:
            skipped.append({"pc_name": pc, "reason": "unsupported"})
        elif not r["cap_files_enabled"]:
            skipped.append({"pc_name": pc, "reason": "disabled"})
        else:
            ok.append((pc, lab))
    return ok, skipped


_SKIP_TEXT = {
    "unknown": "Bilgisayar kayıtlı değil.",
    "module_closed": "'Dosya aktarımı' modülü kapalı.",
    "offline": "Bilgisayar çevrimdışı.",
    "unsupported": "Bu bilgisayardaki ajan dosya aktarımını desteklemiyor.",
    "disabled": "Dosya aktarımı bu bilgisayarda kapalı.",
    "send_failed": "Ajana ulaştırılamadı (bağlantı koptu).",
}


def _skip_error(skipped: list) -> HTTPException:
    if len(skipped) == 1:
        return HTTPException(status_code=409, detail=_SKIP_TEXT.get(skipped[0]["reason"], "Gönderilemedi."))
    return HTTPException(
        status_code=409,
        detail="Seçili bilgisayarların hiçbirine gönderilemedi (çevrimdışı ya da dosya aktarımı kapalı).",
    )


def _form_bool(value) -> bool:
    return str(value or "").strip().lower() in ("1", "true", "on", "yes")


# Gövdesi elle okunan uçların şemadaki gövdesi (docs/openapi.json)
_BINARY = {"type": "string", "format": "binary"}
_PUSH_BODY = {"requestBody": {"required": True, "content": {"multipart/form-data": {"schema": {
    "type": "object",
    "required": ["file", "pcs", "dest", "reason"],
    "properties": {
        "file": _BINARY,
        "pcs": {"type": "array", "items": {"type": "string"}, "maxItems": MAX_TARGETS},
        "dest": {"type": "string", "enum": list(DESTINATIONS)},
        "reason": {"type": "string", "minLength": REASON_MIN, "maxLength": REASON_MAX},
        "allow_exec": {"type": "boolean", "default": False},
    },
}}}}}
_UPLOAD_BODY = {"requestBody": {"required": True, "content": {
    "application/octet-stream": {"schema": _BINARY},
    "multipart/form-data": {"schema": {"type": "object", "required": ["file"], "properties": {"file": _BINARY}}},
}}}


# ── Push: admin -> bilgisayar ───────────────────────────────────────────────────

@router.post("/api/files/push", dependencies=[modules.require("files")], openapi_extra=_PUSH_BODY)
async def push_file(request: Request, auth: dict = Depends(require_admin_session)):
    """Multipart: file, pcs (bir ya da birçok kez), dest, reason, allow_exec. Gövde yetki denetiminden SONRA okunur
    (oturumsuz biri 200 MB yükleyip sunucuya geçici dosya yazdıramaz)."""
    length = request.headers.get("content-length")
    if not length or not length.isdigit():
        raise HTTPException(status_code=411, detail="Content-Length gerekli.")
    if int(length) > filestore.PUSH_MAX_BYTES + 1024 * 1024:
        raise HTTPException(
            status_code=413, detail="Dosya çok büyük (en fazla %d MB)." % (filestore.PUSH_MAX_BYTES >> 20)
        )
    form = await request.form(max_files=1, max_fields=MAX_TARGETS + 10)
    try:
        upload = form.get("file")
        if not isinstance(upload, UploadFile):
            raise HTTPException(status_code=400, detail="Dosya seçin.")
        name = filestore.clean_name(upload.filename)
        if not name:
            raise HTTPException(status_code=400, detail="Geçersiz dosya adı.")
        dest = str(form.get("dest") or "")
        if dest not in DESTINATIONS:
            raise HTTPException(status_code=400, detail="Geçersiz hedef klasör.")
        reason = _clean_reason(form.get("reason"))
        allow_exec = _form_bool(form.get("allow_exec"))
        if filestore.needs_exec(name) and not allow_exec:
            raise HTTPException(
                status_code=400,
                detail="Kısayol ve ekran koruyucu dosyaları (.lnk, .url, .scr) yalnızca "
                "'çalıştırılabilir dosyaya izin ver' işaretliyse gönderilir.",
            )
        pcs = list(dict.fromkeys(str(p).strip() for p in form.getlist("pcs") if str(p).strip()))
        if not pcs:
            raise HTTPException(status_code=400, detail="Hedef bilgisayar seçin.")
        if len(pcs) > MAX_TARGETS or any(len(p) > 100 for p in pcs):
            raise HTTPException(status_code=400, detail="En fazla %d bilgisayar seçilebilir." % MAX_TARGETS)
        targets, skipped = await _targets(pcs, auth)
        if not targets:
            raise _skip_error(skipped)
        batch_id = _new_id()
        blob = "push-%s.bin" % batch_id
        try:
            size, sha256 = await asyncio.to_thread(filestore.store_file, upload.file, blob, filestore.PUSH_MAX_BYTES)
        except filestore.TooLarge:
            raise HTTPException(
                status_code=413, detail="Dosya çok büyük (en fazla %d MB)." % (filestore.PUSH_MAX_BYTES >> 20)
            )
        if size == 0:
            filestore.remove_blob(blob)
            raise HTTPException(status_code=400, detail="Dosya boş.")
    finally:
        await form.close()

    admin = auth.get("sub")
    expires = datetime.datetime.now(datetime.timezone.utc) + datetime.timedelta(seconds=filestore.TOKEN_TTL_SECONDS)
    plan = [(pc, _new_id(), secrets.token_urlsafe(32)) for pc, _lab in targets]
    async with db.transaction() as conn:
        await conn.executemany(
            "INSERT INTO file_transfers (transfer_id, direction, pc_name, batch_id, name, size, sha256, dest, "
            "allow_exec, reason, status, created_by, token_hash, token_expires_at, storage_path) "
            "VALUES ($1, 'push', $2, $3, $4, $5, $6, $7, $8, $9, 'sent', $10, $11, $12, $13)",
            [(tid, pc, batch_id, name, size, sha256, dest, allow_exec, reason, admin, _hash_token(tok), expires, blob)
             for pc, tid, tok in plan],
        )
    sent = []
    for pc, tid, tok in plan:
        msg = {
            "action": "file_push",
            "transfer_id": tid,
            "name": name,
            "size": size,
            "sha256": sha256,
            "url": "/api/files/%s/download?t=%s" % (tid, tok),
            "dest": dest,
            "reason": reason,
            "allow_exec": allow_exec,
        }
        if await manager.send_command(msg, pc):
            sent.append({"pc_name": pc, "transfer_id": tid})
            await add_audit_log(
                pc,
                "file_push",
                "Bilgisayara dosya gönderildi: %s" % admin,
                {"transfer_id": tid, "batch_id": batch_id, "name": name, "size": size, "sha256": sha256,
                 "dest": dest, "allow_exec": allow_exec, "reason": reason, "admin": admin},
            )
        else:
            skipped.append({"pc_name": pc, "reason": "send_failed"})
            await execute_query(
                "UPDATE file_transfers SET status = 'failed', detail = $2, finished_at = NOW() "
                "WHERE transfer_id = $1 AND status = 'sent'",
                (tid, _SKIP_TEXT["send_failed"]),
            )
    if not sent:
        raise _skip_error(skipped)
    return {"status": "success", "batch_id": batch_id, "name": name, "size": size, "sha256": sha256,
            "transfers": sent, "skipped": skipped}


@router.get("/api/files/{transfer_id}/download")
async def agent_download(transfer_id: str, t: str = "", agent_id: Optional[str] = Depends(agent_http_auth)):
    """Ajan, kendisine gönderilen dosyayı indirir. Jeton tek kullanımlıktır ve 1 saatte dolar; aktarım bu ajanın
    bilgisayarına ait olmalıdır. Yanlış jeton, başka bilgisayar, kullanılmış/süresi dolmuş jeton: aynı 404 (jeton
    tüketilmez)."""
    pc = await _require_agent(agent_id)
    _agent_rate("download", pc)
    if not filestore.valid_id(transfer_id) or not t or len(t) > 200:
        raise _not_found()
    await modules.check("files", pc_name=pc)
    rows = await execute_query(
        "UPDATE file_transfers SET token_used_at = NOW(), status = 'downloading' "
        "WHERE transfer_id = $1 AND pc_name = $2 AND direction = 'push' AND token_hash = $3 AND status = 'sent' "
        "AND token_used_at IS NULL AND token_expires_at > NOW() AND storage_path IS NOT NULL "
        "RETURNING name, size, sha256, storage_path",
        (transfer_id, pc, _hash_token(t)),
        fetch=True,
    )
    if not rows:
        log.info("dosya indirme reddedildi", extra={"pc_name": pc, "transfer_id": transfer_id})
        raise _not_found()
    row = rows[0]
    path = filestore.blob_path(row["storage_path"])
    if not path or not await asyncio.to_thread(os.path.isfile, path):
        await execute_query(
            "UPDATE file_transfers SET status = 'failed', detail = 'Sunucudaki dosya bulunamadı.', finished_at = NOW() "
            "WHERE transfer_id = $1",
            (transfer_id,),
        )
        raise _not_found()
    await _broadcast(pc, transfer_id, "push", "downloading")
    return FileResponse(
        path,
        filename=row["name"],
        media_type="application/octet-stream",
        headers={"X-Content-SHA256": row["sha256"], "Cache-Control": "no-store", "X-Content-Type-Options": "nosniff"},
    )


# ── Pull: bilgisayar -> admin ───────────────────────────────────────────────────

class FilePullInput(StrictInput):
    pc: str = Field(min_length=1, max_length=100)
    path: str = Field(min_length=1, max_length=1024)
    max_size: int = Field(ge=1, le=filestore.PULL_MAX_BYTES)
    reason: str = Field(max_length=1000)
    any_profile: bool = False


@router.post("/api/files/pull", dependencies=[modules.require("files")])
async def pull_file(data: FilePullInput, auth: dict = Depends(require_admin_session)):
    """Bilgisayardan dosya ister. Gerekçe zorunlu ve denetim kaydına yazılır. any_profile (başka kullanıcıların
    profilinden de) yalnızca superadmin içindir."""
    if data.any_profile and auth.get("role") != "superadmin":
        raise HTTPException(
            status_code=403, detail="Başka kullanıcıların profilinden dosya almak için superadmin yetkisi gerekir."
        )
    reason = _clean_reason(data.reason)
    try:
        path = filestore.check_pull_path(data.path)
    except ValueError as exc:
        raise HTTPException(status_code=400, detail=str(exc))
    pc = data.pc.strip()
    targets, skipped = await _targets([pc], auth)
    if not targets:
        raise _skip_error(skipped)
    admin = auth.get("sub")
    tid, token = _new_id(), secrets.token_urlsafe(32)
    expires = datetime.datetime.now(datetime.timezone.utc) + datetime.timedelta(seconds=filestore.TOKEN_TTL_SECONDS)
    name = filestore.name_of_path(path)
    await execute_query(
        "INSERT INTO file_transfers (transfer_id, direction, pc_name, batch_id, name, path, max_size, any_profile, "
        "reason, status, created_by, token_hash, token_expires_at) "
        "VALUES ($1, 'pull', $2, $1, $3, $4, $5, $6, $7, 'sent', $8, $9, $10)",
        (tid, pc, name, path, data.max_size, data.any_profile, reason, admin, _hash_token(token), expires),
    )
    msg = {
        "action": "file_pull",
        "transfer_id": tid,
        "path": path,
        "max_size": data.max_size,
        "upload": "/api/files/%s/upload?t=%s" % (tid, token),
        "reason": reason,
        "any_profile": data.any_profile,
    }
    if not await manager.send_command(msg, pc):
        await execute_query(
            "UPDATE file_transfers SET status = 'failed', detail = $2, finished_at = NOW() WHERE transfer_id = $1",
            (tid, _SKIP_TEXT["send_failed"]),
        )
        raise _skip_error([{"pc_name": pc, "reason": "send_failed"}])
    await add_audit_log(
        pc,
        "file_pull",
        "Bilgisayardan dosya istendi: %s" % admin,
        {"transfer_id": tid, "path": path, "max_size": data.max_size, "any_profile": data.any_profile,
         "reason": reason, "admin": admin, "role": auth.get("role")},
    )
    return {"status": "success", "transfer_id": tid, "name": name}


async def _fail(transfer_id: str, detail: str) -> None:
    await execute_query(
        "UPDATE file_transfers SET status = 'failed', detail = $2, finished_at = NOW() "
        "WHERE transfer_id = $1 AND status = 'uploading'",
        (transfer_id, detail),
    )


@router.post("/api/files/{transfer_id}/upload", openapi_extra=_UPLOAD_BODY)
async def agent_upload(
    transfer_id: str, request: Request, t: str = "", agent_id: Optional[str] = Depends(agent_http_auth)
):
    """Ajan istenen dosyayı yükler. Gövde ham dosyadır (application/octet-stream; önerilen) ya da multipart 'file'
    alanı. İsteğe bağlı X-Content-SHA256 başlığı verilirse özet tutmalıdır. Jeton tek kullanımlık: yarıda kalan ya da
    boyut sınırını aşan yükleme aktarımı 'failed' yapar."""
    pc = await _require_agent(agent_id)
    _agent_rate("upload", pc)
    if not filestore.valid_id(transfer_id) or not t or len(t) > 200:
        raise _not_found()
    await modules.check("files", pc_name=pc)
    rows = await execute_query(
        "UPDATE file_transfers SET token_used_at = NOW(), status = 'uploading' "
        "WHERE transfer_id = $1 AND pc_name = $2 AND direction = 'pull' AND token_hash = $3 AND status = 'sent' "
        "AND token_used_at IS NULL AND token_expires_at > NOW() RETURNING max_size, name",
        (transfer_id, pc, _hash_token(t)),
        fetch=True,
    )
    if not rows:
        log.info("dosya yükleme reddedildi", extra={"pc_name": pc, "transfer_id": transfer_id})
        raise _not_found()
    limit = int(rows[0]["max_size"] or filestore.PULL_MAX_BYTES)
    too_large = HTTPException(status_code=413, detail="Dosya izin verilen boyuttan büyük.")
    length = request.headers.get("content-length")
    ctype = (request.headers.get("content-type") or "").lower()
    multipart = ctype.startswith("multipart/form-data")
    slack = 64 * 1024 if multipart else 0
    if length and length.isdigit() and int(length) > limit + slack:
        await _fail(transfer_id, "Dosya izin verilen boyuttan büyük (%s > %s bayt)." % (length, limit))
        raise too_large
    blob = "pull-%s.bin" % transfer_id
    try:
        if multipart:
            form = await request.form(max_files=1, max_fields=5)
            try:
                upload = form.get("file")
                if not isinstance(upload, UploadFile):
                    await _fail(transfer_id, "Yüklemede dosya yok.")
                    raise HTTPException(status_code=400, detail="Yüklemede 'file' alanı yok.")
                size, sha256 = await asyncio.to_thread(filestore.store_file, upload.file, blob, limit)
            finally:
                await form.close()
        else:
            size, sha256 = await filestore.store_stream(request.stream(), blob, limit)
    except filestore.TooLarge:
        await _fail(transfer_id, "Dosya izin verilen boyuttan büyük (sınır %s bayt)." % limit)
        raise too_large
    except ClientDisconnect:
        await _fail(transfer_id, "Yükleme yarıda kaldı.")
        raise HTTPException(status_code=400, detail="Yükleme yarıda kaldı.")
    claimed = (request.headers.get("x-content-sha256") or "").strip().lower()
    if claimed and not hmac.compare_digest(claimed, sha256):
        await asyncio.to_thread(filestore.remove_blob, blob)
        await _fail(transfer_id, "SHA-256 tutmadı.")
        raise HTTPException(status_code=400, detail="SHA-256 tutmadı.")
    done = await execute_query(
        "UPDATE file_transfers SET status = 'done', size = $2, sha256 = $3, storage_path = $4, finished_at = NOW(), "
        "detail = NULL WHERE transfer_id = $1 AND status = 'uploading' RETURNING id",
        (transfer_id, size, sha256, blob),
        fetch=True,
    )
    if not done:
        # Bu arada başka bir yoldan kapandı (ör. modül kapatıldı): dosya tutulmaz
        await asyncio.to_thread(filestore.remove_blob, blob)
        raise _not_found()
    await add_audit_log(
        pc, "file_pull_received", "Bilgisayardan istenen dosya alındı",
        {"transfer_id": transfer_id, "name": rows[0]["name"], "size": size, "sha256": sha256},
    )
    await _broadcast(pc, transfer_id, "pull", "done")
    return {"status": "success", "size": size, "sha256": sha256}


# ── Panel: liste ve alınan dosyayı indirme ─────────────────────────────────────

def _iso(v):
    return timeutil.iso(v) if isinstance(v, (datetime.datetime, datetime.date)) else v


def _item(r: dict) -> dict:
    expires = None
    if r["direction"] == "pull" and r["storage_path"]:
        expires = r["created_at"] + datetime.timedelta(days=filestore.PULL_KEEP_DAYS)
    return {
        "transfer_id": r["transfer_id"],
        "direction": r["direction"],
        "pc_name": r["pc_name"],
        "batch_id": r["batch_id"],
        "name": r["name"],
        "size": r["size"],
        "sha256": r["sha256"],
        "dest": r["dest"],
        "path": r["path"],
        "max_size": r["max_size"],
        "allow_exec": r["allow_exec"],
        "any_profile": r["any_profile"],
        "reason": r["reason"],
        "status": r["status"],
        "detail": r["detail"],
        "created_by": r["created_by"],
        "created_at": _iso(r["created_at"]),
        "finished_at": _iso(r["finished_at"]),
        "downloadable": bool(r["direction"] == "pull" and r["status"] == "done" and r["storage_path"]),
        "expires_at": _iso(expires),
        "purged": r["purged_at"] is not None,
    }


@router.get("/api/files")
async def list_transfers(pc: Optional[str] = None, limit: int = 50, auth: dict = Depends(require_auth)):
    """Aktarımlar, yeniden eskiye (?pc= ile bir bilgisayarın). Yalnız üst veri; dosyanın kendisi /content'ten."""
    limit = max(1, min(int(limit), 200))
    cols = (
        "transfer_id, direction, pc_name, batch_id, name, size, sha256, dest, path, max_size, allow_exec, "
        "any_profile, reason, status, detail, created_by, created_at, finished_at, storage_path, purged_at"
    )
    args = [limit]
    where = [tenancy.device_sql(await tenancy.scope_of(auth), "pc_name", args)]
    if pc:
        args.append(pc)
        where.append("pc_name = $%d" % len(args))
    rows = await execute_query(
        "SELECT %s FROM file_transfers WHERE %s ORDER BY id DESC LIMIT $1" % (cols, " AND ".join(where)),
        tuple(args), fetch=True,
    )
    return {"items": [_item(r) for r in rows or []], "push_max_bytes": filestore.PUSH_MAX_BYTES,
            "pull_max_bytes": filestore.PULL_MAX_BYTES, "pull_keep_days": filestore.PULL_KEEP_DAYS}


@router.get("/api/files/{transfer_id}/content", dependencies=[modules.require("files")])
async def download_content(transfer_id: str, auth: dict = Depends(require_admin_session)):
    """Bilgisayardan alınan dosya: her zaman ek (attachment) olarak, içerik türü tahmin edilmeden (nosniff) ve
    önbelleğe alınmadan. İndirme denetim kaydına yazılır."""
    if not filestore.valid_id(transfer_id):
        raise _not_found()
    rows = await execute_query(
        "SELECT pc_name, name, size, sha256, storage_path FROM file_transfers "
        "WHERE transfer_id = $1 AND direction = 'pull' AND status = 'done' AND storage_path IS NOT NULL",
        (transfer_id,),
        fetch=True,
    )
    if not rows:
        raise _not_found()
    row = rows[0]
    if not (await tenancy.scope_of(auth)).allows_lab(await tenancy.lab_of(row["pc_name"])):
        raise _not_found()   # kapsam dışındaki bilgisayarın dosyası (kurum birimleri)
    path = filestore.blob_path(row["storage_path"])
    if not path or not await asyncio.to_thread(os.path.isfile, path):
        raise _not_found()
    await add_audit_log(
        row["pc_name"], "file_content_download", "Alınan dosya panelden indirildi: %s" % auth.get("sub"),
        {"transfer_id": transfer_id, "name": row["name"], "size": row["size"], "sha256": row["sha256"],
         "admin": auth.get("sub")},
    )
    return FileResponse(
        path,
        filename=row["name"] or "dosya",
        media_type="application/octet-stream",
        content_disposition_type="attachment",
        headers={
            "X-Content-Type-Options": "nosniff",
            "Cache-Control": "no-store",
            "Content-Security-Policy": "default-src 'none'; sandbox",
            "X-Content-SHA256": row["sha256"] or "",
        },
    )


# ── Ajan WebSocket iletileri (routers/agents.py çağırır) ───────────────────────

async def handle_result(pc_name: str, payload: dict) -> None:
    """file_result: {transfer_id, outcome: done|rejected|failed, path?, detail?} (status değil: status alanı olan ileti
    her sunucuda kalp atışıdır). Yalnız bu bilgisayarın açık
    aktarımı güncellenir. Gönderilen dosya için "done" ancak dosya indirildikten sonra kabul edilir; alınan dosya
    yükleme ucunda "done" olur (ajanın sonradan gönderdiği sonuç yalnız yolu tamamlar). "[REDDEDİLDİ]" ile başlayan
    açıklama "rejected" sayılır."""
    tid = payload.get("transfer_id")
    status = payload.get("outcome")
    if not filestore.valid_id(tid) or status not in ("done", "rejected", "failed"):
        log.info("tanınmayan file_result yok sayıldı", extra={"pc_name": pc_name})
        return
    detail = _text(payload.get("detail"), 500)
    path = _text(payload.get("path"), 1024)
    if detail and detail.startswith(REFUSED_PREFIX):
        status = "rejected"
    rows = await execute_query(
        "UPDATE file_transfers SET status = $3, detail = COALESCE($4, detail), "
        "path = CASE WHEN direction = 'push' THEN COALESCE($5, path) ELSE COALESCE(path, $5) END, "
        "finished_at = NOW() "
        "WHERE transfer_id = $1 AND pc_name = $2 AND ("
        "  (direction = 'push' AND (status = 'downloading' OR (status = 'sent' AND $3 <> 'done'))) "
        "  OR (direction = 'pull' AND status = 'sent' AND $3 <> 'done')) "
        "RETURNING direction, name",
        (tid, pc_name, status, detail, path),
        fetch=True,
    )
    if not rows:
        log.info("file_result eşleşmedi", extra={"pc_name": pc_name, "transfer_id": tid, "status": status})
        return
    await add_audit_log(
        pc_name, "file_result", "Dosya aktarımı sonucu: %s" % status,
        {"transfer_id": tid, "direction": rows[0]["direction"], "name": rows[0]["name"], "status": status,
         "path": path, "detail": detail},
    )
    await _broadcast(pc_name, tid, rows[0]["direction"], status)


async def handle_denied(pc_name: str, payload: dict) -> None:
    """capability_denied (capability = files): bilgisayarda dosya aktarımı kapalı. Yetenek kapalı yazılır, iletide
    transfer_id varsa o aktarım "rejected" olur."""
    await execute_query("UPDATE clients SET cap_files_enabled = FALSE WHERE pc_name = $1", (pc_name,))
    tid = payload.get("transfer_id")
    if not filestore.valid_id(tid):
        return
    rows = await execute_query(
        "UPDATE file_transfers SET status = 'rejected', finished_at = NOW(), "
        "detail = 'Dosya aktarımı bu bilgisayarda kapalı (ajan reddetti).' "
        "WHERE transfer_id = $1 AND pc_name = $2 AND status IN ('sent', 'downloading') RETURNING direction",
        (tid, pc_name),
        fetch=True,
    )
    if rows:
        await _broadcast(pc_name, tid, rows[0]["direction"], "rejected")


async def cancel_open(pcs) -> int:
    """Modül kapatılınca: bu bilgisayarlara gönderilmiş, henüz başlamamış aktarımlar iptal (jetonlar geçersiz)."""
    rows = await execute_query(
        "UPDATE file_transfers SET status = 'rejected', finished_at = NOW(), "
        "detail = 'Dosya aktarımı modülü kapatıldı.' WHERE pc_name = ANY($1::text[]) AND status = 'sent' RETURNING id",
        (list(pcs),),
        fetch=True,
    )
    return len(rows or [])
