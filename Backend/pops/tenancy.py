"""Kurum birimleri (ilçe → okul) ve kapsamlı yetki (decision D-25).

Bir ilçe millî eğitim müdürlüğü tek sunucudan birçok okulu yönetebilsin diye laboratuvarlar bir birime bağlanır
(custom_labs.org_unit_id) ve kullanıcılar ile API jetonları bir kapsam taşır (org_scope):

  * org_scope NULL: kapsam yok, her şey görünür (bugünkü davranış; birim kurulmamış kurulumlarda herkes böyle);
  * org_scope [birim kimlikleri]: o birimler ve alt birimleri. Kişi yalnızca laboratuvarı bu birimlerden birine
    bağlı cihazları görür ve onlarda işlem yapar. Atanmamış cihazlar (birime bağlı olmayan laboratuvar ya da
    "Atanmamis_Cihazlar") yalnızca kapsamsız hesaplara görünür.
  * Süper admin her zaman kapsamsızdır; birimleri o kurar, laboratuvarları o bağlar, kapsamları o verir.

Bütün uçlar kapsamı buradan alır: scope_of(auth) kimlikten Scope üretir (istek boyunca kimlik sözlüğünde saklanır),
device_sql / lab_sql / task_sql sorgulara koşul ekler, check_device / check_lab doğrudan kimlikle gelen isteği
denetler (kapsam dışı = 404, var olup olmadığı belli olmasın), require_global kurum geneli ayarları kapsamlı
hesaplara kapatır (403).
"""

import time
from typing import Dict, Iterable, List, Optional, Set

from fastapi import Depends, HTTPException, status

from pops.db import execute_query
from pops.security import require_admin

UNASSIGNED_LAB = "Atanmamis_Cihazlar"

# Birim ağacı küçük bir tablodur; her istekte okunmasın diye kısa süre önbellekte tutulur, yazınca boşaltılır
_CACHE_SECONDS = 5.0
_cache = {"at": 0.0, "parent": {}, "name": {}, "labs": {}}


class Scope:
    """units: kapsamdaki birimler (alt birimler dahil); labs: o birimlere bağlı laboratuvarlar; raw: kayıttaki liste.
    units None ise kapsam yoktur (her şey)."""

    __slots__ = ("units", "labs", "raw")

    def __init__(self, units: Optional[frozenset] = None, labs: Optional[frozenset] = None,
                 raw: Optional[List[int]] = None):
        self.units, self.labs, self.raw = units, labs, raw

    @property
    def is_global(self) -> bool:
        return self.units is None

    def allows_lab(self, lab: Optional[str]) -> bool:
        return self.is_global or (lab is not None and lab in self.labs)

    def allows_unit(self, unit_id: Optional[int]) -> bool:
        return self.is_global or (unit_id is not None and unit_id in self.units)

    def lab_list(self) -> List[str]:
        return sorted(self.labs or ())

    def unit_list(self) -> List[int]:
        return sorted(self.units or ())

    def default_unit(self) -> Optional[int]:
        """Kapsamlı hesabın oluşturduğu sınıf, lisans ve talebin birimi (verilmezse): kayıttaki ilk birim."""
        if self.is_global:
            return None
        for u in self.raw or []:
            if u in self.units:
                return u
        return None


GLOBAL = Scope()


def invalidate() -> None:
    _cache["at"] = 0.0


async def _load():
    if time.monotonic() - _cache["at"] > _CACHE_SECONDS:
        units = await execute_query("SELECT id, parent_id, name FROM org_units", fetch=True)
        labs = await execute_query(
            "SELECT lab_name, org_unit_id FROM custom_labs WHERE org_unit_id IS NOT NULL", fetch=True
        )
        by_unit: Dict[int, Set[str]] = {}
        for r in labs or []:
            by_unit.setdefault(r["org_unit_id"], set()).add(r["lab_name"])
        _cache.update(
            at=time.monotonic(),
            parent={r["id"]: r["parent_id"] for r in units or []},
            name={r["id"]: r["name"] for r in units or []},
            labs=by_unit,
        )
    return _cache


