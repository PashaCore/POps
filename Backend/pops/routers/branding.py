"""Kurum kimliği: giriş ekranında ve panelde görünen kurum adı ve logosu.

Okuma uçları oturumsuzdur (giriş ekranı oturum açılmadan gösterilir); değiştirmek yalnızca süper adminindir. Logo
veritabanında (global_settings, base64) durur: yedekle birlikte gelir, dosya izni gerektirmez. Yalnızca PNG, JPEG ve
WebP kabul edilir (içeriğin ilk baytlarına bakılır); SVG betik taşıyabildiği için alınmaz.
"""

import base64
import hashlib
from typing import Optional

from fastapi import APIRouter, Depends, File, HTTPException, UploadFile
from fastapi.responses import Response
from pydantic import Field

from pops.audit import add_audit_log
from pops.db import execute_query
from pops.models import StrictInput
from pops.security import require_superadmin

router = APIRouter()

MAX_LOGO_BYTES = 256 * 1024
NAME_MAX = 80
_KEYS = ("branding_org_name", "branding_logo", "branding_logo_type")


class BrandingInput(StrictInput):
    org_name: Optional[str] = Field(default=None, max_length=NAME_MAX)


def logo_type(data: bytes) -> Optional[str]:
    """İçeriğin gerçek türü (uzantıya ya da istemcinin bildirdiğine bakılmaz); desteklenmiyorsa None."""
    if data.startswith(b"\x89PNG\r\n\x1a\n"):
        return "image/png"
    if data.startswith(b"\xff\xd8\xff"):
        return "image/jpeg"
    if len(data) >= 12 and data[:4] == b"RIFF" and data[8:12] == b"WEBP":
        return "image/webp"
    return None


async def _settings() -> dict:
    rows = await execute_query("SELECT key, value FROM global_settings WHERE key = ANY($1::text[])", (list(_KEYS),),
                               fetch=True)
    return {r["key"]: r["value"] for r in rows or []}


async def _put(key: str, value: Optional[str]) -> None:
    if value is None:
        await execute_query("DELETE FROM global_settings WHERE key = $1", (key,))
    else:
        await execute_query(
            "INSERT INTO global_settings (key, value) VALUES ($1, $2) ON CONFLICT (key) DO UPDATE SET value = $2",
            (key, value))


def _public(s: dict) -> dict:
    logo = s.get("branding_logo")
    return {
        "org_name": s.get("branding_org_name") or None,
        "logo": bool(logo),
        # Önbellek anahtarı: logo değişince adres değişir
        "logo_v": hashlib.sha256(logo.encode("ascii")).hexdigest()[:12] if logo else None,
    }


@router.get("/api/branding")
async def get_branding():
    return _public(await _settings())


@router.get("/api/branding/logo")
async def get_logo():
    s = await _settings()
    if not s.get("branding_logo"):
        raise HTTPException(status_code=404, detail="Logo yok")
    try:
        data = base64.b64decode(s["branding_logo"])
    except ValueError:
        raise HTTPException(status_code=404, detail="Logo yok")
    kind = logo_type(data)   # saklanan tür yine de denetlenir
    if not kind:
        raise HTTPException(status_code=404, detail="Logo yok")
    return Response(content=data, media_type=kind, headers={
        "Cache-Control": "public, max-age=86400",
        "X-Content-Type-Options": "nosniff",
        "Content-Security-Policy": "default-src 'none'",
    })


@router.post("/api/system/branding")
async def set_branding(data: BrandingInput, auth: dict = Depends(require_superadmin)):
    before = await _settings()
    name = " ".join((data.org_name or "").split()) or None
    await _put("branding_org_name", name)
    await add_audit_log("*", "branding", "Kurum adı değişti", {
        "before": before.get("branding_org_name"), "after": name, "by": auth.get("sub")})
    return _public(await _settings())


@router.post("/api/system/branding/logo")
async def set_logo(file: UploadFile = File(...), auth: dict = Depends(require_superadmin)):
    data = await file.read(MAX_LOGO_BYTES + 1)
    if len(data) > MAX_LOGO_BYTES:
        raise HTTPException(status_code=413, detail="Logo en çok 256 KB olabilir.")
    kind = logo_type(data)
    if not kind:
        raise HTTPException(status_code=415, detail="Logo PNG, JPEG ya da WebP olmalı.")
    await _put("branding_logo", base64.b64encode(data).decode("ascii"))
    await _put("branding_logo_type", kind)
    await add_audit_log("*", "branding", "Kurum logosu değişti", {
        "type": kind, "bytes": len(data), "sha256": hashlib.sha256(data).hexdigest(), "by": auth.get("sub")})
    return _public(await _settings())


@router.delete("/api/system/branding/logo")
async def delete_logo(auth: dict = Depends(require_superadmin)):
    await _put("branding_logo", None)
    await _put("branding_logo_type", None)
    await add_audit_log("*", "branding", "Kurum logosu kaldırıldı", {"by": auth.get("sub")})
    return _public(await _settings())
