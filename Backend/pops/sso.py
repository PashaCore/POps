"""Panel girişi için dizin (LDAP / Active Directory) ve OpenID Connect: ayarlar, grup → rol eşlemesi, hesabın
yerel kullanıcıya bağlanması, kısa ömürlü akış kayıtları.

Protokoller pops/sso_ldap.py ve pops/sso_oidc.py'de; uçlar routers/sso.py ile routers/auth.py'dedir (LDAP girişi
şifre formundan, aynı /api/admin/login ucundan geçer). Kurallar (D-24):
  * Yerel hesaplar kalır ve dizine hiç sorulmaz. Dizine ya da sağlayıcıya ulaşılamasa da yerel süper admin girer;
    ilk yerel süper admin dizin/OIDC hesabına çevrilemez.
  * Dizin/OIDC hesabının yerel şifresi yoktur (password_hash '!sso'). Kimlik external_id ile bağlanır; aynı adlı
    yerel hesap kendiliğinden bağlanmaz (dizindeki "admin" yerel "admin"i ele geçiremez).
  * Rol ve sayfalar her girişte eşlemeden yeniden yazılır; eşlenen bir grupta olmayan giriş yapamaz.
  * Kurum birimi kapsamı (D-25): eşleme org_scope verebilir (birim kimlikleri ya da "all"). Verilmezse YENİ hesap
    en dar kapsamı alır: birim varsa boş kapsam (hiçbir cihaz; süper admin birim seçer), birim yoksa kapsamsız.
  * Gizli değerler (hizmet hesabı şifresi, istemci sırrı) 2FA anahtarlarıyla aynı anahtarla şifreli durur.
"""

import hashlib
import ipaddress
import json
import logging
import re
import secrets
import ssl
from typing import Iterable, List, Optional, Tuple
from urllib.parse import urlsplit

from pops import config, secretbox
from pops.audit import add_audit_log
from pops.db import execute_query

log = logging.getLogger("pops.sso")

KINDS = ("ldap", "oidc")
ROLE_RANK = {"viewer": 1, "admin": 2, "superadmin": 3}
# bcrypt değil: _check_password bunu hiçbir şifreyle eşleştirmez
SSO_PASSWORD_HASH = "!sso"
CALLBACK_SUFFIX = "/api/auth/oidc/callback"
OIDC_STATE_TTL = 600
TICKET_TTL = 60
_PAGE_RE = re.compile(r"^[a-z_]{1,32}$")
_ATTR_RE = re.compile(r"^[A-Za-z][A-Za-z0-9-]{0,63}$")
_CLAIM_RE = re.compile(r"^[A-Za-z0-9_:/.-]{1,64}$")
_HOST_RE = re.compile(r"^[A-Za-z0-9._-]{1,253}$")
_DOMAIN_RE = re.compile(r"^(?=.{1,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}$")
# Dönüş adresi yalnızca panelin kendi yolu olabilir: "/devices", "/tasks?tab=jobs". Şema, "//" ve "\" yok.
_NEXT_RE = re.compile(r"/(?![/\\])[A-Za-z0-9_\-/.]*(\?[A-Za-z0-9_\-=&%.]*)?")

