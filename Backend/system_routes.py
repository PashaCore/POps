"""POps sistem/sürüm uçları (ayrı APIRouter, server.py şişmesin).

- GET /api/health          — kimliksiz; DB erişilebilirliği + çalışan sürüm (hassas veri yok)
- GET /api/system/version  — admin; çalışan sürüm + (varsa) GitHub'daki son sürüm
- POST /api/system/fetch-release — superadmin; imzalı release'i GitHub'dan indirip upload-release
  ile aynı doğrulamayla stage eder (internetli kurulum; internetsiz kurulumda upload-release)

GitHub kontrolü OFFLINE-GÜVENLİDİR: kısa zaman aşımlı, event loop'u bloklamaz
(thread'de urllib), başarısız olursa `latest=None` döner ve hiçbir zaman hata fırlatmaz.
Ek bağımlılık yoktur (internetsiz okullarda pip gerektirmez). Sonuç saatte bir cache'lenir.

server.py bunu `build_router(require_admin, execute_query)` ile kurar; böylece bu modül
server.py'yi import etmez (döngüsel import yok). Python 3.9 uyumlu.
"""
import asyncio
import base64
import json
import os
import re
import secrets
import shutil
import time
import urllib.request
from typing import List, Optional

from fastapi import APIRouter, Depends, File, Form, HTTPException, UploadFile
from pydantic import BaseModel

import release_verify


class EnrollTokenInput(BaseModel):
    lab_name: Optional[str] = None
    note: Optional[str] = None
    ttl_hours: int = 72
    max_uses: int = 1


class DeployUpdateInput(BaseModel):
    target_mode: str = "PC"          # "ALL" | "LAB" | "PC"
    targets: List[str] = []


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
GITHUB_REPO = os.environ.get("POPS_GITHUB_REPO", "PashaCore/POps")
_GITHUB_TIMEOUT = 5.0
_GITHUB_TTL = 3600.0  # saniye
_latest_cache = {"at": 0.0, "tag": None, "checked": False}
_TAG_RE = re.compile(r"^v?[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.]+)?$")
_DOWNLOAD_TIMEOUT = 30.0
_MAX_MANIFEST_BYTES = 1024 * 1024
_MAX_ARTIFACT_BYTES = 200 * 1024 * 1024
_fetch_state = {"busy": False}


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
        return _latest_cache["tag"]
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


