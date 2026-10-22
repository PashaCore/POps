"""OpenID Connect: yetkilendirme kodu akışı (PKCE S256, state, nonce) ve kimlik jetonunun doğrulanması.

Ek kütüphane yok: HTTP standart kütüphaneyle (urllib, eşzamanlı; çağıran asyncio.to_thread ile çalıştırır), imza
PyJWT + cryptography ile. Kimlik jetonu sağlayıcının JWKS anahtarlarıyla doğrulanır; yalnızca asimetrik algoritmalar
kabul edilir (HS*, "none" asla). iss (keşif belgesindeki issuer), aud (istemci kimliği; birden çok aud varsa azp),
exp, iat ve nonce denetlenir. Sağlayıcı adresleri https olmalıdır (allow_insecure_for_tests yalnızca testler için).
Yanıtlar en çok 1 MB okunur, yönlendirme izlenmez, her istek 10 sn ile sınırlıdır. Jetonlar ve kodlar loglanmaz.
"""

import base64
import hashlib
import hmac
import json
import logging
import ssl
import threading
import time
import urllib.error
import urllib.request
from typing import Optional
from urllib.parse import quote, urlencode, urlsplit

import jwt  # PyJWT

from pops import config

log = logging.getLogger("pops.sso.oidc")

ALLOWED_ALGS = ("RS256", "RS384", "RS512", "PS256", "PS384", "PS512", "ES256", "ES384", "ES512", "EdDSA")
_MAX_BYTES = 1024 * 1024
_TIMEOUT = 10
_DISCOVERY_TTL = 3600
_JWKS_TTL = 3600
_JWKS_REFRESH_MIN = 60   # bilinmeyen kid için yeniden indirme en çok bu sıklıkta
_LEEWAY = 60

_cache = {}
_lock = threading.Lock()


class OidcError(Exception):
    """code: login.php'nin göstereceği ileti (unavailable | token | config); message yalnızca loglanır ve
    "Bağlantıyı sına"da gösterilir."""

    def __init__(self, code: str, message: str):
        super().__init__(message)
        self.code = code
        self.message = message


class _NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def _ssl_context(cfg: dict) -> ssl.SSLContext:
    ctx = ssl.create_default_context(cadata=cfg.get("ca_pem") or None)
    ctx.minimum_version = ssl.TLSVersion.TLSv1_2
    return ctx


def check_url(cfg: dict, url: str, what: str) -> str:
    try:
        u = urlsplit(url or "")
    except ValueError:
        u = None
    if not u or not u.hostname or u.scheme not in ("https", "http"):
        raise OidcError("config", "%s geçerli bir adres değil." % what)
    if u.scheme != "https" and not (cfg.get("allow_insecure_for_tests") and config.SSO_ALLOW_INSECURE_FOR_TESTS):
        raise OidcError("config", "%s https olmalı." % what)
    return url


def _request(cfg: dict, url: str, data: Optional[bytes] = None, headers: Optional[dict] = None) -> dict:
    req = urllib.request.Request(url, data=data, method="POST" if data is not None else "GET")
    req.add_header("Accept", "application/json")
    for k, v in (headers or {}).items():
        req.add_header(k, v)
    handlers = [_NoRedirect()]
    if url.startswith("https://"):
        handlers.append(urllib.request.HTTPSHandler(context=_ssl_context(cfg)))
    opener = urllib.request.build_opener(*handlers)
    try:
        with opener.open(req, timeout=_TIMEOUT) as resp:
            raw = resp.read(_MAX_BYTES + 1)
    except urllib.error.HTTPError as exc:
        body = exc.read(4096).decode("utf-8", "replace")
        try:
            err = json.loads(body).get("error", "")
        except (ValueError, AttributeError):
            err = ""
        raise OidcError("token" if data is not None else "unavailable",
                        "Sağlayıcı HTTP %s döndü%s." % (exc.code, (" (%s)" % str(err)[:80]) if err else ""))
    except (urllib.error.URLError, OSError, ssl.SSLError) as exc:
        reason = getattr(exc, "reason", exc)
        text = str(reason)
        if "CERTIFICATE_VERIFY_FAILED" in text or "certificate verify failed" in text:
            raise OidcError("unavailable", "Sağlayıcının TLS sertifikası doğrulanamadı.")
        raise OidcError("unavailable", "Sağlayıcıya ulaşılamadı (%s)." % type(reason).__name__)
    if len(raw) > _MAX_BYTES:
        raise OidcError("unavailable", "Sağlayıcının yanıtı çok büyük.")
    try:
        doc = json.loads(raw)
    except ValueError:
        raise OidcError("unavailable", "Sağlayıcının yanıtı JSON değil.")
    if not isinstance(doc, dict):
        raise OidcError("unavailable", "Sağlayıcının yanıtı beklenen biçimde değil.")
    return doc