LDAP_DEFAULTS = {
    "host": "", "port": 636, "security": "ldaps", "base_dn": "", "bind_dn": "",
    "user_filter": "(sAMAccountName={username})", "username_attribute": "sAMAccountName",
    "group_base_dn": "", "group_filter": "(|(member={user_dn})(uniqueMember={user_dn}))",
    "group_map": [], "ca_pem": "", "timeout": 5, "allow_insecure_for_tests": False,
}
OIDC_DEFAULTS = {
    "display_name": "", "issuer": "", "client_id": "", "redirect_uri": "", "scopes": "openid email profile",
    "username_claim": "email", "groups_claim": "groups", "group_map": [], "allowed_domains": [],
    "default_role": "", "default_pages": [], "default_org_scope": None, "ca_pem": "", "allow_insecure_for_tests": False,
}
DEFAULTS = {"ldap": LDAP_DEFAULTS, "oidc": OIDC_DEFAULTS}
# Değişince kayıtlı sır yeniden girilmeden kullanılmaz: çalınmış bir süper admin oturumu sırrı başka bir sunucuya,
# başka bir CA'nın sertifikasıyla ya da şifresiz bağlantıyla gönderemesin
SECRET_BOUND = {
    "ldap": ("host", "port", "bind_dn", "security", "ca_pem", "allow_insecure_for_tests"),
    "oidc": ("issuer", "client_id", "ca_pem", "allow_insecure_for_tests"),
}


class SsoRefused(Exception):
    """Giriş reddi: code panelin gösterdiği iletinin anahtarıdır (login.php), message sunucu yanıtıdır."""

    def __init__(self, code: str, message: str, status: int = 403):
        super().__init__(message)
        self.code = code
        self.message = message
        self.status = status


# ── Doğrulama (saf işlevler; birim testleri) ─────────────────────────────────────


def safe_next(value: Optional[str]) -> Optional[str]:
    """Girişten sonra gidilecek panel yolu ya da None (açık yönlendirme yok: yalnızca "/" ile başlayan yerel yol)."""
    if not value:
        return None
    if len(value) > 256 or not _NEXT_RE.fullmatch(value) or ".." in value:
        return None
    return value


def insecure_allowed(cfg: dict) -> bool:
    """Şifresiz LDAP / http sağlayıcı: ayarda bayrak VE sunucu ortamında POPS_SSO_ALLOW_INSECURE_FOR_TESTS=1."""
    return bool(cfg.get("allow_insecure_for_tests")) and config.SSO_ALLOW_INSECURE_FOR_TESTS


def _check_insecure_flag(c: dict) -> None:
    if c["allow_insecure_for_tests"] and not config.SSO_ALLOW_INSECURE_FOR_TESTS:
        raise ValueError("allow_insecure_for_tests yalnızca testler içindir (sunucu "
                         "POPS_SSO_ALLOW_INSECURE_FOR_TESTS=1 ile başlatılmadı).")


def is_loopback_host(host: str) -> bool:
    if host == "localhost":
        return True
    try:
        return ipaddress.ip_address(host.strip("[]")).is_loopback
    except ValueError:
        return False


def _balanced_filter(f: str) -> bool:
    if not (f.startswith("(") and f.endswith(")")):
        return False
    depth = 0
    for ch in f:
        depth += 1 if ch == "(" else -1 if ch == ")" else 0
        if depth < 0:
            return False
    return depth == 0


def _check_pages(pages: Iterable[str], what: str) -> List[str]:
    out = []
    for p in pages or []:
        if not isinstance(p, str) or not _PAGE_RE.match(p):
            raise ValueError("%s: geçersiz sayfa adı." % what)
        if p not in out:
            out.append(p)
    return out


ALL_UNITS = "all"


def _check_scope(value, role: Optional[str], what: str):
    """Eşlemenin kurum birimi kapsamı: None (ayarlanmadı), "all" (açıkça bütün kurum) ya da birim kimlikleri.
    Süper admin her zaman kapsamsızdır; onun eşlemesinde kapsam tutulmaz."""
    if value is None or role == "superadmin":
        return None
    if value == ALL_UNITS:
        return ALL_UNITS
    if not isinstance(value, list) or not value:
        raise ValueError("%s: kapsam birim listesi ya da \"all\" olmalı." % what)
    try:
        ids = sorted({int(i) for i in value})
    except (TypeError, ValueError):
        raise ValueError("%s: geçersiz birim." % what)
    if any(i <= 0 for i in ids):
        raise ValueError("%s: geçersiz birim." % what)
    return ids


