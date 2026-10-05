"""POps sistem/sürüm uçları (ayrı APIRouter, server.py şişmesin).

- GET /api/health          — kimliksiz; DB erişilebilirliği + çalışan sürüm (hassas veri yok)
- GET /api/system/version  — admin; çalışan sürüm + (varsa) GitHub'daki son sürüm
- POST /api/system/fetch-release — superadmin; imzalı release'i GitHub'dan indirip upload-release
  ile aynı doğrulamayla stage eder (internetli kurulum; internetsiz kurulumda upload-release)

GitHub kontrolü OFFLINE-GÜVENLİDİR: kısa zaman aşımlı, event loop'u bloklamaz
(thread'de urllib), başarısız olursa `latest=None` döner ve hiçbir zaman hata fırlatmaz.
Ek bağımlılık yoktur (internetsiz okullarda pip gerektirmez). Sonuç saatte bir cache'lenir.

server.py bunu `build_router(require_admin, execute_query)` ile kurar; böylece bu modül
server.py'yi import etmez (döngüsel import yok).
"""
import asyncio
import base64
import datetime
import hashlib
import json
import os
import re
import secrets
import shutil
import time
import urllib.request
from typing import Dict, List, Optional

from fastapi import APIRouter, Depends, File, Form, HTTPException, UploadFile
from fastapi.responses import JSONResponse
from pydantic import BaseModel, field_validator

import release_verify
from pops import agent_version as agent_version_mod
from pops import devicelist, peer_cache, update_tracking
from pops.models import StrictInput, TargetMode, UpdateProgressInput, upper_mode


class EnrollTokenInput(StrictInput):
    lab_name: Optional[str] = None
    note: Optional[str] = None
    ttl_hours: int = 72
    max_uses: int = 1


class DeployUpdateInput(StrictInput):
    target_mode: TargetMode = "PC"
    targets: List[str] = []

    _mode = field_validator("target_mode", mode="before")(upper_mode)


class EnforceInput(BaseModel):
    enabled: bool


class CapabilityInput(BaseModel):
    pc_name: str
    terminal_enabled: Optional[bool] = None   # yalnızca False anlamlı (fail-safe kapatma)
    vision_enabled: Optional[bool] = None


class ReenrollInput(BaseModel):
    pc_name: str
    allow: bool = True


class FetchReleaseInput(BaseModel):
    tag: Optional[str] = None   # boşsa GitHub'daki son release
    force: bool = False


BASE_DIR = os.path.dirname(os.path.abspath(__file__))
# Doğrulanmış release'lerin stage edildiği çalışma zamanı dizini (git dışı)
RELEASES_DIR = os.path.join(BASE_DIR, "releases")
# Faz 5 self-update: backend'in (root DEĞİL) yazdığı istek dosyasının bulunduğu spool
# dizini. Root systemd path-unit (pops-selfupdate.path) bu dosyayı izleyip deploy'u
# çalıştırır. Dizin yoksa/yazılamıyorsa self-update "kurulu değil" sayılır (uç 503 döner).
SELFUPDATE_DIR = os.environ.get("POPS_SELFUPDATE_DIR", "/var/lib/pops")
# Root'un yazdığı durum dosyaları (deploy-status.json, deploy.log): yalnız root'un yazabildiği dizin
STATE_DIR = os.environ.get("POPS_STATE_DIR", "/var/lib/pops-state")
# Sunucu güncelleme kanalı (root'a ait; pops-selfupdate de aynı dosyayı okur): release (varsayılan) | main
SELFUPDATE_CONF = os.environ.get("POPS_SELFUPDATE_CONF", "/etc/pops/selfupdate.conf")


def _selfupdate_channel() -> str:
    try:
        with open(SELFUPDATE_CONF, "r", encoding="utf-8") as f:
            for line in f:
                line = line.strip()
                if line.startswith("CHANNEL="):
                    value = line.split("=", 1)[1].strip().strip("\"'")
                    return value if value in ("release", "main") else "release"
    except OSError:
        pass
    return "release"


GITHUB_REPO = os.environ.get("POPS_GITHUB_REPO", "PashaCore/POps")
_GITHUB_TIMEOUT = 5.0
_GITHUB_TTL = 3600.0  # saniye
_STALE_RETRY = 120.0  # çalışan sürüm "son sürüm"den yeniyken GitHub en erken bu kadar sonra yeniden sorulur
_latest_cache = {"at": 0.0, "tag": None, "checked": False}
_TAG_RE = re.compile(r"^v?[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.]+)?$")
_DOWNLOAD_TIMEOUT = 30.0
_MAX_MANIFEST_BYTES = 1024 * 1024
_MAX_ARTIFACT_BYTES = 200 * 1024 * 1024
_fetch_state = {"busy": False}
# Sunucu güncelleme sorgusu: canlıdaki commit (son self-update) ile GitHub main karşılaştırılır
_REV_RE = re.compile(r"^[0-9a-f]{7,40}$")
_SERVER_PATHS = ("Backend/", "Dashboard/", "keys/", "VERSION")
_COMPARE_TTL = 600.0
_compare_cache = {"rev": None, "at": 0.0, "data": None}
# Sürüm notları: GitHub'daki CHANGELOG.md (commit'e sabit sürümler kalıcı, main 10 dk) önbelleği
_changelog_cache = {}


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
    """GitHub'daki son release tag'i (bloklayan; thread'de çağrılır). Hata olursa None.

    /releases/latest ön sürümleri (-alpha, -beta) saymaz ve hepsi ön sürüm olduğunda 404 döner;
    bu yüzden liste alınır ve taslak olmayan en yeni release seçilir (liste en yeniden başlar).
    """
    url = "https://api.github.com/repos/%s/releases?per_page=10" % GITHUB_REPO
    req = urllib.request.Request(url, headers={
        "Accept": "application/vnd.github+json",
        "User-Agent": "POps-server",
    })
    try:
        with urllib.request.urlopen(req, timeout=_GITHUB_TIMEOUT) as resp:
            data = json.loads(resp.read().decode("utf-8"))
        for rel in data:
            if not rel.get("draft") and rel.get("tag_name"):
                return rel["tag_name"]
        return None
    except Exception:
        return None