def _agent_msis(manifest: dict) -> List[str]:
    """İmzalı manifest'teki ajan MSI'larının adları (deploy-update tam olarak birini bekler)."""
    return [str(a.get("name", "")) for a in manifest.get("artifacts", [])
            if str(a.get("name", "")).startswith("POps-Agent-") and str(a.get("name", "")).endswith("-win-x64.msi")]


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
            out = set()
            for lab in data.targets:
                rows = await execute_query("SELECT pc_name FROM clients WHERE lab_name=$1", (lab,), fetch=True)
                out |= {r["pc_name"] for r in (rows or [])}
            return out
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
        return {"status": "ok" if db_ok else "degraded", "database": db_ok, "version": _read_version()}

    @router.get("/api/system/version")
    async def system_version(check: bool = False, auth: dict = Depends(require_admin)):
        running = _read_version()
        latest = await _github_latest(force=check)
        staged = await _staged_release()
        staged_version = staged.get("version") if staged else None
        update_available = bool(latest and _norm(latest) != _norm(running))
        # Doğrulanmış ajan paketi GitHub'daki son sürüm değil: panel "GitHub'dan indir" düğmesini gösterir
        release_available = bool(latest and _norm(latest) != _norm(staged_version))
        return {
            "running": running,
            "latest": latest,           # offline ise None olabilir
            "update_available": update_available,
            "checked_github": _latest_cache["checked"],
            "repo": GITHUB_REPO,
            "staged_version": staged_version,   # offline'da yüklenip doğrulanan sürüm
            "staged_tag": staged.get("tag") if staged else None,
            "release_available": release_available,
            "enforce_agent_auth": await _enforce_enabled(),
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
        """İnternetli kurulum yolu: imzalı release'i (manifest.json + .sig + ajan MSI'ı) GitHub'dan
        indirir ve upload-release ile AYNI doğrulamadan geçirip stage eder. Güven imzadan gelir,
        indirme kaynağından değil. Önce manifest indirilip doğrulanır; MSI'ın adını ve özetini
        imzalı manifest belirler. Ajanlara göndermek yine ayrı adımdır (deploy-update)."""
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
            msis = _agent_msis(manifest)
            if len(msis) != 1 or msis[0] not in assets:
                raise HTTPException(status_code=502, detail="%s release'inde imzalı ajan MSI'ı bulunamadı." % tag)
            try:
                blobs[msis[0]] = await asyncio.to_thread(_http_get, assets[msis[0]], _MAX_ARTIFACT_BYTES)
            except Exception as exc:
                raise HTTPException(status_code=502, detail="MSI indirilemedi: %s" % exc)
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
        await execute_query(
            "INSERT INTO enroll_tokens (token, lab_name, note, expires_at, max_uses) "
            "VALUES ($1, $2, $3, NOW() + make_interval(hours => $4), $5)",
            (token, lab, note, ttl, uses))
        return {"token": token, "lab_name": lab, "note": note, "ttl_hours": ttl, "max_uses": uses}

    @router.get("/api/system/enroll-tokens")
    async def list_enroll_tokens(auth: dict = Depends(require_superadmin)):
        return await execute_query(
            "SELECT id, token, lab_name, note, created_at, expires_at, is_used, used_by, used_at, "
            "max_uses, use_count, (expires_at < NOW() AND NOT is_used) AS expired "
            "FROM enroll_tokens ORDER BY id DESC LIMIT 200", fetch=True)

    @router.delete("/api/system/enroll-token/{token_id}")
    async def revoke_enroll_token(token_id: int, auth: dict = Depends(require_superadmin)):
        await execute_query("DELETE FROM enroll_tokens WHERE id = $1", (token_id,))
        return {"ok": True}

    @router.post("/api/system/deploy-update")
    async def deploy_update(data: DeployUpdateInput, auth: dict = Depends(require_superadmin)):
        """Staged (yüklenip doğrulanmış) imzalı release'i hedef ajanlara dağıtır. Ajana
        {"action":"update_agent","manifest":<b64>,"manifest_sig":<sig>} gönderilir; ajan MSI'ı
        kendi ServerUrl'inin /updates/<ad>'ından indirip imza + SHA-256'yı kendisi doğrular."""
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
        msis = _agent_msis(staged)
        if len(msis) != 1:
            raise HTTPException(status_code=409,
                                detail="Staged release'de tek bir ajan MSI'ı bekleniyordu, %d var." % len(msis))
        msi_name = msis[0]
        msi_src = os.path.join(reldir, msi_name)
        if not os.path.isfile(msi_src):
            raise HTTPException(status_code=409,
                                detail="MSI staged klasörde yok: %s (upload-release'e MSI'ı da yükleyin)." % msi_name)
        # Ajan buradan indirir (/updates StaticFiles); yol-gezinme koruması
        os.makedirs(updates_dir, exist_ok=True)
        msi_dst = os.path.realpath(os.path.join(updates_dir, msi_name))
        if os.path.dirname(msi_dst) != os.path.realpath(updates_dir):
            raise HTTPException(status_code=400, detail="geçersiz MSI adı")
        shutil.copyfile(msi_src, msi_dst)
        with open(mpath, "rb") as f:
            manifest_b64 = base64.b64encode(f.read()).decode("ascii")
        with open(spath, "r", encoding="utf-8") as f:
            sig = f.read().strip()
        msg = {"action": "update_agent", "manifest": manifest_b64, "manifest_sig": sig}

        targets = await _resolve_targets(data)
        online = sorted(t for t in targets if t in manager.active_agents)
        offline = sorted(t for t in targets if t not in manager.active_agents)
        for pc in online:
            await manager.send_command(msg, pc)
        await add_audit_log("*", "deploy_update", "İmzalı güncelleme dağıtıldı: %s" % version,
                            {"version": version, "msi": msi_name, "dispatched": online, "offline": offline})
        return {"ok": True, "version": version, "msi": msi_name,
                "dispatched": online, "skipped_offline": offline}

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
        status = None
        try:
            with open(os.path.join(SELFUPDATE_DIR, "deploy-status.json"), "r", encoding="utf-8") as f:
                status = json.load(f)
        except Exception:
            status = None
        return {
            "configured": _selfupdate_configured(),
            "pending": os.path.exists(os.path.join(SELFUPDATE_DIR, "deploy-request.json")),
            "status": status,
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
        payload = {
            "requested_by": auth.get("sub"),
            "requested_at": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
            "target": "origin/main",
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
                            {"target": "origin/main"})
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
        # send_command çevrimdışıysa no-op; online durumunu ayrıca bildiriyoruz. Kapatma isteği
        # kalıcı kaydedildi, ajan sonra bağlanınca /ws/agent 'capabilities' handler'ı uygular.
        online = pc in manager.active_agents
        await manager.send_command(msg, pc)
        applied = {k: msg[k] for k in msg if k != "action"}
        await add_audit_log(pc, "set_capabilities", "Yetenek politikası gönderildi", applied)
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
