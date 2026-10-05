"""Sistem uçlarının ortak parçaları: yollar, çalışan sürüm, GitHub sorguları, sürüm karşılaştırma ve istek modelleri.

Uç modülleri buradaki adları çağrı anında `common.<ad>` diye okur. Eski `system_routes` modülü artık bu modülün
kendisidir (Backend/system_routes.py): eski içe aktarmalar ve testler adları onun üzerinden okur ve değiştirir (ör.
RELEASES_DIR, _read_version).

GitHub sorguları ÇEVRİMDIŞI-GÜVENLİDİR: kısa zaman aşımlı, event loop'u bloklamaz (thread'de urllib), başarısız olursa
None döner ve hiçbir zaman hata fırlatmaz. Ek bağımlılık yoktur (internetsiz okullarda pip gerektirmez). Son sürüm
saatte bir önbelleğe alınır.
"""
import asyncio
import json
import os
import re
import time
import urllib.request
from typing import Callable, Dict, List, Optional

from pydantic import field_validator

from pops import agent_version as agent_version_mod
from pops.config import BASE_DIR  # Backend/ (canlıda uygulama dizini): VERSION, keys/ ve releases/ buna göre
from pops.models import StrictInput, TargetMode, upper_mode


class EnrollTokenInput(StrictInput):
    lab_name: Optional[str] = None
    note: Optional[str] = None
    ttl_hours: int = 72
    max_uses: int = 1


class DeployUpdateInput(StrictInput):
    target_mode: TargetMode = "PC"
    targets: List[str] = []

    _mode = field_validator("target_mode", mode="before")(upper_mode)


class EnforceInput(StrictInput):
    enabled: bool


class CapabilityInput(StrictInput):
    pc_name: str
    terminal_enabled: Optional[bool] = None   # yalnızca False anlamlı (fail-safe kapatma)
    vision_enabled: Optional[bool] = None


class ReenrollInput(StrictInput):
    pc_name: str
    allow: bool = True


class FetchReleaseInput(StrictInput):
    tag: Optional[str] = None   # boşsa GitHub'daki son release
    force: bool = False


class Deps:
    """server.py'nin enjekte ettiği bağımlılıklar (bu paket server.py'yi içe aktarmaz; testler sahtelerini verir)."""

    def __init__(self, require_admin: Callable, require_superadmin: Callable, execute_query: Callable, manager,
                 updates_dir: str, add_audit_log: Callable):
        self.require_admin = require_admin
        self.require_superadmin = require_superadmin
        self.execute_query = execute_query
        self.manager = manager
        self.updates_dir = updates_dir
        self.add_audit_log = add_audit_log


def build_router(*args, **kwargs):
    """pops.routers.system.build_router (geri uyum: system_routes.build_router)."""
    from pops.routers.system import build_router as _build  # paket bu modülü içe aktarır; döngü olmasın diye burada

    return _build(*args, **kwargs)


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
_DOWNLOAD_TIMEOUT = 30.0


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


async def staged_release(d: Deps) -> Optional[dict]:
    """Yüklenip doğrulanmış (staged) imzalı release'in manifest'i; yoksa None."""
    rows = await d.execute_query(
        "SELECT value FROM global_settings WHERE key='verified_release_manifest'", fetch=True)
    if rows and rows[0]["value"]:
        try:
            return json.loads(rows[0]["value"])
        except Exception:
            return None
    return None
