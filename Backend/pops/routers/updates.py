"""ESKİ (imzasız zip) güncelleme uçları. v0.1.3+ ajanlar yalnız imzalı güncellemeyi kabul eder;
bunlar geriye uyum için duruyor (yeni yol: system_routes /api/system/deploy-update)."""

import datetime
import hashlib
import os
import re
import zipfile

from fastapi import APIRouter, Depends, File, HTTPException, Request, UploadFile

from pops.config import UPDATES_DIR
from pops.db import execute_query
from pops.security import require_admin, require_auth
from pops.manager import manager

router = APIRouter()


@router.post("/api/upload_update")
async def upload_update(request: Request, file: UploadFile = File(...), auth: dict = Depends(require_admin)):
    try:
        timestamp = datetime.datetime.now().strftime("%Y%m%d_%H%M%S")
        filename = f"pops_update_{timestamp}.zip"
        file_path = os.path.join(UPDATES_DIR, filename)
        content = await file.read()
        with open(file_path, "wb") as f:
            f.write(content)

        has_agent, has_updater = False, False
        try:
            with zipfile.ZipFile(file_path, 'r') as zf:
                for name in zf.namelist():
                    if "POpsAgent" in name:
                        has_agent = True
                    if "POpsUpdater" in name:
                        has_updater = True
        except Exception:
            os.remove(file_path)
            return {"status": "error", "message": "ZIP dosyası bozuk"}

        if not has_agent or not has_updater:
            os.remove(file_path)
            return {"status": "error", "message": "ZIP dosyası gerekli exeleri içermiyor!"}

        file_hash = hashlib.sha256(content).hexdigest()

        dl_url = f"{request.base_url}updates/{filename}"
        await execute_query(
            "INSERT INTO global_settings (key, value) "
            "VALUES ('latest_update_url', $1) ON CONFLICT (key) DO UPDATE "
            "SET value=$1",
            (dl_url,),
        )
        await execute_query(
            "INSERT INTO global_settings (key, value) "
            "VALUES ('latest_update_version', $1) "
            "ON CONFLICT (key) DO UPDATE SET value=$1",
            (f"update_{timestamp}",),
        )
        await execute_query(
            "INSERT INTO global_settings (key, value) "
            "VALUES ('latest_update_hash', $1) ON CONFLICT (key) DO UPDATE "
            "SET value=$1",
            (file_hash,),
        )
        return {"status": "success", "download_url": dl_url, "hash": file_hash}
    except Exception as e:
        return {"status": "error", "message": str(e)}


@router.get("/api/latest_update")
async def get_latest_update(auth: dict = Depends(require_auth)):
    row = await execute_query("SELECT value FROM global_settings WHERE key = 'latest_update_url'", fetch=True)
    return {"download_url": row[0]["value"] if row else None}


SHA256_HEX_RE = re.compile(r"[0-9a-f]{64}")


async def get_update_command():
    """Sunucuya yüklenmiş son paketten ajan güncelleme emrini üretir.

    Paket adresi istekten (dışarıdan) alınmaz, yalnızca /api/upload_update ile bu sunucuya
    yüklenen paket gönderilir. SHA-256 özeti zorunludur; özeti olmayan paket gönderilmez.
    Hata durumunda (None, mesaj) döner.
    """
    rows = await execute_query(
        "SELECT key, value FROM global_settings WHERE key IN ('latest_update_url', 'latest_update_hash')", fetch=True
    )
    settings = {r["key"]: r["value"] for r in (rows or [])}
    url = settings.get("latest_update_url")
    file_hash = (settings.get("latest_update_hash") or "").lower()
    if not url:
        return None, "Sunucuda güncelleme paketi yok"
    if not SHA256_HEX_RE.fullmatch(file_hash):
        return None, "Paketin SHA-256 özeti yok, paketi yeniden yükleyin"
    package = os.path.basename(url.rstrip("/"))
    if not os.path.isfile(os.path.join(UPDATES_DIR, package)):
        return None, "Güncelleme paketi sunucuda bulunamadı, paketi yeniden yükleyin"
    return {"action": "update_agent", "download_url": url, "hash": file_hash}, None


@router.post("/api/update_agent/{hw_id}")
async def update_single_agent(hw_id: str, auth: dict = Depends(require_admin)):
    # İstek gövdesi okunmaz: indirme adresi dışarıdan kabul edilmez
    msg, error = await get_update_command()
    if not msg:
        return {"status": "error", "message": error}

    if hw_id in manager.active_agents:
        await manager.send_command(msg, hw_id)
        return {"status": "success"}
    return {"status": "error", "message": "Offline"}


@router.get("/api/broadcast_update")
async def broadcast_update(auth: dict = Depends(require_admin)):
    msg, error = await get_update_command()
    if not msg:
        return {"status": "error", "message": error}

    for pc_name in list(manager.active_agents.keys()):
        await manager.send_command(msg, pc_name)
    return {"status": "success"}


@router.get("/api/agent_versions")
async def get_agent_versions(auth: dict = Depends(require_auth)):
    rows = await execute_query(
        "SELECT av.pc_name, av.version, av.last_update, c.status, c.hostname "
        "FROM agent_versions av LEFT JOIN clients c ON av.pc_name = c.pc_name",
        fetch=True,
    )
    return rows if rows else []


@router.get("/api/updates")
async def list_updates(request: Request, auth: dict = Depends(require_auth)):
    updates = []
    if os.path.exists(UPDATES_DIR):
        for f in os.listdir(UPDATES_DIR):
            if f.endswith('.zip'):
                updates.append(
                    {
                        "filename": f,
                        "size_mb": round(os.path.getsize(os.path.join(UPDATES_DIR, f)) / (1024 * 1024), 2),
                        "uploaded_at": datetime.datetime.fromtimestamp(
                            os.path.getmtime(os.path.join(UPDATES_DIR, f))
                        ).strftime("%Y-%m-%d %H:%M:%S"),
                        "url": f"{request.base_url}updates/{f}",
                    }
                )
    return sorted(updates, key=lambda x: x["uploaded_at"], reverse=True)


@router.delete("/api/updates/{filename}")
async def delete_update(filename: str, auth: dict = Depends(require_admin)):
    # Yalnızca UPDATES_DIR'in doğrudan içindeki bir .zip silinebilir (path traversal engeli)
    updates_root = os.path.realpath(UPDATES_DIR)
    file_path = os.path.realpath(os.path.join(updates_root, filename))
    if os.path.dirname(file_path) != updates_root or not file_path.endswith(".zip"):
        raise HTTPException(status_code=400, detail="Geçersiz dosya adı")
    if os.path.isfile(file_path):
        os.remove(file_path)
        return {"status": "success"}
    return {"status": "error"}
