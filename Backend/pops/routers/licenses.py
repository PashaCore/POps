"""Lisans takibi: tanımlı lisanslar yazılım envanteriyle karşılaştırılır (kurulu cihaz sayısı / koltuk,
süre bitişi). Okuma require_auth, tanım ekleme/değiştirme/silme require_admin. Aşım ve yaklaşan bitiş
zamanlayıcı tarafından günde bir kez bildirilir (pops.scheduler).

Kurum birimleri (pops/tenancy.py): bir lisans bir birime ait olabilir (org_unit_id); kurulumları o birimin (ve alt
birimlerinin) cihazlarında sayılır ve lisansı yalnızca kapsamı o birimi içeren hesaplar görür. Birimsiz lisans kurum
genelidir: bütün cihazlarda sayılır, yalnızca kapsamsız hesaplar görür."""

import datetime

from fastapi import APIRouter, Depends, HTTPException

from pops.audit import add_audit_log
from pops import modules, tenancy, timeutil
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
    today = today or timeutil.today()
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
            r[k] = timeutil.iso(r[k])
    return r


async def _unit_installed(row: dict) -> int:
    """Birime ait lisansın kurulumları: yalnızca o birimin (alt birimler dahil) cihazlarında."""
    args = [row["match_pattern"], row["publisher"]]
    cond = tenancy.device_sql(await tenancy.scope_for([row["org_unit_id"]]), "s.pc_name", args)
    rows = await execute_query(
        "SELECT count(DISTINCT s.pc_name) AS n FROM device_software s WHERE s.name ILIKE '%' || $1 || '%' "
        "AND ($2::text IS NULL OR $2 = '' OR coalesce(s.publisher, '') ILIKE '%' || $2 || '%') AND " + cond,
        tuple(args),
        fetch=True,
    )
    return int(rows[0]["n"]) if rows else 0


async def licenses_with_usage(scope=None) -> list:
    """Lisanslar ve kullanım; scope verilirse yalnızca o kapsamın gördükleri."""
    args = []
    cond = tenancy.unit_sql(scope or tenancy.GLOBAL, "l.org_unit_id", args)
    rows = await execute_query(_USAGE_SQL + " WHERE " + cond + " ORDER BY l.name", tuple(args), fetch=True)
    out = []
    for r in rows or []:
        r = dict(r)
        if r.get("org_unit_id") is not None:
            r["installed"] = await _unit_installed(r)
        out.append(license_state(r))
    return out


async def _visible(license_id: int, auth: dict) -> dict:
    args = [license_id]
    cond = tenancy.unit_sql(await tenancy.scope_of(auth), "org_unit_id", args)
    rows = await execute_query(
        "SELECT name, match_pattern, publisher, org_unit_id FROM licenses WHERE id = $1 AND " + cond, tuple(args),
        fetch=True,
    )
    if not rows:
        raise HTTPException(status_code=404, detail="Lisans bulunamadı.")
    return rows[0]


async def _unit_for(data: LicenseInput, auth: dict, current=None):
    """Lisansın birimi: verilen (kapsamda olmalı), yoksa mevcut; kapsamlı hesapta hiçbiri yoksa kendi ilk birimi."""
    scope = await tenancy.scope_of(auth)
    unit = data.org_unit_id if "org_unit_id" in data.model_fields_set else current
    if unit is None and not scope.is_global:
        unit = scope.default_unit()
    if unit is not None and (not scope.allows_unit(unit) or not await execute_query(
            "SELECT 1 FROM org_units WHERE id = $1", (unit,), fetch=True)):
        raise HTTPException(status_code=404, detail="Birim bulunamadı.")
    if unit is None and not scope.is_global:
        raise HTTPException(status_code=403, detail=tenancy.GLOBAL_ONLY)
    return unit


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
    items = await licenses_with_usage(await tenancy.scope_of(auth))
    summary = {s: sum(1 for i in items if i["state"] == s) for s in ("ok", "over", "expiring", "expired")}
    return {"items": items, "summary": summary}


@router.get("/api/licenses/{license_id}/devices", dependencies=[modules.require("licenses")])
async def license_devices(license_id: int, auth: dict = Depends(require_auth)):
    lic = await _visible(license_id, auth)
    # Birime ait lisansın cihazları o birimden; kurum geneli lisans yalnızca kapsamsız hesaba görünür
    unit = lic["org_unit_id"]
    scope = await tenancy.scope_for([unit]) if unit is not None else await tenancy.scope_of(auth)
    args = [lic["match_pattern"], lic["publisher"]]
    cond = tenancy.lab_sql(scope, "c.lab_name", args)
    rows = await execute_query(
        "SELECT s.pc_name, s.name, s.version, c.hostname, c.display_name, c.lab_name, c.status "
        "FROM device_software s LEFT JOIN clients c ON c.pc_name = s.pc_name "
        "WHERE s.name ILIKE '%' || $1 || '%' AND ($2::text IS NULL OR $2 = '' OR "
        "coalesce(s.publisher, '') ILIKE '%' || $2 || '%') AND " + cond
        + " ORDER BY c.lab_name NULLS LAST, c.hostname, s.name",
        tuple(args),
        fetch=True,
    )
    return [dict(r) for r in rows or []]


@router.post("/api/licenses", dependencies=[modules.require("licenses")])
async def create_license(data: LicenseInput, auth: dict = Depends(require_admin)):
    name, pattern, publisher, seats, ltype, exp, notes = _validated(data)
    unit = await _unit_for(data, auth)
    rows = await execute_query(
        "INSERT INTO licenses (name, match_pattern, publisher, seats, license_type, expires_at, notes, created_by, "
        "org_unit_id) VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9) RETURNING id",
        (name, pattern, publisher, seats, ltype, exp, notes, auth.get("sub"), unit),
        fetch=True,
    )
    await add_audit_log(
        "*",
        "license_create",
        "Lisans eklendi: %s" % name,
        {"id": rows[0]["id"], "by": auth.get("sub"), "seats": seats, "pattern": pattern, "org_unit_id": unit},
    )
    return {"ok": True, "id": rows[0]["id"]}


@router.post("/api/licenses/{license_id}", dependencies=[modules.require("licenses")], deprecated=True)
async def update_license(license_id: int, data: LicenseInput, auth: dict = Depends(require_admin)):
    name, pattern, publisher, seats, ltype, exp, notes = _validated(data)
    current = await _visible(license_id, auth)
    unit = await _unit_for(data, auth, current["org_unit_id"])
    rows = await execute_query(
        "UPDATE licenses SET name=$1, match_pattern=$2, publisher=$3, seats=$4, license_type=$5, expires_at=$6, "
        "notes=$7, org_unit_id=$9 WHERE id=$8 RETURNING id",
        (name, pattern, publisher, seats, ltype, exp, notes, license_id, unit),
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
    await _visible(license_id, auth)
    rows = await execute_query("DELETE FROM licenses WHERE id = $1 RETURNING name", (license_id,), fetch=True)
    if not rows:
        raise HTTPException(status_code=404, detail="Lisans bulunamadı.")
    await add_audit_log(
        "*", "license_delete", "Lisans silindi: %s" % rows[0]["name"], {"id": license_id, "by": auth.get("sub")}
    )
    return {"ok": True}