def _cached(key: tuple, ttl: int, fetch):
    with _lock:
        hit = _cache.get(key)
        if hit and time.monotonic() - hit[1] < ttl:
            return hit[0]
    value = fetch()
    with _lock:
        _cache[key] = (value, time.monotonic())
    return value


def clear_cache() -> None:
    with _lock:
        _cache.clear()


def discover(cfg: dict, fresh: bool = False) -> dict:
    """Keşif belgesi (/.well-known/openid-configuration), bir saat önbellekte. issuer ayardakiyle aynı olmalı."""
    issuer = check_url(cfg, cfg.get("issuer"), "Sağlayıcı adresi (issuer)").rstrip("/")

    def fetch():
        doc = _request(cfg, issuer + "/.well-known/openid-configuration")
        if str(doc.get("issuer", "")).rstrip("/") != issuer:
            raise OidcError("config", "Keşif belgesindeki issuer ayardaki adresle aynı değil.")
        for k, what in (("authorization_endpoint", "Yetkilendirme adresi"), ("token_endpoint", "Jeton adresi"),
                        ("jwks_uri", "JWKS adresi")):
            check_url(cfg, doc.get(k), what)
        return doc

    key = ("discovery", issuer, cfg.get("ca_pem") or "", bool(cfg.get("allow_insecure_for_tests")))
    if fresh:
        with _lock:
            _cache.pop(key, None)
    return _cached(key, _DISCOVERY_TTL, fetch)


def _b64(data: bytes) -> str:
    return base64.urlsafe_b64encode(data).decode("ascii").rstrip("=")


def pkce_challenge(verifier: str) -> str:
    return _b64(hashlib.sha256(verifier.encode("ascii")).digest())


def authorize_url(cfg: dict, doc: dict, state: str, nonce: str, verifier: str) -> str:
    params = {
        "response_type": "code", "client_id": cfg["client_id"], "redirect_uri": cfg["redirect_uri"],
        "scope": cfg.get("scopes") or "openid", "state": state, "nonce": nonce,
        "code_challenge": pkce_challenge(verifier), "code_challenge_method": "S256",
    }
    ep = doc["authorization_endpoint"]
    return ep + ("&" if urlsplit(ep).query else "?") + urlencode(params)


def exchange_code(cfg: dict, doc: dict, client_secret: Optional[str], code: str, verifier: str) -> str:
    """Kodu kimlik jetonuyla değiştirir (code_verifier ile). İstemci sırrı varsa client_secret_basic (sağlayıcı
    yalnızca client_secret_post destekliyorsa gövdede); yoksa genel (public) istemci, yalnızca PKCE."""
    body = {"grant_type": "authorization_code", "code": code, "redirect_uri": cfg["redirect_uri"],
            "code_verifier": verifier, "client_id": cfg["client_id"]}
    headers = {"Content-Type": "application/x-www-form-urlencoded"}
    if client_secret:
        methods = doc.get("token_endpoint_auth_methods_supported") or ["client_secret_basic"]
        if "client_secret_basic" in methods:
            pair = "%s:%s" % (quote(cfg["client_id"], safe=""), quote(client_secret, safe=""))
            headers["Authorization"] = "Basic " + base64.b64encode(pair.encode("utf-8")).decode("ascii")
        else:
            body["client_secret"] = client_secret
    resp = _request(cfg, doc["token_endpoint"], urlencode(body).encode("ascii"), headers)
    token = resp.get("id_token")
    if not isinstance(token, str) or not token:
        raise OidcError("token", "Sağlayıcı kimlik jetonu (id_token) döndürmedi.")
    return token


def _jwks(cfg: dict, doc: dict, fresh: bool) -> dict:
    uri = doc["jwks_uri"]
    key = ("jwks", uri, cfg.get("ca_pem") or "", bool(cfg.get("allow_insecure_for_tests")))
    if fresh:
        with _lock:
            hit = _cache.get(key)
            if hit and time.monotonic() - hit[1] < _JWKS_REFRESH_MIN:
                return hit[0]
            _cache.pop(key, None)
    return _cached(key, _JWKS_TTL, lambda: _request(cfg, uri))


def _signing_key(cfg: dict, doc: dict, kid: Optional[str], alg: str):
    for fresh in (False, True):
        keys = []
        for k in (_jwks(cfg, doc, fresh).get("keys") or []):
            if not isinstance(k, dict) or k.get("use", "sig") != "sig" or k.get("kty") == "oct":
                continue
            if kid is not None and k.get("kid") != kid:
                continue
            if k.get("alg") and k.get("alg") != alg:
                continue
            keys.append(k)
        if len(keys) == 1 or (keys and kid is not None):
            try:
                return jwt.PyJWK(keys[0], algorithm=alg).key
            except jwt.PyJWTError:
                raise OidcError("token", "Sağlayıcının imza anahtarı okunamadı.")
    raise OidcError("token", "Kimlik jetonunu imzalayan anahtar sağlayıcının JWKS'inde yok.")


