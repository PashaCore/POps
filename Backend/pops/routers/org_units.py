"""Kurum birimleri (ilçe → okul): ağaç, birimin sınıfları. Yönetim yalnızca süper admin'de; okuma kapsamla süzülür.

Birim ve kapsam kuralları pops/tenancy.py'dedir. Bir birim silinirken alt birimi varsa reddedilir; sınıfları,
lisansları ve talepleri birimsiz kalır, kullanıcı/jeton/takvim kapsamlarından çıkarılır (kapsamı boşalan hesap hiçbir
şey görmez). Her değişiklik hash-zincirli denetim kaydına yazılır.
"""

import asyncpg
from fastapi import APIRouter, Depends, HTTPException

from pops import db, tenancy, timeutil
from pops.audit import add_audit_log
from pops.db import execute_query
from pops.models import OrgUnitInput, OrgUnitLabsInput, OrgUnitUpdateInput
from pops.security import require_auth, require_superadmin

router = APIRouter()

_DUPLICATE = "Aynı üst birimin altında bu adla bir birim zaten var."


def _not_found() -> HTTPException:
    return HTTPException(status_code=404, detail="Birim bulunamadı.")


async def _all_labs() -> list:
    """Bilinen bütün sınıflar (tanımlı ya da yalnızca cihazlarda geçen) ve birimleri; atanmamışlar sınıf sayılmaz."""
    rows = await execute_query(
        "SELECT n.lab_name, cl.org_unit_id, (SELECT count(*) FROM clients c WHERE c.lab_name = n.lab_name) AS devices "
        "FROM (SELECT lab_name FROM custom_labs UNION SELECT lab_name FROM clients WHERE lab_name IS NOT NULL) n "
        "LEFT JOIN custom_labs cl ON cl.lab_name = n.lab_name WHERE n.lab_name <> $1 ORDER BY n.lab_name",
        (tenancy.UNASSIGNED_LAB,),
        fetch=True,
    )
    return [dict(r) for r in rows or []]


@router.get("/api/org-units")
async def list_org_units(auth: dict = Depends(require_auth)):
    """Birimler (id, ad, üst birim, sınıflar, kapsamında olan kullanıcı sayısı) ve sınıflar. Kapsamlı hesap yalnızca
    kendi birimlerini ve sınıflarını görür; scope isteği yapanın kapsamıdır (null = kapsamsız)."""
    scope = await tenancy.scope_of(auth)
    units = await execute_query("SELECT id, name, parent_id, created_at FROM org_units ORDER BY lower(name), id",
                                fetch=True)
    labs = [lab for lab in await _all_labs() if scope.allows_lab(lab["lab_name"])]
    by_unit = {}
    for lab in labs:
        if lab["org_unit_id"] is not None:
            by_unit.setdefault(lab["org_unit_id"], []).append(lab["lab_name"])
    users = await execute_query("SELECT org_scope FROM users WHERE org_scope IS NOT NULL", fetch=True)
    out = []
    for u in units or []:
        if not scope.allows_unit(u["id"]):
            continue
        row = timeutil.iso_row(u)
        if not scope.allows_unit(u["parent_id"]):
            row["parent_id"] = None   # kapsamın en üst birimi (üstü görünmez)
        row["labs"] = by_unit.get(u["id"], [])
        row["users"] = sum(1 for r in users or [] if u["id"] in (r["org_scope"] or []))
        out.append(row)
    return {"units": out, "labs": labs, **(await tenancy.principal_info(auth))}


@router.post("/api/org-units")
async def create_org_unit(data: OrgUnitInput, auth: dict = Depends(require_superadmin)):
    name = data.name.strip()
    if not name:
        raise HTTPException(status_code=400, detail="Birim adı boş olamaz.")
    if data.parent_id is not None and not await execute_query(
        "SELECT 1 FROM org_units WHERE id = $1", (data.parent_id,), fetch=True
    ):
        raise HTTPException(status_code=404, detail="Üst birim bulunamadı.")
    try:
        rows = await execute_query(
            "INSERT INTO org_units (name, parent_id) VALUES ($1, $2) RETURNING id, name, parent_id, created_at",
            (name, data.parent_id), fetch=True,
        )
    except asyncpg.UniqueViolationError:
        raise HTTPException(status_code=409, detail=_DUPLICATE)
    tenancy.invalidate()
    row = rows[0]
    await add_audit_log("*", "org_unit_create", "Kurum birimi eklendi: %s" % name,
                        {"id": row["id"], "name": name, "parent_id": data.parent_id, "by": auth.get("sub")})
    return {"status": "success", **timeutil.iso_row(row), "labs": [], "users": 0}