def _check_group_map(items: list) -> list:
    out = []
    for m in items or []:
        group = " ".join(str(m.get("group") or "").split())
        if not group:
            raise ValueError("Grup eşlemesinde grup boş olamaz.")
        role = m.get("role")
        if role not in ROLE_RANK:
            raise ValueError("Grup eşlemesinde geçersiz rol.")
        pages = _check_pages(m.get("pages"), "Grup eşlemesi")
        out.append({"group": group, "role": role, "pages": [] if role == "superadmin" else pages,
                    "org_scope": _check_scope(m.get("org_scope"), role, "Grup eşlemesi")})
    return out


def scope_units(cfg: dict) -> List[int]:
    """Ayarlarda geçen bütün birim kimlikleri (kaydederken var olup olmadıkları denetlenir)."""
    out = set()
    for v in [m.get("org_scope") for m in cfg.get("group_map") or []] + [cfg.get("default_org_scope")]:
        if isinstance(v, list):
            out.update(v)
    return sorted(out)


def check_ca_pem(pem: str) -> str:
    pem = (pem or "").strip()
    if not pem:
        return ""
    if "-----BEGIN CERTIFICATE-----" not in pem:
        raise ValueError("CA sertifikası PEM biçiminde olmalı (-----BEGIN CERTIFICATE-----).")
    try:
        ssl.create_default_context(cadata=pem)
    except (ssl.SSLError, ValueError):
        raise ValueError("CA sertifikası okunamadı.")
    return pem + "\n"


def clean_ldap(cfg: dict) -> dict:
    """Panelden gelen LDAP ayarları: tür denetimi pydantic'te, anlam denetimi burada (ValueError → 400)."""
    c = {k: cfg.get(k, v) for k, v in LDAP_DEFAULTS.items()}
    for k in ("host", "base_dn", "bind_dn", "user_filter", "username_attribute", "group_base_dn", "group_filter"):
        c[k] = str(c[k] or "").strip()
    c["allow_insecure_for_tests"] = bool(c["allow_insecure_for_tests"])
    _check_insecure_flag(c)
    if c["security"] not in ("ldaps", "starttls", "plain"):
        raise ValueError("Bağlantı türü ldaps ya da starttls olmalı.")
    if c["security"] == "plain" and not insecure_allowed(c):
        raise ValueError("Şifresiz LDAP kabul edilmez; LDAPS ya da StartTLS kullanın.")
    if c["host"] and not _HOST_RE.match(c["host"]):
        raise ValueError("Sunucu adı geçersiz (yalnızca ad ya da IP adresi; ldap:// yazmayın).")
    if not 1 <= int(c["port"]) <= 65535:
        raise ValueError("Port 1 ile 65535 arasında olmalı.")
    c["port"] = int(c["port"])
    c["timeout"] = max(1, min(30, int(c["timeout"] or 5)))
    if "{username}" not in c["user_filter"] or not _balanced_filter(c["user_filter"]):
        raise ValueError("Kullanıcı filtresi parantezli bir LDAP filtresi olmalı ve {username} içermeli.")
    if c["group_filter"] and not _balanced_filter(c["group_filter"]):
        raise ValueError("Grup filtresi parantezli bir LDAP filtresi olmalı.")
    if not _ATTR_RE.match(c["username_attribute"]):
        raise ValueError("Kullanıcı adı özniteliği geçersiz.")
    c["group_map"] = _check_group_map(c["group_map"])
    c["ca_pem"] = check_ca_pem(c["ca_pem"])
    return c


def callback_ok(uri: str) -> bool:
    try:
        u = urlsplit(uri)
    except ValueError:
        return False
    if u.query or u.fragment or u.username or u.password or not u.hostname:
        return False
    if not u.path.endswith(CALLBACK_SUFFIX):
        return False
    # Yönlendirme adresi https olmalı; yalnızca bu makinedeki (loopback) bir panel http kullanabilir
    return u.scheme == "https" or (u.scheme == "http" and is_loopback_host(u.hostname))


