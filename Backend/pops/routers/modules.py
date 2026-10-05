"""Modül ayarları ve kurulum profilleri (Sistem → Modüller). Bkz. pops/modules.py."""

from typing import List, Optional

from fastapi import APIRouter, Depends, HTTPException
from pydantic import BaseModel

from pops import modules, winget
from pops.audit import add_audit_log
from pops.db import execute_query
from pops.manager import manager
from pops.security import require_auth, require_superadmin

router = APIRouter()


class ModuleSettingInput(BaseModel):
    enabled: Optional[bool] = None   # None: ayarı kaldır (laboratuvarda kurum ayarına, kurumda "açık"a döner)
    lab: Optional[str] = None        # verilirse laboratuvar istisnası


class ProfileInput(BaseModel):
    profile: str                     # school | org
    reset_labs: bool = False         # laboratuvar istisnaları da silinsin mi


async def _labs() -> List[str]:
    rows = await execute_query(
        "SELECT lab_name FROM clients WHERE lab_name IS NOT NULL UNION SELECT lab_name FROM custom_labs", fetch=True
    )
    return sorted({r["lab_name"] for r in rows or [] if r["lab_name"]})


async def _profile() -> Optional[str]:
    rows = await execute_query("SELECT value FROM global_settings WHERE key = 'install_profile'", fetch=True)
    return rows[0]["value"] if rows else None


async def _snapshot(labs: List[str]) -> dict:
    out = {}
    for m in modules.MODULES:
        for lab in labs + [None]:
            out[(m.id, lab)] = await modules.enabled(m.id, lab)
    return out


async def _close_effects(before: dict, after: dict) -> dict:
    """Kapanan modülün açık işleri durur: Vision oturumları kapanır, bekleyen komut görevleri "Denied" olur. Dosya
    dağıtımı kapanınca (uzak komut açık kalsa da) bekleyen winget görevleri "Denied" olur."""
    closed = [key for key, was_on in before.items() if was_on and not after[key]]
    effects = {"vision_sessions_closed": 0, "tasks_denied": 0}
    for mid, lab in closed:
        if mid not in ("vision", "terminal", "deploy"):
            continue
        if lab is None:
            rows = await execute_query("SELECT pc_name FROM clients WHERE lab_name IS NULL", fetch=True)
        else:
            rows = await execute_query("SELECT pc_name FROM clients WHERE lab_name = $1", (lab,), fetch=True)
        pcs = [r["pc_name"] for r in rows or []]
        if not pcs:
            continue
        if mid == "vision":
            for pc in pcs:
                if manager.vision_sessions.pop(pc, None):
                    effects["vision_sessions_closed"] += 1
                    await manager.send_command({"action": "stop_stream"}, pc)
        elif mid == "deploy":
            denied = await execute_query(
                "UPDATE tasks SET status = 'Denied', output = COALESCE(output, '') || '[MODÜL KAPALI]: Dosya "
                "dağıtımı modülü kapatıldı; winget kurulumu başlatılmadı.' WHERE target_pc = ANY($1::text[]) "
                "AND kind = $2 AND status IN ('Pending', 'Paused') RETURNING id",
                (pcs, winget.KIND),
                fetch=True,
            )
            effects["tasks_denied"] += len(denied or [])
        else:
            denied = await execute_query(
                "UPDATE tasks SET status = 'Denied', output = COALESCE(output, '') || '[MODÜL KAPALI]: Uzak komut "
                "modülü kapatıldı; görev çalıştırılmadı.' WHERE target_pc = ANY($1::text[]) "
                "AND status IN ('Pending', 'Paused') RETURNING id",
                (pcs,),
                fetch=True,
            )
            effects["tasks_denied"] += len(denied or [])
    return effects


@router.get("/api/modules")
async def list_modules(auth: dict = Depends(require_auth)):
    org, lab_rows = await modules._settings()
    labs = await _labs()
    profile = await _profile()
    out = []
    for m in modules.MODULES:
        overrides = {lab: val for (mid, lab), val in lab_rows.items() if mid == m.id}
        out.append({
            "id": m.id,
            "name": m.name,
            "description": m.description,
            "page": m.page,
            "depends": list(m.depends),
            "depends_any": list(m.depends_any),
            "setting": org.get(m.id),                  # None: ayar yok (açık)
            "enabled": await modules.enabled(m.id),    # kurum geneli, bağımlılıklarla
            "lab_overrides": overrides,
            "lab_enabled": {lab: await modules.enabled(m.id, lab) for lab in labs},
            "profiles": {p: vals[m.id] for p, vals in modules.PROFILES.items()},
        })
    return {
        "profile": profile,
        "profile_name": modules.PROFILE_NAMES.get(profile or "", None),
        "profile_names": modules.PROFILE_NAMES,
        "labs": labs,
        "modules": out,
    }