@router.patch("/api/org-units/{unit_id}")
async def update_org_unit(unit_id: int, data: OrgUnitUpdateInput, auth: dict = Depends(require_superadmin)):
    """Ad ve/veya üst birim. Birim kendi altına taşınamaz (döngü)."""
    rows = await execute_query("SELECT name, parent_id FROM org_units WHERE id = $1", (unit_id,), fetch=True)
    if not rows:
        raise _not_found()
    name = (data.name or "").strip() or rows[0]["name"]
    parent = data.parent_id if "parent_id" in data.model_fields_set else rows[0]["parent_id"]
    if parent is not None:
        tree = await execute_query("SELECT id, parent_id FROM org_units", fetch=True)
        parents = {r["id"]: r["parent_id"] for r in tree or []}
        if parent not in parents:
            raise HTTPException(status_code=404, detail="Üst birim bulunamadı.")
        if parent in tenancy.expand(parents, [unit_id]):
            raise HTTPException(status_code=400, detail="Birim kendi altındaki bir birime taşınamaz.")
    try:
        await execute_query("UPDATE org_units SET name = $1, parent_id = $2 WHERE id = $3", (name, parent, unit_id))
    except asyncpg.UniqueViolationError:
        raise HTTPException(status_code=409, detail=_DUPLICATE)
    tenancy.invalidate()
    await add_audit_log("*", "org_unit_update", "Kurum birimi değişti: %s" % name,
                        {"id": unit_id, "name": name, "parent_id": parent, "by": auth.get("sub")})
    return {"status": "success", "id": unit_id, "name": name, "parent_id": parent}


@router.delete("/api/org-units/{unit_id}")
async def delete_org_unit(unit_id: int, auth: dict = Depends(require_superadmin)):
    rows = await execute_query("SELECT name FROM org_units WHERE id = $1", (unit_id,), fetch=True)
    if not rows:
        raise _not_found()
    if await execute_query("SELECT 1 FROM org_units WHERE parent_id = $1 LIMIT 1", (unit_id,), fetch=True):
        raise HTTPException(status_code=409, detail="Önce alt birimleri silin ya da başka bir birime taşıyın.")
    async with db.transaction() as conn:
        labs = await conn.fetch("SELECT lab_name FROM custom_labs WHERE org_unit_id = $1", unit_id)
        for table in ("users", "api_tokens", "scheduled_tasks"):
            await conn.execute(
                f"UPDATE {table} SET org_scope = array_remove(org_scope, $1) WHERE $1 = ANY(org_scope)", unit_id
            )
        await conn.execute("DELETE FROM org_units WHERE id = $1", unit_id)
    tenancy.invalidate()
    await add_audit_log("*", "org_unit_delete", "Kurum birimi silindi: %s" % rows[0]["name"],
                        {"id": unit_id, "labs": [r["lab_name"] for r in labs], "by": auth.get("sub")})
    return {"status": "success"}


@router.put("/api/org-units/{unit_id}/labs")
async def set_org_unit_labs(unit_id: int, data: OrgUnitLabsInput, auth: dict = Depends(require_superadmin)):
    """Birimin sınıflarının TAMAMI: listedekiler bu birime bağlanır (başka birimdeyse oradan alınır), listede
    olmayan eski sınıfları birimsiz kalır. Yalnızca cihazlarda geçen sınıf da tanımlı sınıf olarak eklenir."""
    rows = await execute_query("SELECT name FROM org_units WHERE id = $1", (unit_id,), fetch=True)
    if not rows:
        raise _not_found()
    labs = list(dict.fromkeys(lab.strip() for lab in data.labs if lab and lab.strip()))
    if tenancy.UNASSIGNED_LAB in labs:
        raise HTTPException(status_code=400, detail="Atanmamış cihazlar bir birime bağlanamaz.")
    known = {r["lab_name"] for r in await _all_labs()}
    unknown = [lab for lab in labs if lab not in known]
    if unknown:
        raise HTTPException(status_code=404, detail="Sınıf bulunamadı: %s" % ", ".join(unknown[:5]))
    async with db.transaction() as conn:
        before = [r["lab_name"] for r in await conn.fetch(
            "SELECT lab_name FROM custom_labs WHERE org_unit_id = $1", unit_id)]
        await conn.execute(
            "UPDATE custom_labs SET org_unit_id = NULL WHERE org_unit_id = $1 AND NOT (lab_name = ANY($2::text[]))",
            unit_id, labs,
        )
        await conn.execute(
            "INSERT INTO custom_labs (lab_name, org_unit_id) SELECT unnest($2::text[]), $1 "
            "ON CONFLICT (lab_name) DO UPDATE SET org_unit_id = EXCLUDED.org_unit_id",
            unit_id, labs,
        )
    tenancy.invalidate()
    await add_audit_log("*", "org_unit_labs", "Kurum biriminin sınıfları değişti: %s" % rows[0]["name"],
                        {"id": unit_id, "labs": labs, "before": before, "by": auth.get("sub")})
    return {"status": "success", "id": unit_id, "labs": labs}
