"""Sınav modu uçları (bkz. pops/exams.py, docs/api.md "Exam mode").

Yollar baştan REST biçimindedir; /api/v1 altında da aynı işleyiciye ulaşır (pops/apiversion.py). Sınıf adı yol
bölümüdür ve "9/A" gibi eğik çizgi içerebilir ({lab_name:path}): bu router, genel /api/labs/{lab_name} yollarını
tanımlayan routers/rest.py'den ÖNCE bağlanır (Starlette ilk tam eşleşmeyi seçer; bkz. server._ROUTERS).
"""

from typing import Optional

from fastapi import APIRouter, Depends, HTTPException, Query

from pops import exams, modules
from pops.models import ExamEndInput, ExamStartInput
from pops.security import require_admin, require_auth

router = APIRouter()


async def _lab(lab_name: str) -> str:
    lab = (lab_name or "").strip()
    if not lab or lab == exams.UNASSIGNED:
        raise HTTPException(status_code=400, detail="Sınav modu bir sınıf için başlatılır.")
    if not await exams.lab_exists(lab):
        raise HTTPException(status_code=404, detail="Böyle bir sınıf yok.")
    return lab


@router.post("/api/labs/{lab_name:path}/exam")
async def start_exam(lab_name: str, data: ExamStartInput, auth: dict = Depends(require_admin)):
    """Sınıfta sınav modunu başlatır ve bağlı ajanlara gönderir; çevrimdışı olanlar bağlanınca alır."""
    lab = await _lab(lab_name)
    await modules.check("exam", lab=lab)
    reason = exams.clean_text(data.reason, exams.REASON_MAX)
    if not reason:
        raise HTTPException(status_code=400, detail="Gerekçe yazılmalıdır.")
    try:
        allow = exams.clean_allow(data.allow)
        block_apps = exams.clean_apps(data.block_apps)
        until = exams.resolve_until(data.until, data.duration_minutes)
    except ValueError as exc:
        raise HTTPException(status_code=400, detail=str(exc))
    message = exams.clean_text(data.message, exams.MESSAGE_MAX) or exams.DEFAULT_MESSAGE
    await exams.end_expired()   # süresi dolmuş ama henüz kapanmamış sınav yenisini engellemesin
    try:
        result = await exams.start(lab, allow, until, message, block_apps, reason, auth.get("sub"))
    except exams.ExamConflict:
        raise HTTPException(status_code=409, detail="Bu sınıfta süren bir sınav var; önce onu bitirin.")
    return {"status": "success", **result}


@router.delete("/api/labs/{lab_name:path}/exam")
async def end_exam(lab_name: str, data: Optional[ExamEndInput] = None, auth: dict = Depends(require_admin)):
    """Sınıfın süren sınavını bitirir ve bağlı ajanlara enabled:false gönderir. Gövde isteğe bağlı: {reason}."""
    lab = await _lab(lab_name)
    await exams.end_expired()
    note = exams.clean_text(data.reason if data else "", exams.REASON_MAX)
    result = await exams.end(lab, auth.get("sub"), "admin", note)
    if result is None:
        raise HTTPException(status_code=404, detail="Bu sınıfta süren sınav yok.")
    return {"status": "success", **result}


@router.get("/api/labs/{lab_name:path}/exam")
async def get_lab_exam(lab_name: str, auth: dict = Depends(require_auth)):
    """Sınıfın sınav durumu ve her bilgisayarın durumu (sınavda, ayrıldı, ulaşılamıyor, desteklemiyor ...). Bilinmeyen
    sınıfta sınav yoktur (active: false): panel yeni silinen ya da yeniden adlandırılan sınıfı sorarken 404 almasın."""
    lab = (lab_name or "").strip()
    if not lab:
        raise HTTPException(status_code=400, detail="Sınıf adı gerekli.")
    return await exams.lab_state(lab)


@router.get("/api/exams")
async def list_exams(
    lab: Optional[str] = None,
    active: Optional[bool] = None,
    limit: int = Query(50, ge=1, le=500),
    auth: dict = Depends(require_auth),
):
    """Sınav geçmişi, yeniden eskiye. active=true yalnızca sürenleri (bilgisayar durum sayılarıyla) verir."""
    await exams.end_expired()
    return {"items": await exams.history((lab or "").strip() or None, active, limit)}