def clean_oidc(cfg: dict) -> dict:
    c = {k: cfg.get(k, v) for k, v in OIDC_DEFAULTS.items()}
    for k in ("display_name", "issuer", "client_id", "redirect_uri", "scopes", "username_claim", "groups_claim"):
        c[k] = " ".join(str(c[k] or "").split()) if k in ("display_name", "scopes") else str(c[k] or "").strip()
    c["allow_insecure_for_tests"] = bool(c["allow_insecure_for_tests"])
    _check_insecure_flag(c)
    if c["issuer"]:
        u = urlsplit(c["issuer"])
        if u.scheme not in ("https", "http") or not u.hostname or u.query or u.fragment:
            raise ValueError("Sağlayıcı adresi (issuer) https:// ile başlayan bir adres olmalı.")
        if u.scheme == "http" and not insecure_allowed(c):
            raise ValueError("Sağlayıcı adresi (issuer) https olmalı.")
    if c["redirect_uri"] and not callback_ok(c["redirect_uri"]):
        raise ValueError("Dönüş adresi https://<panel adresi>%s biçiminde olmalı." % CALLBACK_SUFFIX)
    scopes = c["scopes"].split()
    if "openid" not in scopes:
        scopes.insert(0, "openid")
    if any(not re.match(r"^[\x21\x23-\x5b\x5d-\x7e]{1,64}$", s) for s in scopes):
        raise ValueError("Kapsamlar (scopes) geçersiz.")
    c["scopes"] = " ".join(scopes)
    if not _CLAIM_RE.match(c["username_claim"]):
        raise ValueError("Kullanıcı adı talebi (claim) geçersiz.")
    if c["groups_claim"] and not _CLAIM_RE.match(c["groups_claim"]):
        raise ValueError("Grup talebi (claim) geçersiz.")
    domains = []
    for d in c["allowed_domains"] or []:
        d = str(d).strip().lower().lstrip("@")
        if not _DOMAIN_RE.match(d):
            raise ValueError("Geçersiz e-posta alan adı: %s" % d[:80])
        if d not in domains:
            domains.append(d)
    c["allowed_domains"] = domains
    if c["default_role"] not in ("", "viewer", "admin"):
        raise ValueError("Varsayılan rol izleyici ya da yönetici olabilir.")
    if c["default_role"] and not domains:
        raise ValueError("Varsayılan rol için en az bir e-posta alan adı girin.")
    c["default_pages"] = _check_pages(c["default_pages"], "Varsayılan sayfalar")
    c["default_org_scope"] = _check_scope(c["default_org_scope"], c["default_role"] or "viewer", "Varsayılan kapsam")
    c["group_map"] = _check_group_map(c["group_map"])
    c["ca_pem"] = check_ca_pem(c["ca_pem"])
    return c


def require_complete(kind: str, c: dict, has_secret: bool) -> None:
    """Etkinleştirmek için gereken alanlar (kapalıyken taslak kaydedilebilir)."""
    if kind == "ldap":
        missing = [n for k, n in (("host", "sunucu"), ("base_dn", "arama kökü (base DN)"),
                                  ("bind_dn", "hizmet hesabı")) if not c.get(k)]
        if c.get("bind_dn") and not has_secret:
            missing.append("hizmet hesabı şifresi")
    else:
        missing = [n for k, n in (("issuer", "sağlayıcı adresi"), ("client_id", "istemci kimliği"),
                                  ("redirect_uri", "dönüş adresi")) if not c.get(k)]
    if missing:
        raise ValueError("Etkinleştirmek için eksik: %s." % ", ".join(missing))


# ── Grup → rol ──────────────────────────────────────────────────────────────────