async def _github_latest(force: bool = False) -> Optional[str]:
    now = time.time()
    if not force and _latest_cache["checked"] and (now - _latest_cache["at"]) < _GITHUB_TTL:
        cached = _latest_cache["tag"]
        # Çalışan sürüm önbellekteki "son sürüm"den yeniyse önbellek eskidir (GitHub yeni yayımlanan sürümü birkaç
        # dakika geç gösterebilir; sunucu o arada güncellenmiş olur): en çok 2 dakikada bir yeniden sorulur
        if not (cached and _newer(_read_version(), cached) and now - _latest_cache["at"] > _STALE_RETRY):
            return cached
    tag = await asyncio.to_thread(_fetch_github_latest_tag)
    # Sadece başarılı sonucu cache'le; offline'da eski değeri koru, çökme
    if tag is not None:
        _latest_cache["tag"] = tag
    _latest_cache["at"] = now
    _latest_cache["checked"] = True
    return _latest_cache["tag"]


def _http_get(url: str, limit: int, accept: str = "application/octet-stream") -> bytes:
    """Bloklayan HTTPS GET (thread'de çağrılır); `limit` bayttan büyük yanıtı reddeder."""
    req = urllib.request.Request(url, headers={"Accept": accept, "User-Agent": "POps-server"})
    with urllib.request.urlopen(req, timeout=_DOWNLOAD_TIMEOUT) as resp:
        data = resp.read(limit + 1)
    if len(data) > limit:
        raise ValueError("yanıt çok büyük: %s" % url)
    return data


def _github_release_assets(tag: str) -> dict:
    """Bir release'in dosyaları: ad -> indirme adresi (yalnız github.com üzerinden)."""
    url = "https://api.github.com/repos/%s/releases/tags/%s" % (GITHUB_REPO, tag)
    data = json.loads(_http_get(url, 5 * 1024 * 1024, "application/vnd.github+json").decode("utf-8"))
    return {a["name"]: a["browser_download_url"] for a in data.get("assets", [])
            if a.get("name") and str(a.get("browser_download_url", "")).startswith("https://github.com/")}


def _fetch_github_compare(rev: str) -> Optional[dict]:
    """Canlıdaki commit (rev) ile GitHub main arası: kaç commit ileride, sunucuyu (Backend/Dashboard/
    VERSION/keys) etkiliyor mu, son değişikliklerin başlıkları. Hata olursa None (çevrimdışı-güvenli)."""
    url = "https://api.github.com/repos/%s/compare/%s...main" % (GITHUB_REPO, rev)
    try:
        data = json.loads(_http_get(url, 20 * 1024 * 1024, "application/vnd.github+json").decode("utf-8"))
    except Exception:
        return None
    files = [str(f.get("filename", "")) for f in data.get("files", [])]
    commits = data.get("commits", [])   # eskiden yeniye
    return {
        "status": data.get("status"),   # identical | ahead | behind | diverged
        "ahead_by": int(data.get("ahead_by") or 0),
        "server_changed": any(f.startswith(p) for f in files for p in _SERVER_PATHS),
        "version_changed": "VERSION" in files,
        "commits": [str(c.get("commit", {}).get("message", "")).split("\n", 1)[0] for c in commits[-15:]][::-1],
    }


def _read_deploy_status() -> Optional[dict]:
    """Root self-update betiğinin yazdığı son deneme: state (running|ok|failed), rev, at."""
    # Eski kurulumlar dosyayı istek dizinine yazıyordu; yeni dizinde yoksa oraya bakılır
    for directory in (STATE_DIR, SELFUPDATE_DIR):
        try:
            with open(os.path.join(directory, "deploy-status.json"), "r", encoding="utf-8") as f:
                return json.load(f)
        except Exception:
            continue
    return None


async def _server_update(force: bool = False) -> dict:
    """Sunucu güncel mi?

    release kanalı (varsayılan): çalışan sürüm (VERSION) GitHub'daki son sürüm etiketiyle karşılaştırılır; ara
    commit'ler sayılmaz. main kanalı (geliştirme): canlı commit (son BAŞARILI self-update'in rev'i) origin/main ile
    karşılaştırılır."""
    st = _read_deploy_status() or {}
    rev = str(st.get("rev") or "")
    channel = _selfupdate_channel()
    out = {"rev": rev or None, "deployed_at": st.get("at"), "last_state": st.get("state"), "channel": channel,
           "checked": False, "update_available": None, "ahead_by": 0, "commits": [], "version_changed": False}
    if channel == "release":
        latest = await _github_latest(force=force)
        if latest:
            out.update({"checked": True, "latest_release": latest,
                        "update_available": _newer(latest, _read_version())})
        return out
    if st.get("state") != "ok" or not _REV_RE.match(rev):
        return out   # canlı commit bilinmiyor (hiç self-update yok ya da son deneme başarısız)
    now = time.time()
    if force or _compare_cache["rev"] != rev or (now - _compare_cache["at"]) >= _COMPARE_TTL:
        data = await asyncio.to_thread(_fetch_github_compare, rev)
        if data is not None:
            _compare_cache.update({"rev": rev, "at": now, "data": data})
    data = _compare_cache["data"] if _compare_cache["rev"] == rev else None
    if data:
        out.update({"checked": True, "ahead_by": data["ahead_by"], "commits": data["commits"],
                    "version_changed": data["version_changed"],
                    "update_available": data["ahead_by"] > 0 and data["server_changed"]})
    return out


