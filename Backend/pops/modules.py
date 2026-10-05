"""Modüller: kurumun ihtiyacına göre açılıp kapanan özellikler (bkz. docs/design/modules.md).

Çekirdek (cihazlar, laboratuvarlar, kayıt, ajan güncelleme, denetim kaydı, kullanıcılar, bildirimler, sunucu
sağlığı) her zaman açıktır ve burada listelenmez. Modülün ayarı kurum genelinde ya da laboratuvar bazında verilir
(`module_settings`); en özel ayar kazanır, ayar yoksa modül açıktır (mevcut kurulumlar yükseltmede değişmez).
Bağımlı modül, bağımlılığı kapalıysa da kapalı sayılır (ör. lisanslar yazılım envanteri olmadan çalışmaz).

Karar sunucudadır: uçlar `check()` / `require()` ile reddeder, kuyruk ve zamanlayıcı kapalı modülün işini
başlatmaz. Panel menüyü buna göre çizer; ajan tarafı yerel yetenek kilidi (terminal, Vision) ayrıca son sözdür.
"""

import time
from dataclasses import dataclass, field
from typing import Dict, Iterable, List, Optional, Tuple

from fastapi import Depends, HTTPException

from pops.db import execute_query
from pops.security import require_auth


@dataclass(frozen=True)
class Module:
    id: str
    name: str
    description: str
    school: bool                       # "Okul laboratuvarı" profilinde açık mı
    org: bool                          # "Kurum" profilinde açık mı
    depends: Tuple[str, ...] = ()      # hepsi açık olmalı
    depends_any: Tuple[str, ...] = ()  # en az biri açık olmalı
    page: Optional[str] = None         # panelde bu modüle ait sayfa (menüde gizlenir)
    notes: Tuple[str, ...] = field(default_factory=tuple)


MODULES = (
    Module("vision", "Uzaktan ekran (Vision)", "Canlı ekran, önizleme ve onaylı uzaktan kontrol.",
           True, False, page="vision"),
    Module("terminal", "Uzak komut",
           "Terminal, hızlı işlemler ve bilgisayarda komut çalıştıran bütün görevler (dağıtım, zamanlanmış komut).",
           True, False, page="terminal"),
    Module("deploy", "Dosya dağıtımı", "Paket kitaplığı, dosya yükleme ve dağıtım zinciri.",
           True, False, depends=("terminal",), page="deploy"),
    Module("schedules", "Zamanlanmış görevler", "Bir kez, her gün ya da seçili günlerde çalışan görevler.",
           True, True, depends_any=("terminal",)),
    Module("patches", "Windows Update yönetimi", "Yama durumu, tarama ve istenince güncelleme kurma.", True, True),
    Module("software", "Yazılım envanteri", "Kurulu programların listesi.", True, True),
    Module("licenses", "Lisanslar", "Kurulumların satın alınan koltuklarla karşılaştırılması.",
           False, True, depends=("software",)),
    Module("helpdesk", "Yardım masası", "Tepsiden \"Sorun bildir\" ve panelden yanıt.", False, True,
           page="helpdesk"),
    Module("dns_policy", "DNS politikası", "Listelenen alan adlarına girişin tespiti ve eşikte karantina.",
           True, False),
    Module("quarantine", "Karantina", "Kilit ekranı ve ağ yalıtımı; kaldırma ve bypass kodu her zaman çalışır.",
           True, True),
    Module("wol", "Uyandırma (Wake-on-LAN)", "Kapalı bilgisayarları ağdan açma.", True, True),
    Module("exam", "Sınav modu",
           "Sınıfta süreli ağ kısıtlaması: yalnızca izin verilen adresler açık, tepside mesaj, isteğe bağlı program "
           "engeli.", True, False),
    Module("reports", "Raporlar", "Filo, güvenlik olayları, yazılım ve güncelleme raporları, CSV.", True, True,
           page="reports"),
)
BY_ID: Dict[str, Module] = {m.id: m for m in MODULES}

PROFILES = {
    "school": {m.id: m.school for m in MODULES},
    "org": {m.id: m.org for m in MODULES},
}
PROFILE_NAMES = {"school": "Okul laboratuvarı", "org": "Kurum", "custom": "Özel"}

# Ayarlar küçük bir tablodur; her istekte okunmasın diye kısa süre önbellekte tutulur, yazınca boşaltılır
_CACHE_SECONDS = 5.0
_cache = {"at": 0.0, "org": {}, "lab": {}}


def invalidate() -> None:
    _cache["at"] = 0.0