@router.post("/api/modules/{module_id}")
async def set_module(module_id: str, data: ModuleSettingInput, auth: dict = Depends(require_superadmin)):
    if module_id not in modules.BY_ID:
        raise HTTPException(status_code=404, detail="Böyle bir modül yok.")
    labs = await _labs()
    lab = (data.lab or "").strip() or None
    if lab is not None and lab not in labs:
        raise HTTPException(status_code=404, detail="Böyle bir laboratuvar yok.")
    before = await _snapshot(labs)
    scope_type, scope_id = ("lab", lab) if lab else ("org", "")
    if data.enabled is None:
        await execute_query(
            "DELETE FROM module_settings WHERE module_id = $1 AND scope_type = $2 AND scope_id = $3",
            (module_id, scope_type, scope_id),
        )
    else:
        await execute_query(
            "INSERT INTO module_settings (module_id, scope_type, scope_id, enabled, updated_by, updated_at) "
            "VALUES ($1, $2, $3, $4, $5, NOW()) ON CONFLICT (module_id, scope_type, scope_id) "
            "DO UPDATE SET enabled = $4, updated_by = $5, updated_at = NOW()",
            (module_id, scope_type, scope_id, data.enabled, auth.get("sub")),
        )
    if not lab:
        # Kurum ayarını elle değiştirmek kurulumu "özel" yapar
        await execute_query(
            "INSERT INTO global_settings (key, value) VALUES ('install_profile', 'custom') "
            "ON CONFLICT (key) DO UPDATE SET value = 'custom'"
        )
    modules.invalidate()
    effects = await _close_effects(before, await _snapshot(labs))
    state = "açıldı" if data.enabled else ("kapatıldı" if data.enabled is False else "varsayılana döndü")
    await add_audit_log(
        "*",
        "module_setting",
        "Modül %s: %s%s" % (state, modules.BY_ID[module_id].name, " (%s)" % lab if lab else ""),
        {"module": module_id, "lab": lab, "enabled": data.enabled, "by": auth.get("sub"), **effects},
    )
    return {"status": "success", **effects}


def _profile_values(name: str) -> dict:
    if name not in modules.PROFILES:
        raise HTTPException(status_code=400, detail="Profil 'school' ya da 'org' olmalı.")
    return modules.PROFILES[name]


@router.get("/api/system/install-profile/{name}")
async def preview_profile(name: str, auth: dict = Depends(require_superadmin)):
    """Profil uygulanırsa kurum genelinde değişecek modüller (laboratuvar istisnaları ayrıca sayılır)."""
    values = _profile_values(name)
    org, lab_rows = await modules._settings()
    changes = [
        {"id": m.id, "name": m.name, "from": org.get(m.id, True), "to": values[m.id]}
        for m in modules.MODULES
        if org.get(m.id, True) != values[m.id]
    ]
    return {"profile": name, "changes": changes, "lab_overrides": len(lab_rows)}


@router.post("/api/system/install-profile")
async def apply_profile(data: ProfileInput, auth: dict = Depends(require_superadmin)):
    values = _profile_values(data.profile)
    labs = await _labs()
    before = await _snapshot(labs)
    for mid, on in values.items():
        await execute_query(
            "INSERT INTO module_settings (module_id, scope_type, scope_id, enabled, updated_by, updated_at) "
            "VALUES ($1, 'org', '', $2, $3, NOW()) ON CONFLICT (module_id, scope_type, scope_id) "
            "DO UPDATE SET enabled = $2, updated_by = $3, updated_at = NOW()",
            (mid, on, auth.get("sub")),
        )
    if data.reset_labs:
        await execute_query("DELETE FROM module_settings WHERE scope_type = 'lab'")
    await execute_query(
        "INSERT INTO global_settings (key, value) VALUES ('install_profile', $1) "
        "ON CONFLICT (key) DO UPDATE SET value = $1",
        (data.profile,),
    )
    modules.invalidate()
    effects = await _close_effects(before, await _snapshot(labs))
    await add_audit_log(
        "*",
        "module_profile",
        "Kurulum profili uygulandı: %s" % modules.PROFILE_NAMES[data.profile],
        {"profile": data.profile, "reset_labs": data.reset_labs, "by": auth.get("sub"), **effects},
    )
    return {"status": "success", "profile": data.profile, **effects}