def _fetch_changelog(ref: str) -> Optional[str]:
    """GitHub'daki CHANGELOG.md (belirli commit ya da main). Hata olursa None (çevrimdışı-güvenli)."""
    now = time.time()
    hit = _changelog_cache.get(ref)
    if hit and (_REV_RE.match(ref) or now - hit[0] < _COMPARE_TTL):
        return hit[1]
    try:
        text = _http_get("https://raw.githubusercontent.com/%s/%s/CHANGELOG.md" % (GITHUB_REPO, ref),
                         2 * 1024 * 1024, "text/plain").decode("utf-8")
    except Exception:
        return hit[1] if hit else None
    _changelog_cache[ref] = (now, text)
    return text


def _parse_changelog(text: str) -> List[dict]:
    """Keep a Changelog biçimi -> [{version, date, intro, groups: [{kind, items: [str]}]}]."""
    sections, cur, group = [], None, None
    for raw in (text or "").splitlines():
        line = raw.rstrip()
        if line.startswith("## ["):
            ver = line[4:line.index("]")] if "]" in line else line[4:]
            date = line.split(" - ", 1)[1].strip() if " - " in line else None
            cur = {"version": ver, "date": date, "intro": "", "groups": []}
            group = None
            sections.append(cur)
        elif cur is None:
            continue
        elif line.startswith("### "):
            group = {"kind": line[4:].strip(), "items": []}
            cur["groups"].append(group)
        elif line.startswith("- ") and group is not None:
            group["items"].append(line[2:].strip())
        elif line.startswith("  ") and group is not None and group["items"]:
            group["items"][-1] += " " + line.strip()
        elif line and group is None:
            cur["intro"] = (cur["intro"] + " " + line.strip()).strip()
    return sections


def _section(sections: List[dict], version: Optional[str]) -> Optional[dict]:
    want = _norm(version)
    for sec in sections:
        if _norm(sec["version"]) == want:
            return sec
    return None


def _new_items(new: Optional[dict], old: Optional[dict]) -> Optional[dict]:
    """`new` bölümünde olup `old` bölümünde olmayan maddeler (aynı sürüm bölümünün iki hali)."""
    if not new:
        return None
    seen = {i for g in (old or {}).get("groups", []) for i in g["items"]}
    groups = [{"kind": g["kind"], "items": [i for i in g["items"] if i not in seen]} for g in new["groups"]]
    groups = [g for g in groups if g["items"]]
    return {**new, "groups": groups} if groups else None


def _agent_msis(manifest: dict) -> List[str]:
    """İmzalı manifest'teki Windows ajan MSI'larının adları (en çok bir tane beklenir)."""
    return [str(a.get("name", "")) for a in manifest.get("artifacts", [])
            if str(a.get("name", "")).startswith("POps-Agent-") and str(a.get("name", "")).endswith("-win-x64.msi")]


# Linux ajan paketi (Agent-Linux/build_deb.py): pops-agent_<sürüm>_all.deb
_DEB_RE = re.compile(r"^pops-agent_[A-Za-z0-9.+~-]+_all\.deb$")


def _agent_debs(manifest: dict) -> List[str]:
    """İmzalı manifest'teki Linux ajan paketlerinin (.deb) adları (en çok bir tane beklenir)."""
    return [n for n in (str(a.get("name", "")) for a in manifest.get("artifacts", [])) if _DEB_RE.match(n)]


def _agent_packages(manifest: dict) -> Dict[str, str]:
    """Platform -> ajan paketi ("windows": MSI, "linux": .deb). Bir platform için birden fazla paket varsa
    ValueError. Her ajan imzalı manifest'ten yalnızca kendi paketini seçer (Windows ajanı MSI adını, Linux ajanı .deb
    adını arar); diğer platformun paketi onları etkilemez."""
    out = {}
    for platform, names in (("windows", _agent_msis(manifest)), ("linux", _agent_debs(manifest))):
        if len(names) > 1:
            raise ValueError("%s için tek bir ajan paketi bekleniyordu, %d var" % (platform, len(names)))
        if names:
            out[platform] = names[0]
    return out


def _newer(candidate: Optional[str], current: Optional[str]) -> bool:
    """candidate, current'tan yeni mi. GitHub'daki son yayın henüz yayımlanmamışken (etiket var, release yok) sunucu
    yeni sürümü çalıştırır; o zaman eski sürüm "yeni sürüm var" diye önerilmemeli. Çözümlenemeyen sürümde farklılık
    yeterli sayılır (eski davranış)."""
    if not candidate:
        return False
    a, b = agent_version_mod.parse(candidate), agent_version_mod.parse(current)
    if a is not None and b is not None:
        return a > b
    return _norm(candidate) != _norm(current)


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


