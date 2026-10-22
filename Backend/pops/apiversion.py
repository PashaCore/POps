"""Sürümlü API yolu: /api/v1/... isteği yönlendirmeden önce /api/... olur.

/api/v1 entegrasyonlar (betikler, API jetonları) için kararlı yüzeydir; düz /api panel ve mevcut ajanlar için
olduğu gibi kalır. Her uç tek kez tanımlanır ve iki yoldan da aynı işleyiciye, aynı yetki denetimine ulaşır:
  * metrikler rota şablonuyla etiketlenir (/api/v1/devices/X/activity -> /api/devices/{pc_name}/activity);
  * istek sınırı (slowapi) işleyici fonksiyonuna göre sayılır, /api/v1 ayrı bir kota açmaz.
WebSocket yolları (/ws/...) sürümlenmez. Bkz. docs/api.md "Versioning", docs/decisions.md D-21.
"""

API_V1_PREFIX = "/api/v1/"
_RAW_V1_PREFIX = API_V1_PREFIX.encode("ascii")


def unversioned(path: str) -> str:
    """/api/v1/x -> /api/x; başka yollar olduğu gibi döner."""
    if path.startswith(API_V1_PREFIX):
        return "/api/" + path[len(API_V1_PREFIX):]
    return path


class ApiVersionMiddleware:
    """En dıştaki middleware: yolu, istek bağlamı/metrik middleware'i ve yönlendirici görmeden değiştirir. Kapsam
    (scope) kopyalanır; içerideki katmanlar (rota bilgisi dahil) bu kopyayı paylaşır."""

    def __init__(self, app):
        self.app = app

    async def __call__(self, scope, receive, send):
        if scope["type"] == "http" and scope.get("path", "").startswith(API_V1_PREFIX):
            scope = dict(scope)
            scope["path"] = unversioned(scope["path"])
            raw = scope.get("raw_path")
            if raw and raw.startswith(_RAW_V1_PREFIX):
                scope["raw_path"] = b"/api/" + raw[len(_RAW_V1_PREFIX):]
        await self.app(scope, receive, send)
