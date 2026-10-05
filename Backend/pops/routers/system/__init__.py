"""Sistem uçları (eskiden tek dosya: Backend/system_routes.py).

- version.py    — /api/health (kimliksiz; DB erişilebilirliği + çalışan sürüm), çalışan ve GitHub'daki son sürüm, sunucu
                  güncellemesi, sürüm notları
- releases.py   — imzalı release'in stage edilmesi: çevrimdışı yükleme ve GitHub'dan indirme (aynı doğrulama)
- updates.py    — staged release'in ajanlara dağıtımı ve gönderilmiş güncellemenin cihaz cihaz durumu
- identity.py   — kayıt jetonları, ajan kimlik zorlaması, yeniden kayıt izni, yetenek politikası
- selfupdate.py — sunucu backend self-update isteği ve durumu
- common.py     — yollar, sürüm okuma, GitHub sorguları, istek modelleri; eski `system_routes` modülü budur

server.py bunu build_router(...) ile kurar ve bağımlılıkları verir; böylece paket server.py'yi içe aktarmaz (döngüsel
import yok) ve testler sahte veritabanı/yönetici verebilir.
"""
from fastapi import APIRouter

from pops.routers.system import common, identity, releases, selfupdate, updates, version


def build_router(require_admin, require_superadmin, execute_query, manager, updates_dir, add_audit_log) -> APIRouter:
    d = common.Deps(require_admin, require_superadmin, execute_query, manager, updates_dir, add_audit_log)
    # Tek düz router: uçlar dahil edilmiş alt router'larda değil, doğrudan bunun rotalarında (testler de böyle okur)
    router = APIRouter()
    for module in (version, releases, identity, updates, selfupdate):
        module.register(router, d)
    return router
