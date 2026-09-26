"""POps sistem/sürüm uçları (ayrı APIRouter, server.py şişmesin).

- GET /api/health          — kimliksiz; DB erişilebilirliği + çalışan sürüm (hassas veri yok)
- GET /api/system/version  — admin; çalışan sürüm + (varsa) GitHub'daki son sürüm

GitHub kontrolü OFFLINE-GÜVENLİDİR: kısa zaman aşımlı, event loop'u bloklamaz
(thread'de urllib), başarısız olursa `latest=None` döner ve hiçbir zaman hata fırlatmaz.
Ek bağımlılık yoktur (internetsiz okullarda pip gerektirmez). Sonuç saatte bir cache'lenir.

server.py bunu `build_router(require_admin, execute_query)` ile kurar; böylece bu modül
server.py'yi import etmez (döngüsel import yok). Python 3.9 uyumlu.
"""
import asyncio
import json
import os
import time
import urllib.request
from typing import Optional

from fastapi import APIRouter, Depends

BASE_DIR = os.path.dirname(os.path.abspath(__file__))
GITHUB_REPO = os.environ.get("POPS_GITHUB_REPO", "PashaCore/POps")
_GITHUB_TIMEOUT = 5.0
_GITHUB_TTL = 3600.0  # saniye
_latest_cache = {"at": 0.0, "tag": None, "checked": False}


def _read_version() -> str:
    """Çalışan sürüm: POPS_VERSION > (server.py dizini)/VERSION > ../VERSION > CHANGELOG > 'unknown'."""
    env = os.environ.get("POPS_VERSION")
    if env and env.strip():
        return env.strip()
    for path in (os.path.join(BASE_DIR, "VERSION"), os.path.join(BASE_DIR, os.pardir, "VERSION")):
        try:
            with open(path, "r", encoding="utf-8") as f:
                v = f.read().strip()
            if v:
                return v
        except OSError:
            continue
    # Son çare: CHANGELOG'un en üst (Unreleased olmayan) sürüm başlığı
    for path in (os.path.join(BASE_DIR, "CHANGELOG.md"), os.path.join(BASE_DIR, os.pardir, "CHANGELOG.md")):
        try:
            with open(path, "r", encoding="utf-8") as f:
                for line in f:
                    if line.startswith("## [") and not line.startswith("## [Unreleased]"):
                        return line[line.index("[") + 1:line.index("]")]
        except OSError:
            continue
    return "unknown"


def _fetch_github_latest_tag() -> Optional[str]:
    """GitHub'daki son release tag'i (bloklayan; thread'de çağrılır). Hata olursa None."""
    url = "https://api.github.com/repos/%s/releases/latest" % GITHUB_REPO
    req = urllib.request.Request(url, headers={
        "Accept": "application/vnd.github+json",
        "User-Agent": "POps-server",
    })
    try:
        with urllib.request.urlopen(req, timeout=_GITHUB_TIMEOUT) as resp:
            data = json.loads(resp.read().decode("utf-8"))
        tag = data.get("tag_name")
        return tag or None
    except Exception:
        return None


async def _github_latest(force: bool = False) -> Optional[str]:
    now = time.time()
    if not force and _latest_cache["checked"] and (now - _latest_cache["at"]) < _GITHUB_TTL:
        return _latest_cache["tag"]
    tag = await asyncio.to_thread(_fetch_github_latest_tag)
    # Sadece başarılı sonucu cache'le; offline'da eski değeri koru, çökme
    if tag is not None:
        _latest_cache["tag"] = tag
    _latest_cache["at"] = now
    _latest_cache["checked"] = True
    return _latest_cache["tag"]


def _norm(v: Optional[str]) -> Optional[str]:
    """Karşılaştırma için baştaki v/V'yi soy (agent_versions'da karışık 'v'li/'v'siz satırlar olabilir)."""
    if not v:
        return v
    return v[1:] if v[:1] in ("v", "V") else v


def build_router(require_admin, execute_query):
    router = APIRouter()

    @router.get("/api/health")
    async def health():
        try:
            await execute_query("SELECT 1", fetch=True)
            db_ok = True
        except Exception:
            db_ok = False
        return {"status": "ok" if db_ok else "degraded", "database": db_ok, "version": _read_version()}

    @router.get("/api/system/version")
    async def system_version(check: bool = False, auth: dict = Depends(require_admin)):
        running = _read_version()
        latest = await _github_latest(force=check)
        update_available = bool(latest and _norm(latest) != _norm(running))
        return {
            "running": running,
            "latest": latest,           # offline ise None olabilir
            "update_available": update_available,
            "checked_github": _latest_cache["checked"],
            "repo": GITHUB_REPO,
        }

    return router
