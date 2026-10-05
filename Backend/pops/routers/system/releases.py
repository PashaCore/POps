"""İmzalı release'in stage edilmesi: çevrimdışı yükleme (upload-release) ve GitHub'dan indirme (fetch-release).

İkisi de aynı doğrulamadan geçer (_verify_and_stage): ed25519 imzası depodaki açık anahtarla, SHA-256'lar imzalı
manifest'le, downgrade koruması released_at ile. Güven imzadan gelir, paketin nereden geldiğinden değil. Staged sürümü
ajanlara göndermek ayrı adımdır (updates.py, deploy-update).
"""
import asyncio
import json
import os
import re
from typing import List

from fastapi import APIRouter, Depends, File, Form, HTTPException, UploadFile

import release_verify
from pops.routers.system import common

_TAG_RE = re.compile(r"^v?[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.]+)?$")
_MAX_MANIFEST_BYTES = 1024 * 1024
_MAX_ARTIFACT_BYTES = 200 * 1024 * 1024
_fetch_state = {"busy": False}


def _github_release_assets(tag: str) -> dict:
    """Bir release'in dosyaları: ad -> indirme adresi (yalnız github.com üzerinden)."""
    url = "https://api.github.com/repos/%s/releases/tags/%s" % (common.GITHUB_REPO, tag)
    data = json.loads(common._http_get(url, 5 * 1024 * 1024, "application/vnd.github+json").decode("utf-8"))
    return {a["name"]: a["browser_download_url"] for a in data.get("assets", [])
            if a.get("name") and str(a.get("browser_download_url", "")).startswith("https://github.com/")}


async def _verify_and_stage(d: common.Deps, blobs: dict, force: bool) -> dict:
    """manifest.json + .sig + paket(ler): ed25519 imzası, SHA-256'lar ve downgrade koruması
    doğrulanırsa releases/<sürüm>/ altına yazar ve staged sürüm yapar. upload-release ve
    fetch-release aynı kontrolden geçer; paketin nereden geldiği güveni etkilemez."""
    pub = common._pubkey_path()
    if not pub:
        raise HTTPException(status_code=503,
                            detail="Açık anahtar bulunamadı (keys/pops_release_ed25519.pub.pem).")
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
    prev = await common.staged_release(d)
    if prev and not force:
        if int(manifest.get("released_at", 0)) <= int(prev.get("released_at", 0)):
            raise HTTPException(
                status_code=409,
                detail="Yüklenen sürüm mevcut doğrulanmış sürümden (%s) yeni değil. force ile geçin."
                % prev.get("version"))

    version = str(manifest["version"])
    dest = os.path.join(common.RELEASES_DIR, version.replace(os.sep, "_"))
    os.makedirs(dest, exist_ok=True)
    for name, data in blobs.items():
        with open(os.path.join(dest, os.path.basename(name)), "wb") as out:
            out.write(data)

    await d.execute_query(
        "INSERT INTO global_settings (key, value) VALUES ('verified_release_version', $1) "
        "ON CONFLICT (key) DO UPDATE SET value = $1", (version,))
    await d.execute_query(
        "INSERT INTO global_settings (key, value) VALUES ('verified_release_manifest', $1) "
        "ON CONFLICT (key) DO UPDATE SET value = $1", (json.dumps(manifest),))

    return {
        "ok": True,
        "version": version,
        "tag": manifest.get("tag"),
        "artifacts_present": present,
        "artifacts_expected": [a.get("name") for a in manifest.get("artifacts", [])],
    }


