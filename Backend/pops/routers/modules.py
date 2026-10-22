"""Modül ayarları ve kurulum profilleri (Sistem → Modüller). Bkz. pops/modules.py."""

from typing import List, Optional

from fastapi import APIRouter, Depends, HTTPException

from pops import exams, modules, power, tenancy, winget
from pops.audit import add_audit_log
from pops.db import execute_query
from pops.manager import manager
from pops.routers import files as file_transfer
from pops.models import StrictInput
from pops.security import require_auth, require_superadmin

router = APIRouter()


class ModuleSettingInput(StrictInput):
    enabled: Optional[bool] = None   # None: ayarı kaldır (laboratuvarda kurum ayarına, kurumda "açık"a döner)
    lab: Optional[str] = None        # verilirse laboratuvar istisnası


class ProfileInput(StrictInput):
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


async def _snapshot(labs: List[str], settings: Optional[tuple] = None) -> dict:
    """(modül, laboratuvar ya da None) -> açık mı; settings verilirse güncel ayar yerine o (önizleme)."""
    org, lab_rows = settings or await modules._settings()
    return {(m.id, lab): modules.resolve(org, lab_rows, m.id, lab) for m in modules.MODULES for lab in labs + [None]}


async def _pcs_in(lab: Optional[str]) -> List[str]:
    if lab is None:
        rows = await execute_query("SELECT pc_name FROM clients WHERE lab_name IS NULL", fetch=True)
    else:
        rows = await execute_query("SELECT pc_name FROM clients WHERE lab_name = $1", (lab,), fetch=True)
    return [r["pc_name"] for r in rows or []]


async def _deny_tasks(pcs: List[str], cond: str, params: tuple, note: str, apply: bool) -> int:
    """Bekleyen görevlerden koşula (parametresi $2) uyanları "Denied" yapar; apply=False yalnızca sayar."""
    if not apply:
        rows = await execute_query(
            "SELECT count(*) AS n FROM tasks WHERE target_pc = ANY($1::text[]) AND status IN ('Pending', 'Paused') "
            "AND " + cond, (pcs,) + params, fetch=True)
        return int(rows[0]["n"]) if rows else 0
    denied = await execute_query(
        "UPDATE tasks SET status = 'Denied', output = COALESCE(output, '') || $3::text "
        "WHERE target_pc = ANY($1::text[]) AND status IN ('Pending', 'Paused') AND " + cond + " RETURNING id",
        (pcs,) + params + (note,), fetch=True)
    return len(denied or [])


async def _close_effects(before: dict, after: dict, apply: bool = True) -> dict:
    """Kapanan modülün açık işleri durur: Vision oturumları kapanır, bekleyen komut görevleri "Denied" olur, başlamamış
    dosya aktarımlarının jetonları geçersizleşir, süren sınavlar biter. Dosya dağıtımı kapanınca (uzak komut açık
    kalsa da) bekleyen winget görevleri "Denied" olur. Güç komutu ve kullanıcıya mesaj uzak komut modülüne bağlı
    değildir. apply=False yalnızca sayar (değişiklikten önce panelde gösterilen önizleme)."""
    closed = [key for key, was_on in before.items() if was_on and not after[key]]
    effects = {"vision_sessions_closed": 0, "tasks_denied": 0, "transfers_cancelled": 0, "exams_ended": 0}
    if any(mid == "exam" for mid, _lab in closed):
        if apply:
            effects["exams_ended"] = await exams.end_where_module_off()
        else:
            rows = await execute_query("SELECT lab_name FROM exam_sessions WHERE ended_at IS NULL", fetch=True)
            effects["exams_ended"] = sum(1 for r in rows or [] if ("exam", r["lab_name"]) in closed)
    for mid, lab in closed:
        if mid not in ("vision", "terminal", "files", "deploy"):
            continue
        pcs = await _pcs_in(lab)
        if not pcs:
            continue
        if mid == "files":
            if apply:
                effects["transfers_cancelled"] += await file_transfer.cancel_open(pcs)
            else:
                rows = await execute_query(
                    "SELECT count(*) AS n FROM file_transfers WHERE pc_name = ANY($1::text[]) AND status = 'sent'",
                    (pcs,), fetch=True)
                effects["transfers_cancelled"] += int(rows[0]["n"]) if rows else 0
        elif mid == "vision":
            for pc in [pc for pc in pcs if pc in manager.vision_sessions]:
                effects["vision_sessions_closed"] += 1
                if apply and manager.drop_device_sessions(pc):
                    await manager.send_command({"action": "stop_stream"}, pc)
        elif mid == "deploy":
            effects["tasks_denied"] += await _deny_tasks(
                pcs, "kind = $2", (winget.KIND,),
                "[MODÜL KAPALI]: Dosya dağıtımı modülü kapatıldı; winget kurulumu başlatılmadı.", apply)
        else:
            # Güç komutu ve kullanıcıya mesaj komut değildir, kalır (eski ajandaki execute karşılığını kuyruk reddeder)
            effects["tasks_denied"] += await _deny_tasks(
                pcs, "(kind IS NULL OR kind <> ALL($2::text[]))", (list(power.KINDS),),
                "[MODÜL KAPALI]: Uzak komut modülü kapatıldı; görev çalıştırılmadı.", apply)
    return effects


