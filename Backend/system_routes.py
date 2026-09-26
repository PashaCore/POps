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
from typing import List, Optional

from fastapi import APIRouter, Depends, File, Form, HTTPException, UploadFile

import release_verify

BASE_DIR = os.path.dirname(os.path.abspath(__file__))
# Doğrulanmış release'lerin stage edildiği çalışma zamanı dizini (git dışı)
RELEASES_DIR = os.path.join(BASE_DIR, "releases")
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


def _pubkey_path() -> Optional[str]:
    """Release açık anahtarını $APP/keys (canlı) ya da ../keys (paket/repo) altında bulur."""
    for p in (os.path.join(BASE_DIR, "keys", "pops_release_ed25519.pub.pem"),
              os.path.join(BASE_DIR, os.pardir, "keys", "pops_release_ed25519.pub.pem")):
        if os.path.isfile(p):
            return p
    return None


def build_router(require_admin, require_superadmin, execute_query):
    router = APIRouter()

    async def _staged_release() -> Optional[dict]:
        rows = await execute_query(
            "SELECT value FROM global_settings WHERE key='verified_release_manifest'", fetch=True)
        if rows and rows[0]["value"]:
            try:
                return json.loads(rows[0]["value"])
            except Exception:
                return None
        return None

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
        staged = await _staged_release()
        staged_version = staged.get("version") if staged else None
        update_available = bool(latest and _norm(latest) != _norm(running))
        return {
            "running": running,
            "latest": latest,           # offline ise None olabilir
            "update_available": update_available,
            "checked_github": _latest_cache["checked"],
            "repo": GITHUB_REPO,
            "staged_version": staged_version,   # offline'da yüklenip doğrulanan sürüm
            "staged_tag": staged.get("tag") if staged else None,
        }

    @router.post("/api/system/upload-release")
    async def upload_release(
        files: List[UploadFile] = File(...),
        force: bool = Form(False),
        auth: dict = Depends(require_superadmin),
    ):
        """İnternetsiz kurulum yolu: imzalı bir release (manifest.json + .sig + paketler)
        yükle, ed25519 imzasını depodaki açık anahtarla doğrula, özetleri kontrol et ve
        doğrulanmışsa stage et. Uygulamak (sunucu/ajan güncelleme) sonraki fazlarda."""
        pub = _pubkey_path()
        if not pub:
            raise HTTPException(status_code=503,
                                detail="Açık anahtar bulunamadı (keys/pops_release_ed25519.pub.pem).")
        blobs = {}
        for f in files:
            blobs[os.path.basename(f.filename or "")] = await f.read()
        if "manifest.json" not in blobs or "manifest.json.sig" not in blobs:
            raise HTTPException(status_code=400,
                                detail="manifest.json ve manifest.json.sig birlikte yüklenmeli.")
        try:
            manifest = release_verify.verify_manifest(
                blobs["manifest.json"], blobs["manifest.json.sig"], pub)
        except release_verify.ReleaseVerifyError as exc:
            raise HTTPException(status_code=400, detail="İmza doğrulanamadı: %s" % exc)

        present = []
        for name, data in blobs.items():
            if name in ("manifest.json", "manifest.json.sig"):
                continue
            entry = release_verify.artifact_entry(manifest, name)
            if not entry:
                raise HTTPException(status_code=400, detail="Manifest'te olmayan dosya: %s" % name)
            if release_verify.sha256_bytes(data) != entry.get("sha256"):
                raise HTTPException(status_code=400, detail="SHA-256 uyuşmuyor: %s" % name)
            present.append(name)

        # Downgrade koruması: released_at monoton olmalı (imzalı manifest'in içinde).
        prev = await _staged_release()
        if prev and not force:
            if int(manifest.get("released_at", 0)) <= int(prev.get("released_at", 0)):
                raise HTTPException(
                    status_code=409,
                    detail="Yüklenen sürüm mevcut doğrulanmış sürümden (%s) yeni değil. force ile geçin."
                    % prev.get("version"))

        version = str(manifest["version"])
        dest = os.path.join(RELEASES_DIR, version.replace(os.sep, "_"))
        os.makedirs(dest, exist_ok=True)
        for name, data in blobs.items():
            with open(os.path.join(dest, os.path.basename(name)), "wb") as out:
                out.write(data)

        await execute_query(
            "INSERT INTO global_settings (key, value) VALUES ('verified_release_version', $1) "
            "ON CONFLICT (key) DO UPDATE SET value = $1", (version,))
        await execute_query(
            "INSERT INTO global_settings (key, value) VALUES ('verified_release_manifest', $1) "
            "ON CONFLICT (key) DO UPDATE SET value = $1", (json.dumps(manifest),))

        return {
            "ok": True,
            "version": version,
            "tag": manifest.get("tag"),
            "artifacts_present": present,
            "artifacts_expected": [a.get("name") for a in manifest.get("artifacts", [])],
        }

    return router