def normalize_dn(dn: str) -> str:
    """DN karşılaştırması: büyük/küçük harf ve virgül/eşittir çevresindeki boşluklar önemsizdir."""
    s = (dn or "").strip().lower()
    return re.sub(r"\s*([,=+])\s*", r"\1", s)


def map_role(groups: Iterable[str], group_map: list, dn: bool) -> Optional[Tuple[str, List[str], object]]:
    """Kullanıcının gruplarıyla eşleşen en yüksek rol, eşleşen bütün eşlemelerin sayfaları (birleşim) ve kapsamı.
    Kapsam: eşleşen eşlemelerden kapsam verenlerin birleşimi ("all" verilmişse "all"); hiçbiri vermediyse None
    (ayarlanmadı: link_user en dar kapsamı uygular). Eşleşme yoksa None (giriş reddedilir). LDAP'ta gruplar DN
    olarak, OIDC'de büyük/küçük harf duyarsız karşılaştırılır."""
    norm = normalize_dn if dn else (lambda g: (g or "").strip().casefold())
    have = {norm(g) for g in groups if isinstance(g, str) and g.strip()}
    best = None
    pages: List[str] = []
    scopes = []
    for m in group_map or []:
        if norm(m.get("group", "")) not in have:
            continue
        for p in m.get("pages") or []:
            if p not in pages:
                pages.append(p)
        if best is None or ROLE_RANK[m["role"]] > ROLE_RANK[best]:
            best = m["role"]
        if m.get("org_scope") is not None:
            scopes.append(m["org_scope"])
    if best is None:
        return None
    if best == "superadmin" or ALL_UNITS in scopes:
        scope = ALL_UNITS if best != "superadmin" else None
    else:
        scope = sorted({int(i) for s in scopes for i in s}) if scopes else None
    return best, ([] if best == "superadmin" else sorted(pages)), scope


def valid_username(name: str) -> bool:
    return bool(name) and len(name) <= 128 and name == name.strip() and not name.lower().startswith("token:") \
        and not any(ord(ch) < 32 for ch in name)


# ── Ayarların saklanması ────────────────────────────────────────────────────────


async def load(kind: str, with_secret: bool = True) -> Tuple[bool, dict, Optional[str]]:
    """(etkin mi, ayarlar (varsayılanlarla tamamlanmış), açık sır ya da None)."""
    rows = await execute_query(
        "SELECT enabled, config, secret FROM sso_providers WHERE kind = $1", (kind,), fetch=True)
    if not rows:
        return False, dict(DEFAULTS[kind]), None
    raw = rows[0]["config"]
    cfg = json.loads(raw) if isinstance(raw, str) else dict(raw or {})
    merged = {k: cfg.get(k, v) for k, v in DEFAULTS[kind].items()}
    secret = secretbox.unseal(rows[0]["secret"]) if with_secret and rows[0]["secret"] else None
    return bool(rows[0]["enabled"]), merged, secret


async def public_state(kind: str) -> dict:
    rows = await execute_query(
        "SELECT enabled, config, secret IS NOT NULL AND secret <> '' AS has_secret, updated_by, updated_at "
        "FROM sso_providers WHERE kind = $1", (kind,), fetch=True)
    if not rows:
        return {"enabled": False, **DEFAULTS[kind], "has_secret": False, "updated_by": None, "updated_at": None}
    r = rows[0]
    raw = r["config"]
    cfg = json.loads(raw) if isinstance(raw, str) else dict(raw or {})
    return {"enabled": bool(r["enabled"]), **{k: cfg.get(k, v) for k, v in DEFAULTS[kind].items()},
            "has_secret": bool(r["has_secret"]), "updated_by": r["updated_by"], "updated_at": r["updated_at"]}


def _audit_value(key: str, value):
    # Sertifikanın kendisi değil özeti yazılır; sırlar hiç yazılmaz
    if key == "ca_pem":
        return hashlib.sha256(value.encode()).hexdigest()[:16] if value else ""
    return value