def _state_changes(before: dict, after: dict) -> List[dict]:
    """Açık/kapalı durumu değişecek (modül, laboratuvar) çiftleri; lab None kurum genelidir."""
    return [
        {"id": mid, "name": modules.BY_ID[mid].name, "lab": lab, "from": was_on, "to": after[(mid, lab)]}
        for (mid, lab), was_on in before.items()
        if was_on != after[(mid, lab)]
    ]


@router.get("/api/modules")
async def list_modules(auth: dict = Depends(require_auth)):
    org, lab_rows = await modules._settings()
    # Kapsamlı hesap (pops/tenancy.py) yalnızca kendi laboratuvarlarının ayarını görür
    scope = await tenancy.scope_of(auth)
    labs = [lab for lab in await _labs() if scope.allows_lab(lab)]
    profile = await _profile()
    out = []
    for m in modules.MODULES:
        overrides = {lab: val for (mid, lab), val in lab_rows.items() if mid == m.id and scope.allows_lab(lab)}
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


def _lab_or_404(lab: Optional[str], labs: List[str]) -> Optional[str]:
    lab = (lab or "").strip() or None
    if lab is not None and lab not in labs:
        raise HTTPException(status_code=404, detail="Böyle bir laboratuvar yok.")
    return lab


def _with_setting(settings: tuple, module_id: str, lab: Optional[str], value: Optional[bool]) -> tuple:
    """Ayarların kopyası, bir modülün kurum ya da laboratuvar ayarı değiştirilmiş olarak (önbelleğe dokunmaz)."""
    org, lab_rows = dict(settings[0]), dict(settings[1])
    target, key = (lab_rows, (module_id, lab)) if lab else (org, module_id)
    if value is None:
        target.pop(key, None)
    else:
        target[key] = value
    return org, lab_rows


@router.get("/api/modules/{module_id}/preview")
async def preview_module(module_id: str, enabled: Optional[bool] = None, lab: Optional[str] = None,
                         auth: dict = Depends(require_superadmin)):
    """Ayar değişirse ne olur (hiçbir şey yazmaz): durumu değişecek modüller (bağımlılar dahil, kurum geneli ve
    laboratuvar bazında) ve kapanacak Vision oturumu ile reddedilecek görev sayısı. enabled yoksa ayar kaldırılır."""
    if module_id not in modules.BY_ID:
        raise HTTPException(status_code=404, detail="Böyle bir modül yok.")
    labs = await _labs()
    lab = _lab_or_404(lab, labs)
    current = await modules._settings()
    before = await _snapshot(labs, current)
    after = await _snapshot(labs, _with_setting(current, module_id, lab, enabled))
    return {"module": module_id, "lab": lab, "enabled": enabled, "changes": _state_changes(before, after),
            **await _close_effects(before, after, apply=False)}


@router.post("/api/modules/{module_id}")
async def set_module(module_id: str, data: ModuleSettingInput, auth: dict = Depends(require_superadmin)):
    if module_id not in modules.BY_ID:
        raise HTTPException(status_code=404, detail="Böyle bir modül yok.")
    labs = await _labs()
    lab = _lab_or_404(data.lab, labs)
    modules.invalidate()   # önceki durum önbellekten değil veritabanından (etkiler ve denetim kaydı buna göre)
    before = await _snapshot(labs)
    scope_type, scope_id = ("lab", lab) if lab else ("org", "")
    prev = await execute_query(
        "SELECT enabled FROM module_settings WHERE module_id = $1 AND scope_type = $2 AND scope_id = $3",
        (module_id, scope_type, scope_id),
        fetch=True,
    )
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
        {"module": module_id, "lab": lab, "enabled": data.enabled, "previous": prev[0]["enabled"] if prev else None,
         "by": auth.get("sub"), **effects},
    )
    return {"status": "success", **effects}


def _profile_values(name: str) -> dict:
    if name not in modules.PROFILES:
        raise HTTPException(status_code=400, detail="Profil 'school' ya da 'org' olmalı.")
    return modules.PROFILES[name]


@router.get("/api/system/install-profile/{name}")
async def preview_profile(name: str, reset_labs: bool = False, auth: dict = Depends(require_superadmin)):
    """Profil uygulanırsa kurum genelinde değişecek modüller (laboratuvar istisnaları ayrıca sayılır) ve kapanacak
    Vision oturumu ile reddedilecek görev sayısı (reset_labs: istisnalar da silinirse)."""
    values = _profile_values(name)
    org, lab_rows = await modules._settings()
    changes = [
        {"id": m.id, "name": m.name, "from": org.get(m.id, True), "to": values[m.id]}
        for m in modules.MODULES
        if org.get(m.id, True) != values[m.id]
    ]
    labs = await _labs()
    before = await _snapshot(labs, (org, lab_rows))
    after = await _snapshot(labs, (dict(values), {} if reset_labs else dict(lab_rows)))
    return {"profile": name, "changes": changes, "lab_overrides": len(lab_rows),
            **await _close_effects(before, after, apply=False)}


@router.post("/api/system/install-profile")
async def apply_profile(data: ProfileInput, auth: dict = Depends(require_superadmin)):
    values = _profile_values(data.profile)
    labs = await _labs()
    modules.invalidate()
    org, lab_rows = await modules._settings()
    changed = [m.id for m in modules.MODULES if org.get(m.id, True) != values[m.id]]
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
        {"profile": data.profile, "reset_labs": data.reset_labs, "changed": changed,
         "lab_overrides_deleted": len(lab_rows) if data.reset_labs else 0, "by": auth.get("sub"), **effects},
    )
    return {"status": "success", "profile": data.profile, **effects}
