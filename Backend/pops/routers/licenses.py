"""Lisans takibi: tanımlı lisanslar yazılım envanteriyle karşılaştırılır (kurulu cihaz sayısı / koltuk,
süre bitişi). Okuma require_auth, tanım ekleme/değiştirme/silme require_admin. Aşım ve yaklaşan bitiş
zamanlayıcı tarafından günde bir kez bildirilir (pops.scheduler)."""

import datetime

from fastapi import APIRouter, Depends, HTTPException

from pops.audit import add_audit_log
from pops import modules
from pops.db import execute_query
from pops.models import LicenseInput
from pops.security import require_admin, require_auth

router = APIRouter()

LICENSE_TYPES = ("per_device", "site", "subscription")
EXPIRY_WARN_DAYS = 30

# Eşleşen kurulumlar: yazılım adında desen (ve varsa yayıncıda süzgeç) geçen, DISTINCT cihaz
_USAGE_SQL = (
    "SELECT l.*, (SELECT count(DISTINCT s.pc_name) FROM device_software s "
    "WHERE s.name ILIKE '%' || l.match_pattern || '%' "
    "AND (l.publisher IS NULL OR l.publisher = '' OR coalesce(s.publisher, '') ILIKE '%' || l.publisher || '%')) "
    "AS installed FROM licenses l"
)


def license_state(row: dict, today: datetime.date = None) -> dict:
    """Bir lisans satırına durum ekler: ok | over | expiring | expired (öncelik: süresi dolmuş > aşım > yaklaşan)."""
    today = today or datetime.date.today()
    r = dict(row)
    seats, installed, exp = r.get("seats"), int(r.get("installed") or 0), r.get("expires_at")
    r["installed"] = installed
    r["free"] = None if seats is None else seats - installed
    if exp and exp < today:
        r["state"] = "expired"
    elif seats is not None and installed > seats:
        r["state"] = "over"
    elif exp and (exp - today).days <= EXPIRY_WARN_DAYS:
        r["state"] = "expiring"
    else:
        r["state"] = "ok"
    for k in ("expires_at", "created_at"):
        if r.get(k):
            r[k] = r[k].isoformat()
    return r


async def licenses_with_usage() -> list:
    rows = await execute_query(_USAGE_SQL + " ORDER BY l.name", fetch=True)
    return [license_state(r) for r in rows or []]


def _validated(data: LicenseInput) -> tuple:
    name = (data.name or "").strip()
    pattern = (data.match_pattern or "").strip()
    if not name or len(name) > 200:
        raise HTTPException(status_code=400, detail="Lisans adı 1-200 karakter olmalı.")
    if len(pattern) < 2 or len(pattern) > 200:
        raise HTTPException(status_code=400, detail="Eşleşme ifadesi 2-200 karakter olmalı.")
    if any(ch in pattern for ch in "%_\\"):
        raise HTTPException(status_code=400, detail="Eşleşme ifadesinde %, _ ve \\ kullanılamaz; düz metin yazın.")
    if data.seats is not None and not (0 <= data.seats <= 1000000):
        raise HTTPException(status_code=400, detail="Koltuk sayısı 0-1000000 olmalı (boş = sınırsız).")
    if data.license_type not in LICENSE_TYPES:
        raise HTTPException(status_code=400, detail="Lisans türü: per_device | site | subscription")
    exp = None
    if data.expires_at:
        try:
            exp = datetime.date.fromisoformat(data.expires_at.strip())
        except ValueError:
            raise HTTPException(status_code=400, detail="Bitiş tarihi YYYY-AA-GG olmalı.")
    publisher = (data.publisher or "").strip()[:200] or None
    if publisher and any(ch in publisher for ch in "%_\\"):
        raise HTTPException(status_code=400, detail="Yayıncı süzgecinde %, _ ve \\ kullanılamaz.")
    notes = (data.notes or "").strip()[:2000] or None
    return name, pattern, publisher, data.seats, data.license_type, exp, notes


@router.get("/api/licenses", dependencies=[modules.require("licenses")])
async def list_licenses(auth: dict = Depends(require_auth)):
    items = await licenses_with_usage()
    summary = {s: sum(1 for i in items if i["state"] == s) for s in ("ok", "over", "expiring", "expired")}
    return {"items": items, "summary": summary}


@router.get("/api/licenses/{license_id}/devices", dependencies=[modules.require("licenses")])
async def license_devices(license_id: int, auth: dict = Depends(require_auth)):
    lic = await execute_query("SELECT match_pattern, publisher FROM licenses WHERE id = $1", (license_id,), fetch=True)
    if not lic:
        raise HTTPException(status_code=404, detail="Lisans bulunamadı.")
    rows = await execute_query(
        "SELECT s.pc_name, s.name, s.version, c.hostname, c.display_name, c.lab_name, c.status "
        "FROM device_software s LEFT JOIN clients c ON c.pc_name = s.pc_name "
        "WHERE s.name ILIKE '%' || $1 || '%' AND ($2::text IS NULL OR $2 = '' OR "
        "coalesce(s.publisher, '') ILIKE '%' || $2 || '%') ORDER BY c.lab_name NULLS LAST, c.hostname, s.name",
        (lic[0]["match_pattern"], lic[0]["publisher"]),
        fetch=True,
    )
    return [dict(r) for r in rows or []]


@router.post("/api/licenses", dependencies=[modules.require("licenses")])
async def create_license(data: LicenseInput, auth: dict = Depends(require_admin)):
    name, pattern, publisher, seats, ltype, exp, notes = _validated(data)
    rows = await execute_query(
        "INSERT INTO licenses (name, match_pattern, publisher, seats, license_type, expires_at, notes, created_by) "
        "VALUES ($1,$2,$3,$4,$5,$6,$7,$8) RETURNING id",
        (name, pattern, publisher, seats, ltype, exp, notes, auth.get("sub")),
        fetch=True,
    )
    await add_audit_log(
        "*",
        "license_create",
        "Lisans eklendi: %s" % name,
        {"id": rows[0]["id"], "by": auth.get("sub"), "seats": seats, "pattern": pattern},
    )
    return {"ok": True, "id": rows[0]["id"]}


@router.post("/api/licenses/{license_id}", dependencies=[modules.require("licenses")])
async def update_license(license_id: int, data: LicenseInput, auth: dict = Depends(require_admin)):
    name, pattern, publisher, seats, ltype, exp, notes = _validated(data)
    rows = await execute_query(
        "UPDATE licenses SET name=$1, match_pattern=$2, publisher=$3, seats=$4, license_type=$5, expires_at=$6, "
        "notes=$7 WHERE id=$8 RETURNING id",
        (name, pattern, publisher, seats, ltype, exp, notes, license_id),
        fetch=True,
    )
    if not rows:
        raise HTTPException(status_code=404, detail="Lisans bulunamadı.")
    await add_audit_log(
        "*",
        "license_update",
        "Lisans değişti: %s" % name,
        {"id": license_id, "by": auth.get("sub"), "seats": seats, "pattern": pattern},
    )
    return {"ok": True}


@router.delete("/api/licenses/{license_id}", dependencies=[modules.require("licenses")])
async def delete_license(license_id: int, auth: dict = Depends(require_admin)):
    rows = await execute_query("DELETE FROM licenses WHERE id = $1 RETURNING name", (license_id,), fetch=True)
    if not rows:
        raise HTTPException(status_code=404, detail="Lisans bulunamadı.")
    await add_audit_log(
        "*", "license_delete", "Lisans silindi: %s" % rows[0]["name"], {"id": license_id, "by": auth.get("sub")}
    )
    return {"ok": True}