async def _settings():
    if time.monotonic() - _cache["at"] > _CACHE_SECONDS:
        rows = await execute_query("SELECT module_id, scope_type, scope_id, enabled FROM module_settings", fetch=True)
        org, lab = {}, {}
        for r in rows or []:
            if r["enabled"] is None:
                continue
            if r["scope_type"] == "org":
                org[r["module_id"]] = bool(r["enabled"])
            else:
                lab[(r["module_id"], r["scope_id"])] = bool(r["enabled"])
        _cache.update(at=time.monotonic(), org=org, lab=lab)
    return _cache["org"], _cache["lab"]


async def own_setting(module_id: str, lab: Optional[str] = None) -> bool:
    """Modülün kendi ayarı (bağımlılıklara bakmadan): laboratuvar istisnası > kurum ayarı > açık."""
    org, labs = await _settings()
    if lab is not None and (module_id, lab) in labs:
        return labs[(module_id, lab)]
    return org.get(module_id, True)


async def enabled(module_id: str, lab: Optional[str] = None, _seen=None) -> bool:
    mod = BY_ID.get(module_id)
    if mod is None:
        return True   # modül olmayan (çekirdek) özellik
    seen = _seen or set()
    if module_id in seen:
        return False
    seen = seen | {module_id}
    if not await own_setting(module_id, lab):
        return False
    for dep in mod.depends:
        if not await enabled(dep, lab, seen):
            return False
    if mod.depends_any:
        for dep in mod.depends_any:
            if await enabled(dep, lab, seen):
                return True
        return False
    return True


async def lab_of(pc_name: str) -> Optional[str]:
    rows = await execute_query("SELECT lab_name FROM clients WHERE pc_name = $1", (pc_name,), fetch=True)
    return rows[0]["lab_name"] if rows else None


async def enabled_anywhere(module_id: str) -> bool:
    """Kurum genelinde ya da en az bir laboratuvarda açık mı (kurum geneli sayfalar ve listeler için)."""
    if await enabled(module_id):
        return True
    _org, labs = await _settings()
    return any([await enabled(module_id, lab) for lab in {lab for (_mid, lab) in labs}])


async def labs_of(pc_names: Iterable[str]) -> Dict[str, Optional[str]]:
    names = list(dict.fromkeys(str(p) for p in pc_names))
    if not names:
        return {}
    rows = await execute_query(
        "SELECT pc_name, lab_name FROM clients WHERE pc_name = ANY($1::text[])", (names,), fetch=True
    )
    found = {r["pc_name"]: r["lab_name"] for r in rows or []}
    return {n: found.get(n) for n in names}


async def split_pcs(module_id: str, pc_names: Iterable[str]) -> Tuple[List[str], List[str]]:
    """(modülün açık olduğu cihazlar, kapalı olduğu cihazlar); her cihaz kendi laboratuvarının ayarıyla."""
    labs = await labs_of(pc_names)
    verdict: Dict[Optional[str], bool] = {}
    allowed, denied = [], []
    for pc, lab in labs.items():
        if lab not in verdict:
            verdict[lab] = await enabled(module_id, lab)
        (allowed if verdict[lab] else denied).append(pc)
    return allowed, denied


def closed_error(module_id: str) -> HTTPException:
    """409: modül kapalı. Panel mesajı detail'den okur; modül kimliği X-POps-Module başlığındadır."""
    name = BY_ID[module_id].name if module_id in BY_ID else module_id
    return HTTPException(status_code=409, detail="'%s' modülü kapalı." % name,
                         headers={"X-POps-Module": module_id, "X-POps-Module-State": "disabled"})


async def check(module_id: str, pc_name: Optional[str] = None, lab: Optional[str] = None) -> None:
    """Modül (cihazın ya da verilen laboratuvarın ayarına göre) kapalıysa 409."""
    if pc_name is not None and lab is None:
        lab = await lab_of(pc_name)
    if not await enabled(module_id, lab):
        raise closed_error(module_id)


def require(module_id: str):
    """FastAPI bağımlılığı: modül kurum genelinde ya da en az bir laboratuvarda açık değilse 409. Cihaza yönelik
    işlemler ayrıca check()/split_pcs() ile cihazın laboratuvarına göre denetlenir."""

    # Önce kimlik: oturumsuz istek modül durumunu öğrenmesin
    async def dependency(_auth: dict = Depends(require_auth)):
        if not await enabled_anywhere(module_id):
            raise closed_error(module_id)

    return Depends(dependency)
