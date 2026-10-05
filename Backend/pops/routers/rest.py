"""REST adları: eski RPC biçimli uçların (/api/create_lab, /api/move_pcs ...) kaynak adlı karşılıkları.

Entegrasyonlar bunları /api/v1 altında kullanır (pops/apiversion.py /api/v1/x'i /api/x'e çevirir, bu yüzden burada
/api/... olarak tanımlıdır ve düz /api altında da yanıt verir). Eski yollar çalışmaya devam eder; dokümanda ve
OpenAPI şemasında "deprecated" işaretlidir (docs/api.md "REST names and deprecated paths").

Mantık kopyalanmaz: her REST yolu eski işleyicinin KENDİSİNİ ya da yol parametresini eski gövde modeline çeviren
ince bir sarmalayıcıyı çağırır. Eski uca bağlı modül bağımlılıkları (modules.require) alias() ile aynen taşınır.

Kapsam: bir API jetonunun kullanabildiği uçlar. Süper admin uçları (sistem, sürüm, kayıt jetonları, modüller) ve
yalnızca panel oturumuyla kullanılan uzak ekran / uzaktan girdi uçları adlarını korur. Ajanların çağırdığı uçlar
(ajan protokolü) değişmez.
"""

from fastapi import APIRouter, Depends

from pops.models import (
    DeleteLabInput,
    DeletePackageInput,
    DeviceUpdateInput,
    LabLayoutInput,
    LabRenameInput,
    LockdownInput,
    MainPcInput,
    QuarantineInput,
    RenameDeviceInput,
    RenameLabInput,
    SaveLabLayoutInput,
)
from pops.routers import agents, control, devices, helpdesk, licenses, schedules, tasks
from pops.security import require_admin

router = APIRouter()

_SOURCES = (agents, control, devices, helpdesk, licenses, schedules, tasks)

# (REST yöntemi, REST yolu, eski yöntem, eski yol): docs/api.md tablosu ve testler bununla karşılaştırılır
ALIASES = []


def _source_route(endpoint):
    for mod in _SOURCES:
        for route in mod.router.routes:
            if getattr(route, "endpoint", None) is endpoint:
                return route
    raise LookupError("eski uç bulunamadı: %s" % endpoint.__name__)


def alias(method: str, path: str, endpoint, like=None):
    """path'i endpoint'e bağlar. like: sarmalayıcının çağırdığı eski işleyici (bağımlılıkları ve yolu ondan alınır)."""
    src = _source_route(like or endpoint)
    group = src.endpoint.__module__.rsplit(".", 1)[-1]   # şemadaki etiket: eski ucun grubu (devices, tasks ...)
    router.add_api_route(path, endpoint, methods=[method], dependencies=list(src.dependencies), tags=[group])
    ALIASES.append((method, path, sorted(src.methods)[0], src.path))


# ── Sınıflar ───────────────────────────────────────────────────────────────────
# Sınıf adı yol bölümüdür ve "9/A" gibi eğik çizgi içerebilir ({lab_name:path}; istemci eğik çizgiyi olduğu gibi
# yazar, Apache kodlanmış %2F'yi varsayılan olarak reddeder). Bu yüzden sonu sabit olan yollar (/wake, /main-pc,
# /layout) genel /api/labs/{lab_name} yollarından ÖNCE tanımlanır: Starlette ilk tam eşleşmeyi seçer.
alias("POST", "/api/labs/{lab_name:path}/wake", devices.wake_lab)


async def put_lab_main_pc(lab_name: str, data: MainPcInput, auth: dict = Depends(require_admin)):
    """Sınıfın ana bilgisayarını ayarlar. Eski /api/set_main_pc'nin aksine aç/kapa yapmaz: aynı bilgisayar yeniden
    gönderilince değişmez (kaldırmak için DELETE)."""
    return await devices.put_main_pc(lab_name, data.pc_name, auth)


async def delete_lab_main_pc(lab_name: str, auth: dict = Depends(require_admin)):
    """Sınıfın ana bilgisayarını kaldırır."""
    return await devices.clear_main_pc(lab_name, auth)


alias("PUT", "/api/labs/{lab_name:path}/main-pc", put_lab_main_pc, like=devices.set_main_pc)
alias("DELETE", "/api/labs/{lab_name:path}/main-pc", delete_lab_main_pc, like=devices.set_main_pc)