def register(router: APIRouter, d: common.Deps) -> None:
    @router.post("/api/system/upload-release")
    async def upload_release(
        files: List[UploadFile] = File(...),
        force: bool = Form(False),
        auth: dict = Depends(d.require_superadmin),
    ):
        """İnternetsiz kurulum yolu: imzalı bir release (manifest.json + .sig + paketler)
        yükle, ed25519 imzasını depodaki açık anahtarla doğrula, özetleri kontrol et ve
        doğrulanmışsa stage et. Staged sürümü uygulamak: ajanlara /api/system/deploy-update,
        sunucu backend'ine /api/system/self-update (Faz 5)."""
        blobs = {}
        for f in files:
            blobs[os.path.basename(f.filename or "")] = await f.read()
        return await _verify_and_stage(d, blobs, force)

    @router.post("/api/system/fetch-release")
    async def fetch_release(data: common.FetchReleaseInput, auth: dict = Depends(d.require_superadmin)):
        """İnternetli kurulum yolu: imzalı release'i (manifest.json + .sig + ajan paketleri: Windows MSI'ı ve varsa
        Linux .deb'i) GitHub'dan indirir ve upload-release ile AYNI doğrulamadan geçirip stage eder. Güven imzadan
        gelir, indirme kaynağından değil. Önce manifest indirilip doğrulanır; paketlerin adını ve özetini imzalı
        manifest belirler. Ajanlara göndermek yine ayrı adımdır (deploy-update)."""
        pub = common._pubkey_path()
        if not pub:
            raise HTTPException(status_code=503,
                                detail="Açık anahtar bulunamadı (keys/pops_release_ed25519.pub.pem).")
        if _fetch_state["busy"]:
            raise HTTPException(status_code=409, detail="Başka bir indirme sürüyor.")
        _fetch_state["busy"] = True
        try:
            tag = (data.tag or "").strip() or await asyncio.to_thread(common._fetch_github_latest_tag)
            if not tag:
                raise HTTPException(status_code=502, detail="GitHub'a ulaşılamadı. İnternetsiz kurulumda "
                                                            "paketi 'Çevrimdışı imzalı paket yükle' ile yükleyin.")
            if not _TAG_RE.match(tag):
                raise HTTPException(status_code=400, detail="Geçersiz sürüm etiketi.")
            names = ("manifest.json", "manifest.json.sig")
            try:
                assets = await asyncio.to_thread(_github_release_assets, tag)
                missing = [n for n in names if n not in assets]
                if missing:
                    raise HTTPException(status_code=502, detail="%s release'inde yok: %s" % (tag, ", ".join(missing)))
                blobs = {}
                for n in names:
                    blobs[n] = await asyncio.to_thread(common._http_get, assets[n], _MAX_MANIFEST_BYTES)
            except HTTPException:
                raise
            except Exception as exc:
                raise HTTPException(status_code=502, detail="GitHub'dan indirilemedi (%s): %s" % (tag, exc))
            try:
                manifest = release_verify.verify_manifest(blobs["manifest.json"], blobs["manifest.json.sig"], pub)
            except release_verify.ReleaseVerifyError as exc:
                raise HTTPException(status_code=400, detail="İmza doğrulanamadı: %s" % exc)
            try:
                packages = common._agent_packages(manifest)
            except ValueError as exc:
                raise HTTPException(status_code=502, detail="%s release'i: %s." % (tag, exc))
            missing = [n for n in packages.values() if n not in assets]
            if not packages or missing:
                raise HTTPException(status_code=502, detail="%s release'inde imzalı ajan paketi bulunamadı%s." % (
                    tag, (": " + ", ".join(missing)) if missing else ""))
            for name in packages.values():
                try:
                    blobs[name] = await asyncio.to_thread(common._http_get, assets[name], _MAX_ARTIFACT_BYTES)
                except Exception as exc:
                    raise HTTPException(status_code=502, detail="%s indirilemedi: %s" % (name, exc))
            result = await _verify_and_stage(d, blobs, data.force)
        finally:
            _fetch_state["busy"] = False
        await d.add_audit_log("*", "fetch_release",
                              "İmzalı sürüm GitHub'dan indirildi ve doğrulandı: %s" % result["version"],
                              {"tag": tag, "by": auth.get("sub"), "artifacts": result["artifacts_present"]})
        return result