async def save(kind: str, enabled: bool, cfg: dict, secret: Optional[str], by: str) -> dict:
    """Ayarları yazar ve denetim kaydına geçirir. secret: None = kayıtlı kalır, "" = silinir, metin = yenisi.
    Sunucu/sağlayıcı değişip sır yeniden girilmediyse reddedilir (kayıtlı sır başka bir adrese gönderilmesin)."""
    old_enabled, old_cfg, _ = await load(kind, with_secret=False)
    rows = await execute_query("SELECT secret FROM sso_providers WHERE kind = $1", (kind,), fetch=True)
    old_sealed = rows[0]["secret"] if rows else None
    if secret is None and old_sealed and any(old_cfg.get(k) != cfg.get(k) for k in SECRET_BOUND[kind]):
        raise ValueError("Sunucu ya da hizmet hesabı değişti: kayıtlı şifre kullanılmaz, şifreyi yeniden girin."
                         if kind == "ldap" else
                         "Sağlayıcı ya da istemci değişti: kayıtlı istemci sırrı kullanılmaz, sırrı yeniden girin.")
    sealed = old_sealed if secret is None else (secretbox.seal(secret) if secret else None)
    units = scope_units(cfg)
    if units:
        known = await execute_query("SELECT id FROM org_units WHERE id = ANY($1::int[])", (units,), fetch=True)
        missing = sorted(set(units) - {r["id"] for r in known or []})
        if missing:
            raise ValueError("Bilinmeyen birim: %s" % ", ".join(str(i) for i in missing))
    if enabled:
        require_complete(kind, cfg, bool(sealed))
    await execute_query(
        "INSERT INTO sso_providers (kind, enabled, config, secret, updated_by, updated_at) "
        "VALUES ($1, $2, $3::jsonb, $4, $5, NOW()) ON CONFLICT (kind) DO UPDATE SET enabled = $2, config = $3::jsonb, "
        "secret = $4, updated_by = $5, updated_at = NOW()",
        (kind, enabled, json.dumps(cfg), sealed, by),
    )
    changed = sorted(k for k in cfg if cfg.get(k) != old_cfg.get(k))
    ended = 0
    if old_enabled and not enabled:
        # Sağlayıcı kapatılınca onun hesaplarının açık oturumları da kapanır (yeniden açılınca yeniden girerler)
        rows = await execute_query(
            "UPDATE users SET token_version = token_version + 1 WHERE auth_source = $1 RETURNING id", (kind,),
            fetch=True)
        ended = len(rows or [])
    await add_audit_log("*", "sso_settings", "Kimlik sağlayıcısı ayarları değişti: %s" % kind.upper(), {
        "kind": kind, "enabled": enabled, "was_enabled": old_enabled, "changed": changed,
        "before": {k: _audit_value(k, old_cfg.get(k)) for k in changed},
        "after": {k: _audit_value(k, cfg.get(k)) for k in changed},
        "secret_changed": secret is not None and sealed != old_sealed, "sessions_ended": ended, "by": by,
    })
    return await public_state(kind)


def secret_for_test(kind: str, form: dict, stored_cfg: dict, stored_secret: Optional[str],
                    typed: Optional[str]) -> Optional[str]:
    """"Bağlantıyı sına": yazılmış sır varsa o; yoksa kayıtlı sır yalnızca sunucu ve hesap aynıysa kullanılır."""
    if typed is not None:
        return typed
    if all(form.get(k) == stored_cfg.get(k) for k in SECRET_BOUND[kind]):
        return stored_secret
    return None


# ── Kısa ömürlü akışlar ve biletler ─────────────────────────────────────────────


def _h(value: str) -> str:
    return hashlib.sha256(value.encode("utf-8")).hexdigest()