def expand(parent: Dict[int, Optional[int]], ids: Iterable[int]) -> Set[int]:
    """Birimler ve bütün alt birimleri (yalnızca var olanlar)."""
    children: Dict[int, List[int]] = {}
    for uid, pid in parent.items():
        if pid is not None:
            children.setdefault(pid, []).append(uid)
    out: Set[int] = set()
    stack = [i for i in ids if i in parent]
    while stack:
        uid = stack.pop()
        if uid in out:
            continue
        out.add(uid)
        stack.extend(children.get(uid, ()))
    return out


async def scope_for(raw: Optional[Iterable[int]]) -> Scope:
    """Kayıttaki kapsam listesinden Scope (None = kapsamsız). Zamanlanmış görevlerin kapsamı da böyle açılır."""
    if raw is None:
        return GLOBAL
    raw = [int(u) for u in raw]
    tree = await _load()
    units = expand(tree["parent"], raw)
    labs = set()
    for u in units:
        labs |= tree["labs"].get(u, set())
    return Scope(frozenset(units), frozenset(labs), raw)


async def scope_of(auth: Optional[dict]) -> Scope:
    """İsteği yapanın kapsamı. Süper admin (panel oturumu) her zaman kapsamsızdır."""
    if not auth:
        return GLOBAL
    cached = auth.get("_scope")
    if isinstance(cached, Scope):
        return cached
    if auth.get("role") == "superadmin" and not auth.get("api_token_id"):
        scope = GLOBAL
    else:
        scope = await scope_for(auth.get("org_scope"))
    auth["_scope"] = scope
    return scope


async def unit_names(ids: Optional[Iterable[int]]) -> List[dict]:
    """[{id, name}] (kayıttaki sırayla; silinmiş birim atlanır)."""
    if ids is None:
        return []
    tree = await _load()
    return [{"id": int(u), "name": tree["name"][int(u)]} for u in ids if int(u) in tree["name"]]


# ── SQL koşulları ──────────────────────────────────────────────────────────────
# Kapsamsızda "TRUE" döner ve parametre eklenmez; kapsamlıda laboratuvar listesi args'a eklenir.

def lab_sql(scope: Scope, column: str, args: list) -> str:
    if scope.is_global:
        return "TRUE"
    args.append(scope.lab_list())
    return "%s = ANY($%d::text[])" % (column, len(args))


def device_sql(scope: Scope, column: str, args: list) -> str:
    """column bir cihaz kimliği (pc_name / hw_id / target_pc): cihazın laboratuvarı kapsamda mı."""
    if scope.is_global:
        return "TRUE"
    args.append(scope.lab_list())
    return "%s IN (SELECT pc_name FROM clients WHERE lab_name = ANY($%d::text[]))" % (column, len(args))


def task_sql(scope: Scope, alias: str, args: list) -> str:
    """Görev, cihazının ŞU ANKİ laboratuvarına göre görünür; cihaz silinmişse görevin kayıtlı laboratuvarına göre."""
    if scope.is_global:
        return "TRUE"
    args.append(scope.lab_list())
    return (
        "COALESCE((SELECT c.lab_name FROM clients c WHERE c.pc_name = %(a)s.target_pc), %(a)s.target_lab) "
        "= ANY($%(n)d::text[])" % {"a": alias, "n": len(args)}
    )


def unit_sql(scope: Scope, column: str, args: list) -> str:
    """column bir birim kimliği (lisans, talep): kapsamdaki birimlerden biri mi (NULL yalnızca kapsamsıza)."""
    if scope.is_global:
        return "TRUE"
    args.append(scope.unit_list())
    return "%s = ANY($%d::int[])" % (column, len(args))


def owned_scope_sql(scope: Scope, column: str, args: list) -> str:
    """column bir kapsam listesi (zamanlanmış görev): bütün birimleri kapsamda mı (NULL yalnızca kapsamsıza)."""
    if scope.is_global:
        return "TRUE"
    args.append(scope.unit_list())
    return "(%s IS NOT NULL AND %s <@ $%d::int[])" % (column, column, len(args))