def validate_id_token(cfg: dict, doc: dict, id_token: str, nonce: str) -> dict:
    try:
        header = jwt.get_unverified_header(id_token)
    except jwt.PyJWTError:
        raise OidcError("token", "Kimlik jetonu okunamadı.")
    alg = header.get("alg")
    supported = doc.get("id_token_signing_alg_values_supported") or ["RS256"]
    if alg not in ALLOWED_ALGS or alg not in supported:
        raise OidcError("token", "Kimlik jetonunun imza algoritması kabul edilmiyor (%s)." % str(alg)[:16])
    key = _signing_key(cfg, doc, header.get("kid"), alg)
    try:
        claims = jwt.decode(
            id_token, key=key, algorithms=[alg], audience=cfg["client_id"], issuer=doc["issuer"], leeway=_LEEWAY,
            options={"require": ["exp", "iat", "iss", "aud", "sub"]},
        )
    except jwt.ExpiredSignatureError:
        raise OidcError("token", "Kimlik jetonunun süresi dolmuş.")
    except jwt.InvalidAudienceError:
        raise OidcError("token", "Kimlik jetonu bu istemci için verilmemiş (aud).")
    except jwt.InvalidIssuerError:
        raise OidcError("token", "Kimlik jetonunun sağlayıcısı (iss) uyuşmuyor.")
    except jwt.InvalidSignatureError:
        raise OidcError("token", "Kimlik jetonunun imzası geçersiz.")
    except jwt.PyJWTError as exc:
        raise OidcError("token", "Kimlik jetonu geçersiz (%s)." % type(exc).__name__)
    aud = claims.get("aud")
    if isinstance(aud, list) and len(aud) > 1 and claims.get("azp") != cfg["client_id"]:
        raise OidcError("token", "Kimlik jetonu birden çok istemciye verilmiş ve azp bu istemci değil.")
    got = claims.get("nonce")
    if not isinstance(got, str) or not hmac.compare_digest(got.encode("utf-8"), nonce.encode("utf-8")):
        raise OidcError("token", "Kimlik jetonunun nonce değeri bu girişe ait değil.")
    return claims


def identity(cfg: dict, claims: dict) -> dict:
    """Talepler: kullanıcı adı (ayarlı talep), e-posta ve doğrulanmış mı, gruplar, değişmez kimlik (iss|sub)."""
    claim = cfg.get("username_claim") or "email"
    name = claims.get(claim)
    name = name.strip() if isinstance(name, str) else ""
    if claim == "email":
        name = name.lower()
    email = claims.get("email") if isinstance(claims.get("email"), str) else ""
    verified = claims.get("email_verified")
    groups = claims.get(cfg.get("groups_claim") or "") if cfg.get("groups_claim") else None
    if isinstance(groups, str):
        groups = [groups]
    return {
        "username": name,
        "email": email.strip().lower(),
        "email_verified": verified is True or (isinstance(verified, str) and verified.lower() == "true"),
        "groups": [g for g in (groups or []) if isinstance(g, str)],
        "external_id": "%s|%s" % (claims.get("iss"), claims.get("sub")),
    }


def domain_allowed(cfg: dict, ident: dict) -> bool:
    """E-posta alan adı listede mi (yalnızca doğrulanmış e-posta; sağlayıcı email_verified göndermiyorsa grup
    eşlemesi kullanılmalı)."""
    email = ident.get("email") or ""
    if not ident.get("email_verified") or "@" not in email:
        return False
    return email.rsplit("@", 1)[1] in (cfg.get("allowed_domains") or [])


def test(cfg: dict) -> dict:
    """"Bağlantıyı sına": keşif belgesi ve imza anahtarları (istemci sırrı sınanamaz; ilk girişte görülür)."""
    doc = discover(cfg, fresh=True)
    jwks = _jwks(cfg, doc, fresh=True)
    keys = [k for k in (jwks.get("keys") or []) if isinstance(k, dict) and k.get("use", "sig") == "sig"
            and k.get("kty") != "oct"]
    if not keys:
        raise OidcError("config", "Sağlayıcının JWKS'inde imza anahtarı yok.")
    algs = [a for a in (doc.get("id_token_signing_alg_values_supported") or ["RS256"]) if a in ALLOWED_ALGS]
    if not algs:
        raise OidcError("config", "Sağlayıcı desteklenen bir imza algoritması bildirmiyor.")
    return {"ok": True, "issuer": doc["issuer"], "authorization_endpoint": doc["authorization_endpoint"],
            "token_endpoint": doc["token_endpoint"], "keys": len(keys), "algorithms": algs,
            "pkce": "S256" in (doc.get("code_challenge_methods_supported") or ["S256"])}