async def put_flow(kind: str, value: str, data: dict, ttl: int) -> None:
    await execute_query("DELETE FROM sso_flows WHERE expires_at < NOW()")
    await execute_query(
        "INSERT INTO sso_flows (id_hash, kind, data, expires_at) "
        "VALUES ($1, $2, $3::jsonb, NOW() + make_interval(secs => $4))",
        (_h(value), kind, json.dumps(data), float(ttl)),
    )


async def take_flow(kind: str, value: str) -> Optional[dict]:
    """Kaydı okur ve siler (bir kez geçer); süresi dolmuşsa None."""
    if not value or len(value) > 256:
        return None
    rows = await execute_query(
        "DELETE FROM sso_flows WHERE id_hash = $1 AND kind = $2 RETURNING data, expires_at > NOW() AS live",
        (_h(value), kind), fetch=True,
    )
    if not rows or not rows[0]["live"]:
        return None
    raw = rows[0]["data"]
    return json.loads(raw) if isinstance(raw, str) else dict(raw)


def binding_hash(binding: str) -> str:
    return _h(binding)


async def issue_ticket(user: dict, source: str, binding: str, next_path: Optional[str]) -> str:
    """Geri dönüşten sonra panelin (login.php) oturumu açması için tek kullanımlık bilet (60 sn). Akışı başlatan
    tarayıcının PHP oturumundaki bağa (binding) bağlıdır: başkasına gönderilen bilet işe yaramaz."""
    ticket = secrets.token_urlsafe(32)
    await put_flow("ticket", ticket, {
        "user_id": user["id"], "tv": int(user.get("token_version") or 0), "source": source,
        "binding": binding, "next": next_path,
    }, TICKET_TTL)
    return ticket


# ── Hesabın yerel kullanıcıya bağlanması ────────────────────────────────────────

_USER_COLS = ("id, username, role, permissions, totp_enabled, totp_secret, token_version, auth_source, external_id, "
              "org_scope")
_UNSET = object()


async def _wanted_scope(role: str, scope):
    """Eşlemenin istediği kapsam: superadmin ve "all" kapsamsız (None); birim listesi, silinmiş birimler atılarak
    (hepsi silindiyse boş liste: hiçbir şey, kapsamsız DEĞİL); ayarlanmadıysa _UNSET."""
    if role == "superadmin" or scope == ALL_UNITS:
        return None
    if scope is None:
        return _UNSET
    rows = await execute_query("SELECT id FROM org_units WHERE id = ANY($1::int[])", ([int(i) for i in scope],),
                               fetch=True)
    have = {r["id"] for r in rows or []}
    return [int(i) for i in scope if int(i) in have]


async def _narrowest_scope():
    """Kapsamı ayarlanmamış yeni hesabın kapsamı: birim varsa boş liste (hiçbir cihaz; süper admin birim seçene
    kadar), hiç birim yoksa (kurum birimleri kullanılmıyor) kapsamsız. Kazara kapsamsız hesap açılmaz."""
    rows = await execute_query("SELECT EXISTS (SELECT 1 FROM org_units) AS any", fetch=True)
    return [] if rows and rows[0]["any"] else None


