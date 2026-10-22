"""İmzalı release manifest'inin doğrulanması (Windows ReleaseVerifier ile aynı kurallar).

Manifest'i tools/sign_release.py üretir; imza manifest.json'un tam baytları üzerindeki ham ed25519 imzasıdır
(base64). Açık anahtar keys/pops_release_ed25519.pub.pem'in ham 32 baytıdır ve kodun içindedir: sunucu ele geçirilse
bile başka anahtarla imzalanmış paket kurulmaz.

Linux paketi: manifest'te adı tam olarak "pops-agent_<manifest sürümü>_all.deb" olan TEK bir artefakt. Dosya adı
POps sürümünü olduğu gibi taşır (GitHub release dosya adlarındaki "~" işaretini değiştirir); paketin içindeki
Debian sürümü ön sürüm doğru sıralansın diye "~" ile yazılır (0.1.22-alpha → 0.1.22~alpha).
"""

import base64
import json
import re
from typing import List, Optional, Tuple

from cryptography.exceptions import InvalidSignature
from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PublicKey

PUBLIC_KEY_B64 = "MQqVu9JHcHvpj0gI8FcrrtrqrCxSe4iAqKR2L/bZYuQ="
MANIFEST_SCHEMA = "pops-manifest/1"
DEB_NAME_RE = re.compile(r"^pops-agent_[A-Za-z0-9.+~-]+_all\.deb$")
SHA256_RE = re.compile(r"^[0-9a-f]{64}$")
MAX_PACKAGE_BYTES = 64 * 1024 * 1024


class ManifestError(Exception):
    pass


class Artifact:
    def __init__(self, name: Optional[str], sha256: Optional[str], size: int):
        self.name = name
        self.sha256 = sha256
        self.size = size


class Manifest:
    def __init__(self, version: str, tag: Optional[str], released_at: int, artifacts: List[Artifact]):
        self.version = version
        self.tag = tag
        self.released_at = released_at
        self.artifacts = artifacts


def verify_signature(manifest: bytes, signature_b64: Optional[str], public_key_b64: str = PUBLIC_KEY_B64) -> bool:
    try:
        sig = base64.b64decode((signature_b64 or "").strip(), validate=True)
        key = base64.b64decode(public_key_b64, validate=True)
    except (ValueError, TypeError):
        return False
    if manifest is None or len(sig) != 64 or len(key) != 32:
        return False
    try:
        Ed25519PublicKey.from_public_bytes(key).verify(sig, manifest)
        return True
    except (InvalidSignature, ValueError):
        return False


def _str(obj: dict, key: str) -> Optional[str]:
    v = obj.get(key) if isinstance(obj, dict) else None
    return v if isinstance(v, str) else None


def parse(manifest: bytes) -> Manifest:
    """Yalnızca imzası doğrulanmış baytlar için. Beklenen alanlar yoksa ManifestError."""
    try:
        root = json.loads(manifest.decode("utf-8"))
    except (ValueError, UnicodeDecodeError) as exc:
        raise ManifestError("manifest JSON değil: %s" % exc)
    if not isinstance(root, dict):
        raise ManifestError("manifest bir JSON nesnesi değil")
    if _str(root, "schema") != MANIFEST_SCHEMA:
        raise ManifestError("bilinmeyen manifest şeması: %r" % root.get("schema"))
    version = _str(root, "version")
    if parse_semver(version) is None:
        raise ManifestError("manifest sürümü geçersiz: %r" % version)
    arts = root.get("artifacts")
    if not isinstance(arts, list):
        raise ManifestError("manifest'te artifacts yok")
    artifacts = []
    for a in arts:
        size = a.get("size") if isinstance(a, dict) else None
        sha = _str(a, "sha256")
        artifacts.append(Artifact(_str(a, "name"), sha.lower() if sha else None,
                                  size if isinstance(size, int) and not isinstance(size, bool) else -1))
    released = root.get("released_at")
    return Manifest(version, _str(root, "tag"), released if isinstance(released, int) else 0, artifacts)


# ── SemVer 2.0 önceliği: "v" öneki ve "+derleme" yok sayılır; 0.1.2-alpha < 0.1.2 < 0.1.3-alpha ──
def _numeric(s: str) -> bool:
    return 0 < len(s) <= 18 and s.isascii() and s.isdigit()


def parse_semver(version: Optional[str]) -> Optional[Tuple[Tuple[int, int, int], List[str]]]:
    if not version or not version.strip():
        return None
    v = version.strip()
    if v[:1] in ("v", "V"):
        v = v[1:]
    v = v.split("+", 1)[0]
    core, dash, pre = v.partition("-")
    parts = core.split(".")
    if len(parts) != 3 or not all(_numeric(p) for p in parts):
        return None
    pres = pre.split(".") if dash else []
    if any(not p or not re.match(r"^[A-Za-z0-9-]+$", p) for p in pres):
        return None
    return (int(parts[0]), int(parts[1]), int(parts[2])), pres


def compare_versions(a: str, b: str) -> int:
    pa, pb = parse_semver(a), parse_semver(b)
    if pa is None or pb is None:
        raise ValueError("geçersiz sürüm: %r / %r" % (a, b))
    if pa[0] != pb[0]:
        return 1 if pa[0] > pb[0] else -1
    prea, preb = pa[1], pb[1]
    if not prea or not preb:
        return (len(preb) > 0) - (len(prea) > 0)
    for x, y in zip(prea, preb):
        nx, ny = _numeric(x), _numeric(y)
        if nx and ny:
            c = (int(x) > int(y)) - (int(x) < int(y))
        elif nx:
            c = -1
        elif ny:
            c = 1
        else:
            c = (x > y) - (x < y)
        if c:
            return c
    return (len(prea) > len(preb)) - (len(prea) < len(preb))


def same_version(a: Optional[str], b: Optional[str]) -> bool:
    try:
        return bool(a and b) and compare_versions(a, b) == 0
    except ValueError:
        return (a or "").strip().lstrip("vV") == (b or "").strip().lstrip("vV")


def deb_version(version: str) -> str:
    """POps sürümü → Debian sürümü: ön sürüm ayıracı "~" (dpkg'da 0.1.22~alpha < 0.1.22)."""
    v = version.strip().lstrip("vV")
    return v.replace("-", "~", 1)


def from_deb_version(version: str) -> str:
    return (version or "").strip().replace("~", "-", 1)


def deb_name(version: str) -> str:
    return "pops-agent_%s_all.deb" % version.strip().lstrip("vV")


def select_deb(manifest: Manifest) -> Artifact:
    """Tek bir Linux paketi; adı manifest sürümüyle eşleşmeli, özeti ve boyutu geçerli olmalı."""
    debs = [a for a in manifest.artifacts if a.name and DEB_NAME_RE.match(a.name)]
    if len(debs) != 1:
        raise ManifestError("manifest'te tek bir Linux ajan paketi (.deb) bekleniyordu, %d bulundu" % len(debs))
    deb = debs[0]
    if deb.name != deb_name(manifest.version):
        raise ManifestError("paket adı (%s) manifest sürümüyle (%s) uyuşmuyor" % (deb.name, manifest.version))
    if not deb.sha256 or not SHA256_RE.match(deb.sha256) or deb.size <= 0 or deb.size > MAX_PACKAGE_BYTES:
        raise ManifestError("%s: manifest'teki özet ya da boyut geçersiz" % deb.name)
    return deb