def build_router(require_admin, require_superadmin, execute_query, manager, updates_dir, add_audit_log):
    router = APIRouter()

    async def _resolve_targets(data: DeployUpdateInput):
        if data.target_mode == "ALL":
            rows = await execute_query("SELECT pc_name FROM clients", fetch=True)
            return {r["pc_name"] for r in (rows or [])}
        if data.target_mode == "LAB":
            rows = await execute_query(
                "SELECT pc_name FROM clients WHERE lab_name = ANY($1::text[])", ([str(x) for x in data.targets],),
                fetch=True)
            return {r["pc_name"] for r in (rows or [])}
        return {t for t in (data.targets or []) if t}

    async def _staged_release() -> Optional[dict]:
        rows = await execute_query(
            "SELECT value FROM global_settings WHERE key='verified_release_manifest'", fetch=True)
        if rows and rows[0]["value"]:
            try:
                return json.loads(rows[0]["value"])
            except Exception:
                return None
        return None

    async def _enforce_enabled() -> bool:
        rows = await execute_query(
            "SELECT value FROM global_settings WHERE key='enforce_agent_auth'", fetch=True)
        return bool(rows and str(rows[0]["value"]) == "1")

    @router.get("/api/health")
    async def health():
        try:
            await execute_query("SELECT 1", fetch=True)
            db_ok = True
        except Exception:
            db_ok = False
        body = {"status": "ok" if db_ok else "degraded", "database": db_ok, "version": _read_version()}
        # Veritabanı yoksa hizmet hazır değildir: 503 (curl -f, ters vekil ve Docker sağlık kontrolü bunu görür)
        return body if db_ok else JSONResponse(body, status_code=503)

    @router.get("/api/system/version")
    async def system_version(check: bool = False, auth: dict = Depends(require_admin)):
        running = _read_version()
        latest = await _github_latest(force=check)
        staged = await _staged_release()
        staged_version = staged.get("version") if staged else None
        update_available = _newer(latest, running)
        # Doğrulanmış ajan paketi GitHub'daki son sürüm değil: panel "GitHub'dan indir" düğmesini gösterir
        release_available = _newer(latest, staged_version)
        counts = await execute_query(
            "SELECT count(*) AS total, count(s.pc_name) AS enrolled "
            "FROM clients c LEFT JOIN agent_secrets s ON s.pc_name = c.pc_name", fetch=True)
        return {
            "server": await _server_update(force=check),
            "agents_total": int(counts[0]["total"]) if counts else 0,
            "agents_enrolled": int(counts[0]["enrolled"]) if counts else 0,
            "running": running,
            "latest": latest,           # offline ise None olabilir
            "update_available": update_available,
            "checked_github": _latest_cache["checked"],
            "repo": GITHUB_REPO,
            "staged_version": staged_version,   # offline'da yüklenip doğrulanan sürüm
            "staged_tag": staged.get("tag") if staged else None,
            "release_available": release_available,
            "enforce_agent_auth": await _enforce_enabled(),
            "update_peer_cache": await peer_cache.enabled(),
        }

    @router.get("/api/system/release-notes")
    async def release_notes(auth: dict = Depends(require_admin)):
        """Sürüm notları (GitHub'daki CHANGELOG.md'den): sunucuda kurulu kodun içeriği, güncellemeyle gelecek
        yenilikler ve ajan paketinin notları. GitHub'a ulaşılamazsa available=false (çevrimdışı-güvenli)."""
        running = _read_version()
        st = _read_deploy_status() or {}
        rev = str(st.get("rev") or "") if st.get("state") == "ok" else ""
        main_text = await asyncio.to_thread(_fetch_changelog, "main")
        if main_text is None:
            return {"available": False}
        main_secs = _parse_changelog(main_text)
        rev_secs = None
        if _REV_RE.match(rev):
            rev_text = await asyncio.to_thread(_fetch_changelog, rev)
            rev_secs = _parse_changelog(rev_text) if rev_text is not None else None
        base = rev_secs if rev_secs is not None else main_secs
        installed = [sec for sec in (_section(base, "Unreleased"), _section(base, running)) if sec and sec["groups"]]
        incoming = []
        if rev_secs is not None:
            rev_versions = {_norm(sec["version"]) for sec in rev_secs}
            for sec in main_secs:
                if sec["version"] == "Unreleased":
                    diff = _new_items(sec, _section(rev_secs, "Unreleased"))
                    if diff:
                        incoming.append(diff)
                elif _norm(sec["version"]) not in rev_versions:
                    incoming.append(sec)
        latest = await _github_latest(force=False)
        return {
            "available": True,
            "running": running,
            "rev": rev or None,
            "installed": installed,
            "incoming": incoming,
            "agent": _section(main_secs, latest) if latest else None,
        }

    @router.post("/api/system/upload-release")
    async def upload_release(
        files: List[UploadFile] = File(...),
        force: bool = Form(False),
        auth: dict = Depends(require_superadmin),
    ):
        """İnternetsiz kurulum yolu: imzalı bir release (manifest.json + .sig + paketler)
        yükle, ed25519 imzasını depodaki açık anahtarla doğrula, özetleri kontrol et ve
        doğrulanmışsa stage et. Staged sürümü uygulamak: ajanlara /api/system/deploy-update,
        sunucu backend'ine /api/system/self-update (Faz 5)."""
        blobs = {}
        for f in files:
            blobs[os.path.basename(f.filename or "")] = await f.read()
        return await _verify_and_stage(blobs, force)

    async def _verify_and_stage(blobs: dict, force: bool) -> dict:
        """manifest.json + .sig + paket(ler): ed25519 imzası, SHA-256'lar ve downgrade koruması
        doğrulanırsa releases/<sürüm>/ altına yazar ve staged sürüm yapar. upload-release ve
        fetch-release aynı kontrolden geçer; paketin nereden geldiği güveni etkilemez."""
        pub = _pubkey_path()
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

    @router.post("/api/system/fetch-release")
    async def fetch_release(data: FetchReleaseInput, auth: dict = Depends(require_superadmin)):
        """İnternetli kurulum yolu: imzalı release'i (manifest.json + .sig + ajan paketleri: Windows MSI'ı ve varsa
        Linux .deb'i) GitHub'dan indirir ve upload-release ile AYNI doğrulamadan geçirip stage eder. Güven imzadan
        gelir, indirme kaynağından değil. Önce manifest indirilip doğrulanır; paketlerin adını ve özetini imzalı
        manifest belirler. Ajanlara göndermek yine ayrı adımdır (deploy-update)."""
        pub = _pubkey_path()
        if not pub:
            raise HTTPException(status_code=503,
                                detail="Açık anahtar bulunamadı (keys/pops_release_ed25519.pub.pem).")
        if _fetch_state["busy"]:
            raise HTTPException(status_code=409, detail="Başka bir indirme sürüyor.")
        _fetch_state["busy"] = True
        try:
            tag = (data.tag or "").strip() or await asyncio.to_thread(_fetch_github_latest_tag)
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
                    blobs[n] = await asyncio.to_thread(_http_get, assets[n], _MAX_MANIFEST_BYTES)
            except HTTPException:
                raise
            except Exception as exc:
                raise HTTPException(status_code=502, detail="GitHub'dan indirilemedi (%s): %s" % (tag, exc))
            try:
                manifest = release_verify.verify_manifest(blobs["manifest.json"], blobs["manifest.json.sig"], pub)
            except release_verify.ReleaseVerifyError as exc:
                raise HTTPException(status_code=400, detail="İmza doğrulanamadı: %s" % exc)
            try:
                packages = _agent_packages(manifest)
            except ValueError as exc:
                raise HTTPException(status_code=502, detail="%s release'i: %s." % (tag, exc))
            missing = [n for n in packages.values() if n not in assets]
            if not packages or missing:
                raise HTTPException(status_code=502, detail="%s release'inde imzalı ajan paketi bulunamadı%s." % (
                    tag, (": " + ", ".join(missing)) if missing else ""))
            for name in packages.values():
                try:
                    blobs[name] = await asyncio.to_thread(_http_get, assets[name], _MAX_ARTIFACT_BYTES)
                except Exception as exc:
                    raise HTTPException(status_code=502, detail="%s indirilemedi: %s" % (name, exc))
            result = await _verify_and_stage(blobs, data.force)
        finally:
            _fetch_state["busy"] = False
        await add_audit_log("*", "fetch_release",
                            "İmzalı sürüm GitHub'dan indirildi ve doğrulandı: %s" % result["version"],
                            {"tag": tag, "by": auth.get("sub"), "artifacts": result["artifacts_present"]})
        return result

    # --- Ajan kayıt (enroll) jetonları (Faz 3) -----------------------------------
    # Jeton üretimi/yönetimi burada. Jetonun TÜKETİMİ (ilk bağlanışta doğrula + secret ver)
    # ve /ws/agent kimlik zorlaması server.py'de uygulanmıştır (enroll consume + set_secret,
    # enforce_agent_auth → WS 4401).

    @router.post("/api/system/enroll-token")
    async def create_enroll_token(data: EnrollTokenInput, auth: dict = Depends(require_superadmin)):
        ttl = max(1, min(int(data.ttl_hours or 72), 24 * 30))  # 1 saat – 30 gün
        uses = max(1, min(int(data.max_uses or 1), 10000))     # 1 = tek kullanımlık; lab için toplu
        lab = (data.lab_name or "").strip() or None
        note = (data.note or "").strip() or None
        token = secrets.token_urlsafe(24)
        # Veritabanında yalnızca özet ve tanıma ipucu (ilk 6 karakter) kalır; jeton yalnızca şimdi gösterilir
        await execute_query(
            "INSERT INTO enroll_tokens (token_hash, token_hint, lab_name, note, expires_at, max_uses) "
            "VALUES ($1, $2, $3, $4, NOW() + make_interval(hours => $5), $6)",
            (hashlib.sha256(token.encode("utf-8")).hexdigest(), token[:6], lab, note, ttl, uses))
        return {"token": token, "lab_name": lab, "note": note, "ttl_hours": ttl, "max_uses": uses}

    @router.get("/api/system/enroll-tokens")
    async def list_enroll_tokens(auth: dict = Depends(require_superadmin)):
        return await execute_query(
            "SELECT id, token_hint, lab_name, note, created_at, expires_at, is_used, used_by, used_at, "
            "max_uses, use_count, (expires_at < NOW() AND NOT is_used) AS expired "
            "FROM enroll_tokens ORDER BY id DESC LIMIT 200", fetch=True)

    @router.delete("/api/system/enroll-token/{token_id}")
    async def revoke_enroll_token(token_id: int, auth: dict = Depends(require_superadmin)):
        await execute_query("DELETE FROM enroll_tokens WHERE id = $1", (token_id,))
        return {"ok": True}

    @router.post("/api/system/update-progress")
    async def update_progress(data: UpdateProgressInput, auth: dict = Depends(require_admin)):
        """Gönderilmiş bir ajan güncellemesinin cihaz cihaz durumu (panelin işlem merkezi): bağlı mı, çalışan sürüm,
        sonucu beklenen gönderim var mı (ne zaman gönderildi, ajanın bildirdiği son adım) ve gönderimden sonra gelen
        güncelleme sonucu (başarılı / geri döndü / reddedildi). Zamanlar Unix saniyesi; "now" sunucunun saati."""
        pcs = list(dict.fromkeys(str(p) for p in data.pcs))[:5000]
        if not pcs:
            return {"items": []}
        since = datetime.datetime.fromtimestamp(max(0.0, data.since)).strftime("%Y-%m-%d %H:%M:%S")
        rows = await execute_query(
            "SELECT c.pc_name, c.status, c.running_version, av.version AS agent_version FROM clients c "
            "LEFT JOIN agent_versions av ON av.pc_name = c.pc_name WHERE c.pc_name = ANY($1::text[])",
            (pcs,), fetch=True,
        )
        results = await execute_query(
            "SELECT DISTINCT ON (hw_id) hw_id, changes FROM device_audit_logs "
            "WHERE action = 'update_result' AND hw_id = ANY($1::text[]) AND timestamp >= $2 ORDER BY hw_id, id DESC",
            (pcs, since), fetch=True,
        )
        last = {}
        for r in results or []:
            try:
                ch = json.loads(r["changes"]) if isinstance(r["changes"], str) else (r["changes"] or {})
            except ValueError:
                ch = {}
            last[r["hw_id"]] = {k: ch.get(k) for k in ("status", "rollback", "to_version", "detail", "agent_state")}
        known = {r["pc_name"]: r for r in rows or []}
        # Sınıf içi eş gönderimi: bilgisayarın rolü (tohum, tohumu bekliyor, eşten) ve sınıf başına özet
        peer = peer_cache.status_for(pcs)
        items = []
        for pc in pcs:
            r = known.get(pc)
            version = (r and (r["running_version"] or r["agent_version"])) or None
            sent = manager.pending_updates.get(pc)
            # Ajanın bildirdiği son adım (0.1.22+; eski ajanda hep boş). Yalnızca sonucu beklenen gönderimde olur.
            stage = (manager.update_stages.get(pc) or {}) if sent else {}
            items.append({
                "pc": pc,
                "known": r is not None,
                "online": bool(r and str(r["status"] or "").lower() != "offline"),
                "version": version,
                "on_target": bool(version) and _norm(version) == _norm(data.version),
                "pending": sent is not None,
                "sent_at": sent[1] if sent else None,
                "stage": stage.get("stage"),
                "detail": stage.get("detail"),
                "attempt": stage.get("attempt"),
                "of": stage.get("of"),
                "stage_at": stage.get("stage_at"),
                "result": last.get(pc),
                "peer": peer["items"].get(pc),
            })
        return {"items": items, "now": time.time(), "peer_labs": peer["labs"]}

    @router.post("/api/system/deploy-update")
    async def deploy_update(data: DeployUpdateInput, auth: dict = Depends(require_superadmin)):
        """Staged (yüklenip doğrulanmış) imzalı release'i hedef ajanlara dağıtır. Ajana
        {"action":"update_agent","manifest":<b64>,"manifest_sig":<sig>} gönderilir; ajan kendi paketini (Windows MSI,
        Linux .deb) imzalı manifest'ten seçer, kendi ServerUrl'inin /updates/<ad>'ından indirip imza + SHA-256'yı
        kendisi doğrular. Release'te kendi platformunun paketi olmayan cihazlar atlanır (skipped_no_package)."""
        staged = await _staged_release()
        if not staged:
            raise HTTPException(status_code=400,
                                detail="Önce imzalı bir release yükleyin (Sistem > çevrimdışı imzalı paket).")
        version = str(staged.get("version") or "")
        reldir = os.path.join(RELEASES_DIR, version.replace(os.sep, "_"))
        mpath = os.path.join(reldir, "manifest.json")
        spath = os.path.join(reldir, "manifest.json.sig")
        if not (os.path.isfile(mpath) and os.path.isfile(spath)):
            raise HTTPException(status_code=409, detail="Staged release dosyaları eksik; tekrar yükleyin.")
        try:
            packages = _agent_packages(staged)
        except ValueError as exc:
            raise HTTPException(status_code=409, detail="Staged release: %s." % exc)
        if not packages:
            raise HTTPException(status_code=409, detail="Staged release'de ajan paketi (MSI ya da .deb) yok.")
        # Ajan buradan indirir (/updates StaticFiles); yol-gezinme koruması. Yalnızca staged klasörde olan paketler
        # kopyalanır (upload-release'e yalnız MSI yüklenmiş olabilir); paketi olmayan platformun cihazları atlanır.
        os.makedirs(updates_dir, exist_ok=True)
        available = {}
        for platform, name in packages.items():
            src = os.path.join(reldir, name)
            if not os.path.isfile(src):
                continue
            dst = os.path.realpath(os.path.join(updates_dir, name))
            if os.path.dirname(dst) != os.path.realpath(updates_dir):
                raise HTTPException(status_code=400, detail="geçersiz paket adı")
            shutil.copyfile(src, dst)
            available[platform] = name
        if not available:
            raise HTTPException(status_code=409, detail="Paket staged klasörde yok: %s (upload-release'e paketi de "
                                                        "yükleyin)." % ", ".join(packages.values()))
        msi_name, deb_name = available.get("windows"), available.get("linux")
        with open(mpath, "rb") as f:
            manifest_b64 = base64.b64encode(f.read()).decode("ascii")
        with open(spath, "r", encoding="utf-8") as f:
            sig = f.read().strip()
        msg = {"action": "update_agent", "manifest": manifest_b64, "manifest_sig": sig}
        msi_sha = str((release_verify.artifact_entry(staged, msi_name) or {}).get("sha256") or "").lower()

        targets = await _resolve_targets(data)
        rows = await execute_query(
            "SELECT pc_name, platform FROM clients WHERE pc_name = ANY($1::text[])", (sorted(targets),), fetch=True)
        platform_of = {r["pc_name"]: (r["platform"] or "windows") for r in rows or []}
        no_package = sorted(t for t in targets if platform_of.get(t, "windows") not in available)
        targets = {t for t in targets if t not in no_package}
        # Aynı sürüm son 15 dk içinde gönderildiyse (ya da ajan o sürede adım bildirdiyse) yeniden gönderilmez: ajan
        # kurulum sürerken gelen ikinci emri zaten yok sayar. Kurulum sırasında bağlantısız görünen cihaz da burada.
        already = sorted(await update_tracking.recently_sent(targets, version))
        targets = {t for t in targets if t not in already}
        # Sınıf içi eş önbelleği (pops/peer_cache.py): sınıfın tohumu şimdi, geri kalanı tohum hazır olunca "peers"
        # ile gider; hazır eşi olan sınıfa hemen peers ile; özelliği olmayanlara bugünkü gibi. Eşler MSI'ı paylaşır
        # (msi_sha): yalnızca Windows cihazlar; .deb alan Linux cihazlar bugünkü gibi doğrudan gönderilir
        staging = await peer_cache.plan([t for t in targets if t in manager.active_agents
                                         and platform_of.get(t, "windows") == "windows"], version, msi_sha, msg)
        online = []
        offline = sorted(t for t in targets if t not in manager.active_agents)
        for pc in sorted(t for t in targets if t in manager.active_agents and t not in staging.hold):
            peers = staging.peers.get(pc)
            if not await manager.send_command(dict(msg, peers=peers) if peers else msg, pc):
                offline.append(pc)   # bağlantı bu arada koptu
                await peer_cache.not_sent(pc)
                continue
            online.append(pc)
            # Sonucu beklenen güncelleme; tabloda da tutulur, sunucu yeniden başlasa da izlenir; önceki gönderimin
            # adımı silinir (bkz. pops/update_tracking.py)
            await update_tracking.mark_sent(pc, version)
        waiting = sorted(pc for pc in staging.hold if peer_cache.is_waiting(pc))
        seeds = sorted(pc for pc in staging.seeds if pc in online)
        with_peers = sorted(pc for pc in staging.peers if pc in online)
        await add_audit_log("*", "deploy_update", "İmzalı güncelleme dağıtıldı: %s" % version,
                            {"version": version, "msi": msi_name, "deb": deb_name, "dispatched": online,
                             "offline": offline, "no_package": no_package, "already_pending": already,
                             "seeds": seeds, "waiting_for_seed": waiting, "with_peers": with_peers,
                             "by": auth.get("sub")})
        return {"ok": True, "version": version, "msi": msi_name, "deb": deb_name, "dispatched": online,
                "skipped_offline": offline, "skipped_no_package": no_package, "already_pending": already,
                "seeds": seeds, "waiting_for_seed": waiting, "with_peers": with_peers}

    @router.post("/api/system/update-peer-cache")
    async def set_update_peer_cache(data: EnforceInput, auth: dict = Depends(require_superadmin)):
        """Ajan güncellemesinde sınıf içi eş önbelleği (varsayılan açık). Kapatılınca tohum bekleyen bilgisayarlara
        güncelleme hemen, eşsiz gönderilir; sonraki gönderimler bugünkü gibi hepsine birden gider."""
        await peer_cache.set_enabled(data.enabled)
        await add_audit_log("*", "update_peer_cache",
                            "Güncellemede eş önbelleği %s" % ("açıldı" if data.enabled else "kapatıldı"),
                            {"enabled": data.enabled, "by": auth.get("sub")})
        return {"ok": True, "update_peer_cache": data.enabled}

    @router.post("/api/system/enforce-auth")
    async def set_enforce(data: EnforceInput, auth: dict = Depends(require_superadmin)):
        """Ajan kimlik zorlamasını aç/kapa. AÇIKKEN secret'sız ajan bağlantıları reddedilir —
        yalnızca tüm filo yeni (kimlik doğrulayan) ajana geçtikten sonra açın."""
        await execute_query(
            "INSERT INTO global_settings (key, value) VALUES ('enforce_agent_auth', $1) "
            "ON CONFLICT (key) DO UPDATE SET value = $1", ("1" if data.enabled else "0",))
        await add_audit_log("*", "enforce_auth",
                            "Ajan kimlik zorlaması %s" % ("AÇILDI" if data.enabled else "kapatıldı"),
                            {"enabled": data.enabled})
        return {"ok": True, "enforce_agent_auth": data.enabled}

    # --- Faz 5: sunucu backend self-update (SSH'siz, panelden) --------------------
    def _selfupdate_configured() -> bool:
        return os.path.isdir(SELFUPDATE_DIR) and os.access(SELFUPDATE_DIR, os.W_OK)

    @router.get("/api/system/self-update/status")
    async def self_update_status(auth: dict = Depends(require_admin)):
        """Son self-update denemesinin durumunu (deploy-status.json) ve kurulu olup
        olmadığını döner. Root deploy betiği bu dosyayı yazar."""
        return {
            "configured": _selfupdate_configured(),
            "pending": os.path.exists(os.path.join(SELFUPDATE_DIR, "deploy-request.json")),
            "status": _read_deploy_status(),
        }

    @router.post("/api/system/self-update")
    async def self_update(auth: dict = Depends(require_superadmin)):
        """origin/main'den sunucu backend'ini güncellemeyi KUYRUKLAR. Backend root
        olmadığından yalnızca bir istek dosyası yazar; root systemd path-unit bunu görüp
        git ff-only çeker ve pops-deploy-backend'i (sağlık kontrolü + geri dönüşlü) çalıştırır.
        Keyfi kod yürütülmez; yalnızca origin/main yeniden dağıtılır."""
        if not _selfupdate_configured():
            raise HTTPException(
                status_code=503,
                detail="Self-update kurulu değil. systemd path-unit'i etkinleştirin (bkz. docs/self-update.md).")
        target = "origin/main" if _selfupdate_channel() == "main" else "latest-release"
        payload = {
            "requested_by": auth.get("sub"),
            "requested_at": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
            "target": target,   # bilgi amaçlı: root betiği kanalı kendi ayar dosyasından okur
        }
        req_path = os.path.join(SELFUPDATE_DIR, "deploy-request.json")
        tmp = req_path + ".tmp"
        try:
            with open(tmp, "w", encoding="utf-8") as f:
                json.dump(payload, f)
            os.replace(tmp, req_path)   # atomik: path-unit yarım dosya görmesin
        except OSError as exc:
            raise HTTPException(status_code=503, detail="İstek yazılamadı: %s" % exc)
        await add_audit_log("*", "self_update",
                            "Sunucu self-update kuyruklandı (%s)" % (auth.get("sub") or "?"),
                            {"target": target})
        return {"ok": True, "queued": True}

    # --- Ajan yetenek politikası (terminal/Vision) — fail-safe: yalnızca KAPATMA -----
    @router.post("/api/system/set-capabilities")
    async def set_capabilities(data: CapabilityInput, auth: dict = Depends(require_superadmin)):
        """Bir cihazda terminal/Vision yeteneğini kapatır. FAIL-SAFE: ajan sunucudan gelen "aç"ı
        yok sayar (kalıcı açma yeniden kurulum / offline-imzalı politika ister); bu uç pratikte
        yalnızca KAPATMAK içindir. İstek kalıcı kaydedilir; ajan çevrimdışıysa yeniden bağlanınca
        uygulanır. Panelde açığa çekmek isteği temizler ama ajanı otomatik açmaz."""
        pc = (data.pc_name or "").strip()
        if not pc:
            raise HTTPException(status_code=400, detail="pc_name gerekli")
        msg = {"action": "set_capabilities"}
        sets, params = [], []
        if data.terminal_enabled is not None:
            msg["terminal_enabled"] = bool(data.terminal_enabled)
            params.append(not bool(data.terminal_enabled))
            sets.append("cap_terminal_disable_requested=$%d" % len(params))
        if data.vision_enabled is not None:
            msg["vision_enabled"] = bool(data.vision_enabled)
            params.append(not bool(data.vision_enabled))
            sets.append("cap_vision_disable_requested=$%d" % len(params))
        if len(msg) == 1:
            raise HTTPException(status_code=400, detail="terminal_enabled ve/veya vision_enabled verin.")
        params.append(pc)
        await execute_query("UPDATE clients SET %s WHERE pc_name=$%d" % (", ".join(sets), len(params)),
                            tuple(params))
        await devicelist.sync([pc])
        # send_command çevrimdışıysa no-op; online durumunu ayrıca bildiriyoruz. Kapatma isteği
        # kalıcı kaydedildi, ajan sonra bağlanınca /ws/agent 'capabilities' handler'ı uygular.
        online = pc in manager.active_agents
        await manager.send_command(msg, pc)
        applied = {k: msg[k] for k in msg if k != "action"}
        await add_audit_log(pc, "set_capabilities", "Yetenek politikası gönderildi", {**applied, "by": auth.get("sub")})
        return {"ok": True, "delivered_online": online, **applied}

    # --- Yeniden-enroll izni (F2 kurtarma yolu: Deep Freeze / yeniden kurulum) --------
    @router.post("/api/system/allow-reenroll")
    async def allow_reenroll(data: ReenrollInput, auth: dict = Depends(require_superadmin)):
        """Bir cihaz için tek-seferlik yeniden-enroll iznini aç/kapat. Varsayılan KAPALI: enroll
        token'la mevcut secret'ı ele geçirme engellenir (F2). AÇIKKEN cihaz enroll token'la yeniden
        secret alabilir; sunucu başarılı yeniden-enroll'da bayrağı otomatik FALSE yapar."""
        pc = (data.pc_name or "").strip()
        if not pc:
            raise HTTPException(status_code=400, detail="pc_name gerekli")
        await execute_query("UPDATE clients SET allow_reenroll=$1 WHERE pc_name=$2", (bool(data.allow), pc))
        await add_audit_log(pc, "allow_reenroll",
                            "Yeniden-enroll izni %s" % ("AÇILDI" if data.allow else "kapatıldı"),
                            {"allow": bool(data.allow), "by": auth.get("sub")})
        return {"ok": True, "pc_name": pc, "allow_reenroll": bool(data.allow)}

    return router
