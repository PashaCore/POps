"""Entegrasyonlar (Sistem → Entegrasyonlar): GLPI'ye dışa aktarımın ayarları, bağlantı sınaması ve elle eşitleme.
Bkz. pops/glpi.py. Yalnızca superadmin; jetonlar yanıtlarda hiçbir zaman yer almaz."""

import datetime
from typing import Dict, Optional

from fastapi import APIRouter, Depends, HTTPException
from pydantic import Field, field_validator

from pops import config, glpi
from pops.audit import add_audit_log
from pops.models import StrictInput
from pops.security import require_superadmin

router = APIRouter()

# GLPI jetonları harf ve rakamdır; boşluk ve satır sonu bir HTTP başlığına giremez
TOKEN_PATTERN = r"^[!-~]*$"

# Kayıtlı jetonlar başka bir adrese kendiliğinden gönderilmez
STALE_TOKENS = "GLPI adresi değişti: kayıtlı jetonlar yeni adrese gönderilmez, jetonları yeniden girin."


class GlpiSyncInput(StrictInput):
    computers: bool = True
    software: bool = True
    tickets: bool = True
    ticket_reporter: bool = False   # talebi bildiren kişinin adı talep metnine yazılsın mı (kişisel veri)


class GlpiSettingsInput(StrictInput):
    enabled: bool = False
    url: str = Field(default="", max_length=500)
    # None: kayıtlı jeton değişmez; "": silinir
    app_token: Optional[str] = Field(default=None, max_length=200, pattern=TOKEN_PATTERN)
    user_token: Optional[str] = Field(default=None, max_length=200, pattern=TOKEN_PATTERN)
    entity: int = Field(default=0, ge=0, le=10**9)
    interval_hours: int = 24
    sync: GlpiSyncInput = GlpiSyncInput()
    tickets_since: Optional[str] = None             # YYYY-MM-DD: bu günden itibaren açılan talepler gider
    locations: Optional[Dict[str, int]] = None      # sınıf -> GLPI Location.id; None: değişmez

    @field_validator("interval_hours")
    @classmethod
    def _interval(cls, v):
        if v not in glpi.INTERVALS:
            raise ValueError("aralık 0, 6, 12 ya da 24 saat olmalı")
        return v

    @field_validator("tickets_since")
    @classmethod
    def _since(cls, v):
        if v in (None, ""):
            return None
        datetime.date.fromisoformat(v)
        return v

    @field_validator("locations")
    @classmethod
    def _locations(cls, v):
        if v is None:
            return None
        if len(v) > 5000:
            raise ValueError("en çok 5000 sınıf")
        return {str(k)[:200]: int(n) for k, n in v.items() if int(n) > 0}


class GlpiTestInput(StrictInput):
    """Formdaki değerlerle sınama (kaydetmeden); verilmeyen değer kayıtlı olandan alınır."""
    url: Optional[str] = Field(default=None, max_length=500)
    app_token: Optional[str] = Field(default=None, max_length=200, pattern=TOKEN_PATTERN)
    user_token: Optional[str] = Field(default=None, max_length=200, pattern=TOKEN_PATTERN)


async def _view() -> dict:
    s = await glpi.load()
    out = glpi.public(s)
    out.update(glpi.state())
    out["links"] = await glpi.counts()
    out["problems"] = await glpi.problems()
    out["allow_private"] = config.GLPI_ALLOW_PRIVATE
    return out


@router.get("/api/system/glpi")
async def get_glpi(auth: dict = Depends(require_superadmin)):
    return await _view()


@router.post("/api/system/glpi")
async def save_glpi(data: GlpiSettingsInput, auth: dict = Depends(require_superadmin)):
    cur = await glpi.load()
    values = data.model_dump()
    values["url"] = data.url.strip()
    if values["url"] and not values["url"].lower().startswith(("https://", "http://")):
        raise HTTPException(status_code=400, detail="GLPI adresi https:// ile başlamalı.")
    if cur["url"] and glpi.api_url(values["url"]) != glpi.api_url(cur["url"]):
        if [n for n in ("app_token", "user_token") if cur[n] and values[n] is None]:
            raise HTTPException(status_code=400, detail=STALE_TOKENS)
    user_token = cur["user_token"] if data.user_token is None else data.user_token
    if data.enabled and (not values["url"] or not user_token):
        raise HTTPException(status_code=400, detail="Eşitlemeyi açmak için GLPI adresi ve kullanıcı jetonu gerekli.")
    if values["sync"]["tickets"] and not values["tickets_since"]:
        # Talepler ilk açıldığı günden itibaren gider; eski talepler GLPI'ye dökülmesin
        values["tickets_since"] = cur.get("tickets_since") or datetime.date.today().isoformat()
    if values["locations"] is None:
        values.pop("locations")
    changed = await glpi.save(values)
    if changed:
        await add_audit_log("*", "glpi_settings", "GLPI dışa aktarım ayarları değişti",
                            {"changed": changed, "enabled": data.enabled, "by": auth.get("sub")})
    return await _view()


@router.post("/api/system/glpi/test")
async def test_glpi(data: GlpiTestInput, auth: dict = Depends(require_superadmin)):
    cur = await glpi.load()
    url = cur["url"] if data.url is None else data.url.strip()
    # Kayıtlı jetonlar yalnızca kayıtlı adrese gider: başka adres sınanırken jetonlar formdan gelmeli
    same = glpi.api_url(url) == glpi.api_url(cur["url"])
    return await glpi.test_connection(
        url,
        data.app_token if data.app_token is not None else (cur["app_token"] if same else ""),
        data.user_token if data.user_token is not None else (cur["user_token"] if same else ""),
    )


@router.post("/api/system/glpi/sync")
async def sync_glpi(wait: bool = False, auth: dict = Depends(require_superadmin)):
    """Şimdi eşitle: tur arka planda başlar (sürüyorsa yenisi başlamaz). wait=true turun sonucunu bekler (en çok 120
    sn; betikler ve testler için)."""
    s = await glpi.load()
    if not s["enabled"]:
        raise HTTPException(status_code=400, detail="GLPI'ye dışa aktarım kapalı.")
    started = glpi.start("manual")
    if started:
        await add_audit_log("*", "glpi_sync", "GLPI eşitlemesi elle başlatıldı", {"by": auth.get("sub")})
    result = await glpi.wait(120) if wait else None
    return {"started": started, "result": result, **glpi.state()}


@router.delete("/api/system/glpi/links/{pc_name}")
async def forget_glpi_link(pc_name: str, auth: dict = Depends(require_superadmin)):
    """Cihazın GLPI bağlantısını unutur (GLPI'ye dokunmaz); sonraki eşitlemede yeniden aranır ya da oluşturulur."""
    removed = await glpi.forget_device(pc_name)
    if not removed:
        raise HTTPException(status_code=404, detail="Bu cihazın GLPI bağlantısı yok.")
    await add_audit_log(pc_name, "glpi_unlink", "GLPI bağlantısı unutuldu", {"by": auth.get("sub")})
    return {"status": "success", "removed": removed}