async def link_user(source: str, external_id: str, username: str, role: str, pages: List[str],
                    scope=None) -> dict:
    """Dizin/OIDC kimliğinin yerel kaydı: external_id ile bulunur; yoksa aynı adlı, henüz bağlanmamış aynı kaynaklı
    kayda bağlanır (süper adminin önceden açtığı hesap); o da yoksa oluşturulur. Rol ve sayfalar eşlemeden yazılır;
    değiştiyse token_version artar (eski oturumlar kapanır). Yerel ya da başka kişiye bağlı aynı ad: reddedilir.
    scope (map_role'ün üçüncü değeri): ayarlanmışsa her girişte yazılır; ayarlanmamışsa yeni hesap en dar kapsamı
    alır, var olan hesabın kapsamı (süper adminin panelde verdiği) değişmez."""
    perms = json.dumps(pages)
    wanted = await _wanted_scope(role, scope)
    rows = await execute_query(
        "SELECT %s FROM users WHERE auth_source = $1 AND external_id = $2" % _USER_COLS,
        (source, external_id), fetch=True)
    created = False
    if rows:
        u = rows[0]
    else:
        if not valid_username(username):
            raise SsoRefused("conflict", "Hesabın kullanıcı adı panelde kullanılamaz.")
        same = await execute_query(
            "SELECT %s FROM users WHERE lower(username) = lower($1)" % _USER_COLS, (username,), fetch=True)
        if len(same) > 1 or (same and (same[0]["auth_source"] != source or same[0]["external_id"])):
            log.warning("dizin hesabı yerel kayda bağlanmadı: aynı ad başka bir hesapta",
                        extra={"source": source, "user": username})
            raise SsoRefused("conflict", "Bu kullanıcı adı panelde başka bir hesaba ait; yöneticinize başvurun.")
        if same:
            u = (await execute_query(
                "UPDATE users SET external_id = $1 WHERE id = $2 AND external_id IS NULL RETURNING %s" % _USER_COLS,
                (external_id, same[0]["id"]), fetch=True) or [None])[0]
            if not u:
                raise SsoRefused("conflict", "Hesap bağlanamadı; yeniden deneyin.")
        else:
            new_scope = await _narrowest_scope() if wanted is _UNSET else wanted
            try:
                u = (await execute_query(
                    "INSERT INTO users (username, password_hash, role, permissions, auth_source, external_id, "
                    "org_scope) VALUES ($1, $2, $3, $4, $5, $6, $7) RETURNING %s" % _USER_COLS,
                    (username, SSO_PASSWORD_HASH, role, perms, source, external_id, new_scope), fetch=True))[0]
            except Exception as exc:  # aynı anda iki ilk giriş: benzersizlik ihlali
                log.warning("dizin hesabı oluşturulamadı", extra={"user": username, "error": type(exc).__name__})
                raise SsoRefused("conflict", "Hesap oluşturulamadı; yeniden deneyin.")
            created = True
            await add_audit_log("*", "sso_user_created", "Dizin/OIDC hesabı ilk girişte oluşturuldu: %s" % username, {
                "user": username, "source": source, "role": role, "pages": pages, "org_scope": new_scope})
    if not created:
        old_scope = list(u["org_scope"]) if u["org_scope"] is not None else None
        if wanted is not _UNSET:
            new_scope = wanted
        elif u["role"] == "superadmin" and role != "superadmin":
            # Süper adminlikten düşen hesap ayarlanmamış kapsamla bütün kuruma kalmasın
            new_scope = await _narrowest_scope()
        else:
            new_scope = old_scope
        if u["role"] != role or (u["permissions"] or "[]") != perms or new_scope != old_scope:
            before = {"role": u["role"], "pages": u["permissions"], "org_scope": old_scope}
            u = (await execute_query(
                "UPDATE users SET role = $1, permissions = $2, org_scope = $4, token_version = token_version + 1 "
                "WHERE id = $3 RETURNING %s" % _USER_COLS, (role, perms, u["id"], new_scope), fetch=True))[0]
            await add_audit_log("*", "sso_role_synced", "Dizin/OIDC grubundan rol güncellendi: %s" % u["username"], {
                "user": u["username"], "source": source, "before": before,
                "after": {"role": role, "pages": perms, "org_scope": new_scope}})
    return u


async def revoke_sessions(user_id: Optional[int], reason: str) -> None:
    """Dizinde kapatılan, silinen ya da grubu kalmayan hesabın açık oturumları kapanır (yeniden denediğinde)."""
    if user_id is None:
        return
    await execute_query("UPDATE users SET token_version = token_version + 1 WHERE id = $1", (user_id,))
    log.info("dizin hesabının oturumları kapatıldı", extra={"user_id": user_id, "reason": reason})