# ── Doğrudan kimlikle gelen istekler ──────────────────────────────────────────

def device_not_found() -> HTTPException:
    return HTTPException(status_code=404, detail="Cihaz bulunamadı.")


def lab_not_found() -> HTTPException:
    return HTTPException(status_code=404, detail="Sınıf bulunamadı.")


async def lab_of(pc_name: str) -> Optional[str]:
    rows = await execute_query("SELECT lab_name FROM clients WHERE pc_name = $1", (pc_name,), fetch=True)
    return rows[0]["lab_name"] if rows else None


async def check_device(auth: dict, pc_name: str) -> None:
    """Kapsamlı hesap için cihaz kapsamda değilse (ya da yoksa) 404. Kapsamsızda bugünkü davranış (denetim yok)."""
    scope = await scope_of(auth)
    if scope.is_global:
        return
    if not scope.allows_lab(await lab_of(pc_name)):
        raise device_not_found()


async def check_devices(auth: dict, pc_names: Iterable[str]) -> None:
    scope = await scope_of(auth)
    if scope.is_global:
        return
    names = list(dict.fromkeys(str(p) for p in pc_names))
    if await visible_pcs(auth, names) != names:
        raise device_not_found()


async def check_lab(auth: dict, lab_name: Optional[str]) -> None:
    if not (await scope_of(auth)).allows_lab(lab_name):
        raise lab_not_found()


async def visible_pcs(auth: dict, pc_names: Iterable[str]) -> List[str]:
    """Verilen cihazlardan kapsamdakiler (sıra korunur)."""
    names = list(dict.fromkeys(str(p) for p in pc_names))
    scope = await scope_of(auth)
    if scope.is_global or not names:
        return names
    rows = await execute_query(
        "SELECT pc_name FROM clients WHERE pc_name = ANY($1::text[]) AND lab_name = ANY($2::text[])",
        (names, scope.lab_list()),
        fetch=True,
    )
    ok = {r["pc_name"] for r in rows or []}
    return [n for n in names if n in ok]


async def clean_scope(org_scope, role: Optional[str] = None):
    """Kurum birimi kapsamı: None (her şey) ya da var olan birimlerin listesi. Süper admin her zaman kapsamsızdır."""
    if org_scope is None:
        return None
    if role == 'superadmin':
        raise HTTPException(status_code=400, detail="Süper admin bütün kurumu yönetir; kapsam verilemez.")
    ids = list(dict.fromkeys(int(i) for i in org_scope))
    if not ids:
        raise HTTPException(status_code=400, detail="En az bir birim seçin ya da kapsamı kaldırın.")
    rows = await execute_query("SELECT id FROM org_units WHERE id = ANY($1::int[])", (ids,), fetch=True)
    missing = set(ids) - {r["id"] for r in rows or []}
    if missing:
        raise HTTPException(status_code=400, detail="Bilinmeyen birim: %s" % ", ".join(str(i) for i in sorted(missing)))
    return ids


GLOBAL_ONLY = "Bu işlem bütün kurumu etkiler; kapsamı birimlerle sınırlı hesaplar yapamaz."


async def require_global(auth: dict) -> None:
    if not (await scope_of(auth)).is_global:
        raise HTTPException(status_code=status.HTTP_403_FORBIDDEN, detail=GLOBAL_ONLY)


async def require_global_admin(auth: dict = Depends(require_admin)) -> dict:
    """Kurum geneli ayarlar (ajan politikası, kuyruk sınırı, oto-kayıt, paket kitaplığı): admin ve kapsamsız."""
    await require_global(auth)
    return auth


async def principal_info(auth: dict) -> dict:
    """Panel için: kapsam (birim adları) ya da None."""
    scope = await scope_of(auth)
    if scope.is_global:
        return {"org_scope": None, "org_units": []}
    return {"org_scope": scope.raw, "org_units": await unit_names(scope.raw)}
