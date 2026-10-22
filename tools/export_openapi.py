#!/usr/bin/env python3
"""POps API'sinin OpenAPI şemasını docs/openapi.json'a yazar (ya da --check ile güncel olduğunu denetler).

Çalışan sunucu şemayı sunmaz (docs_url/redoc_url/openapi_url kapalı); entegrasyonlar şemayı depodan okur. Uygulama
içe aktarılır ama veritabanına bağlanılmaz (zorunlu ortam değişkenleri birim testlerindeki gibi sahte değerle
doldurulur). Şema entegrasyon yüzeyini anlatır: /api/... yolları /api/v1/... olarak yazılır (düz /api panel ve
ajanlar içindir, aynı uçlardır; bkz. docs/api.md). Çıktı anahtarları sıralı, girintili ve sondan satır sonludur:
aynı kod her zaman aynı dosyayı üretir.

    python tools/export_openapi.py          # docs/openapi.json'u yeniden yazar
    python tools/export_openapi.py --check  # farklıysa çıkış kodu 1 (CI 'backend' işi)

Python 3.10+, Backend/requirements.txt kurulu olmalı.
"""
import argparse
import json
import os
import sys

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), os.pardir))
BACKEND = os.path.join(ROOT, "Backend")
OUT = os.path.join(ROOT, "docs", "openapi.json")
REGEN = "python tools/export_openapi.py"

API_VERSION = "1"
DESCRIPTION = (
    "POps central server API. `/api/v1` is the stable surface for integrations; the same endpoints answer under "
    "plain `/api` for the panel and existing agents. Authenticate with an API token (`Authorization: Bearer "
    "pops_...`, created by a superadmin in Ayarlar → Güvenlik) or a panel login JWT. Paths marked deprecated still "
    "work; use their REST names. Reference: docs/api.md."
)


def build_spec() -> dict:
    for key in ("JWT_SECRET", "DB_USER", "DB_PASS", "DB_NAME"):
        os.environ.setdefault(key, "openapi-export")
    os.environ.setdefault("LOG_LEVEL", "WARNING")
    sys.path.insert(0, BACKEND)
    import server  # noqa: E402  (ortam değişkenleri yukarıda)
    from fastapi.routing import APIRoute, iter_route_contexts
    from pops.apiversion import API_V1_PREFIX

    spec = server.app.openapi()
    spec["info"] = {"title": "POps API", "version": API_VERSION, "description": DESCRIPTION}

    # Her işleme bir etiket: uç grubunun (router modülünün) adı; REST adları eski ucun grubunu alır
    # (FastAPI dahil edilen router'ları iç içe tutar; iter_route_contexts hepsini düz verir)
    tags = {}
    for ctx in iter_route_contexts(server.app.routes):
        route = ctx.original_route
        if not isinstance(route, APIRoute) or not route.include_in_schema:
            continue
        module = route.endpoint.__module__
        # Sistem paketinin (pops/routers/system/) bütün uçları tek grup
        module = "system" if module.startswith("pops.routers.system.") else module.rsplit(".", 1)[-1]
        tag = (route.tags or [None])[0] or module
        for method in route.methods or ():
            tags[(ctx.path_format, method.lower())] = tag

    paths = {}
    for path, ops in spec.get("paths", {}).items():
        for method, op in ops.items():
            op["tags"] = [tags.get((path, method), "other")]
        paths[API_V1_PREFIX + path[len("/api/"):] if path.startswith("/api/") else path] = ops
    spec["paths"] = paths
    return spec


def render(spec: dict) -> str:
    return json.dumps(spec, indent=2, sort_keys=True, ensure_ascii=False) + "\n"


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--check", action="store_true", help="dosyayı yazma; güncel değilse çıkış kodu 1")
    args = ap.parse_args()
    text = render(build_spec())
    if args.check:
        try:
            with open(OUT, encoding="utf-8") as f:
                current = f.read()
        except FileNotFoundError:
            current = None
        if current != text:
            print("docs/openapi.json is out of date (or missing). Regenerate it and commit the result:\n"
                  "    pip install -r Backend/requirements.txt\n"
                  "    %s" % REGEN)
            return 1
        print("docs/openapi.json is up to date (%d paths)." % len(json.loads(text)["paths"]))
        return 0
    with open(OUT, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)
    print("wrote %s (%d paths)" % (os.path.relpath(OUT, ROOT), len(json.loads(text)["paths"])))
    return 0


if __name__ == "__main__":
    sys.exit(main())
