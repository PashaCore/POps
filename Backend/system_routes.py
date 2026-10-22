"""Geri uyum: sistem uçları pops/routers/system/ paketine taşındı.

`import system_routes` paketin ortak modülünü (pops.routers.system.common) verir: yollar (RELEASES_DIR), sürüm okuma
(_read_version), GitHub sorguları, istek modelleri ve build_router. Uç modülleri bu adları çağrı anında okuduğu için
eski içe aktarmalar ve testler adları buradan değiştirebilir.
"""
import sys

from pops.routers.system import common

sys.modules[__name__] = common