async def put_lab_layout(lab_name: str, data: LabLayoutInput, auth: dict = Depends(require_admin)):
    """Sınıfın oturma planını kaydeder ({layout_json})."""
    return await devices.save_lab_layout(SaveLabLayoutInput(lab_name=lab_name, layout_json=data.layout_json), auth)


alias("PUT", "/api/labs/{lab_name:path}/layout", put_lab_layout, like=devices.save_lab_layout)

alias("GET", "/api/labs", devices.get_custom_labs)
alias("POST", "/api/labs", devices.create_lab)


async def rename_lab(lab_name: str, data: LabRenameInput, auth: dict = Depends(require_admin)):
    """Sınıfı yeniden adlandırır ({new_name}); cihazlar, oturma planı ve görev kayıtları yeni ada taşınır."""
    return await devices.rename_lab(RenameLabInput(old_name=lab_name, new_name=data.new_name), auth)


async def delete_lab(lab_name: str, auth: dict = Depends(require_admin)):
    """Sınıfı siler; cihazları atanmamışlara (Atanmamis_Cihazlar) döner."""
    return await devices.delete_lab(DeleteLabInput(lab_name=lab_name), auth)


alias("PATCH", "/api/labs/{lab_name:path}", rename_lab, like=devices.rename_lab)
alias("DELETE", "/api/labs/{lab_name:path}", delete_lab, like=devices.delete_lab)

# ── Cihazlar ──────────────────────────────────────────────────────────────────
alias("POST", "/api/devices/move", devices.move_pcs)
alias("POST", "/api/devices/wake", devices.wake_all)
alias("POST", "/api/devices/{pc_name}/wake", devices.wake_pc)


async def update_device(pc_name: str, data: DeviceUpdateInput, auth: dict = Depends(require_admin)):
    """Cihazın görünen adını değiştirir ({display_name})."""
    return await devices.rename_device(RenameDeviceInput(pc_name=pc_name, display_name=data.display_name), auth)


alias("PATCH", "/api/devices/{pc_name}", update_device, like=devices.rename_device)


async def quarantine_device(pc_name: str, data: QuarantineInput, auth: dict = Depends(require_admin)):
    """Cihazı karantinaya alır ({reason})."""
    return await control.lockdown_pc(LockdownInput(target_pc=pc_name, reason=data.reason), auth)


async def release_device(pc_name: str, data: QuarantineInput, auth: dict = Depends(require_admin)):
    """Karantinayı kaldırır ({reason}, istek gövdesinde)."""
    return await control.unlock_pc(LockdownInput(target_pc=pc_name, reason=data.reason), auth)


alias("POST", "/api/devices/{pc_name}/quarantine", quarantine_device, like=control.lockdown_pc)
alias("DELETE", "/api/devices/{pc_name}/quarantine", release_device, like=control.unlock_pc)
alias("POST", "/api/devices/{pc_name}/bypass-code", control.get_bypass_token)

# ── Ayarlar ───────────────────────────────────────────────────────────────────
alias("GET", "/api/settings/task-concurrency", tasks.get_concurrent_limit)
alias("PUT", "/api/settings/task-concurrency", tasks.set_concurrent_limit)
alias("PUT", "/api/settings/auto-enroll", devices.set_auto_enroll)
alias("PUT", "/api/agent_policies", agents.save_policies)

# ── Görevler, paketler, dosyalar ────────────────────────────────────────────────
alias("POST", "/api/tasks", tasks.deploy_orchestration)
alias("DELETE", "/api/tasks", tasks.flush_queue)
alias("POST", "/api/packages", tasks.add_package)


async def delete_package(package_id: str, auth: dict = Depends(require_admin)):
    """Paket tanımını siler."""
    return await tasks.delete_package(DeletePackageInput(id=package_id), auth)


alias("DELETE", "/api/packages/{package_id}", delete_package, like=tasks.delete_package)
alias("POST", "/api/files", tasks.upload_file)

# ── Zamanlanmış görevler, lisanslar, destek talepleri ─────────────────────────
alias("PATCH", "/api/scheduled_tasks/{task_id}", schedules.toggle_scheduled_task)
alias("PUT", "/api/licenses/{license_id}", licenses.update_license)
alias("PATCH", "/api/tickets/{ticket_id}", helpdesk.update_ticket)
