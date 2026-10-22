"""Dizin (LDAP / AD) ve OpenID Connect ile giriş: OIDC akışının uçları, giriş biletinin bozdurulması, sağlayıcı
ayarları (yalnızca süper admin) ve "Bağlantıyı sına". LDAP girişinin kendisi /api/admin/login'dedir (routers/auth.py).

OIDC akışı (D-24):
  1. Panel (login.php) bir bağ (binding) üretip PHP oturumuna yazar, tarayıcıyı /api/auth/oidc/start?b=<özeti>
     adresine gönderir.
  2. start: state, nonce ve PKCE doğrulayıcısı üretir, sunucuda saklar (10 dk), state'i HttpOnly + SameSite=Lax
     çerezine yazar ve sağlayıcıya yönlendirir.
  3. callback: state hem kayıtta hem çerezde olmalı (tek kullanımlık); kod PKCE ile değiştirilir, kimlik jetonu
     doğrulanır, gruplar role eşlenir, hesap bağlanır. Tarayıcı panelin giriş sayfasına tek kullanımlık, 60 sn'lik
     bir biletle döner.
  4. login.php bileti PHP oturumundaki bağla birlikte /api/auth/sso/redeem'e gönderir; yanıt şifreli girişle
     aynıdır (2FA açıksa ikinci adım istenir).
Yönlendirme adresleri yalnızca ayardan gelir (dönüş adresi ve ondan türeyen giriş sayfası); "next" yalnızca panelin
kendi yolu olabilir.
"""

import asyncio
import hmac
import logging
import secrets
from typing import Optional
from urllib.parse import urlencode, urlsplit

from fastapi import APIRouter, Depends, HTTPException, Query, Request
from fastapi.responses import RedirectResponse

from pops import sso, sso_ldap, sso_oidc
from pops.db import execute_query
from pops.models import LdapSettingsInput, LdapTestInput, OidcSettingsInput, SsoRedeemInput
from pops.routers.auth import finish_login
from pops.security import limiter, require_superadmin

log = logging.getLogger("pops.sso")

router = APIRouter()

OIDC_COOKIE = "pops_oidc_state"


def _login_url(cfg: dict) -> str:
    """Panelin giriş sayfası: ayardaki dönüş adresinden türetilir (isteğin Host başlığından değil)."""
    return cfg["redirect_uri"][: -len(sso.CALLBACK_SUFFIX)] + "/login"


def _cookie_path(cfg: dict) -> str:
    return urlsplit(cfg["redirect_uri"]).path.rsplit("/", 1)[0] or "/"


def _redirect(url: str, cfg: dict, clear_cookie: bool = True) -> RedirectResponse:
    resp = RedirectResponse(url, status_code=302)
    resp.headers["Cache-Control"] = "no-store"
    resp.headers["Referrer-Policy"] = "no-referrer"
    if clear_cookie:
        resp.delete_cookie(OIDC_COOKIE, path=_cookie_path(cfg), secure=cfg["redirect_uri"].startswith("https://"),
                           httponly=True, samesite="lax")
    return resp


def _fail(cfg: dict, code: str, detail: str, **extra) -> RedirectResponse:
    log.warning("OIDC girişi reddedildi", extra={"reason": code, "detail": detail[:200], **extra})
    return _redirect(_login_url(cfg) + "?" + urlencode({"sso_error": code}), cfg)


async def _oidc_enabled() -> tuple:
    enabled, cfg, secret = await sso.load("oidc")
    if not enabled or not cfg.get("redirect_uri") or not sso.callback_ok(cfg["redirect_uri"]):
        raise HTTPException(status_code=404, detail="OpenID Connect ile giriş kapalı.")
    return cfg, secret


# ── Giriş sayfası ve OIDC akışı (oturumsuz) ─────────────────────────────────────


@router.get("/api/auth/sso")
async def sso_info():
    """Giriş sayfası için: hangi yöntemler açık (ayrıntı yok)."""
    rows = await execute_query("SELECT kind, enabled, config ->> 'display_name' AS name FROM sso_providers",
                               fetch=True)
    on = {r["kind"]: r for r in rows or [] if r["enabled"]}
    return {"ldap": "ldap" in on, "oidc": "oidc" in on,
            "oidc_name": (on["oidc"]["name"] or "") if "oidc" in on else ""}


# Tarayıcı yönlendirmeleri: API şemasında yer almaz (dönüş adresi düz /api yolu olmalı, /api/v1 değil)
@router.get("/api/auth/oidc/start", include_in_schema=False)
@limiter.limit("20/minute")
async def oidc_start(
    request: Request,
    b: str = Query(..., pattern=r"^[0-9a-f]{64}$", description="Panel oturumundaki bağın SHA-256 özeti"),
    next: Optional[str] = Query(default=None, max_length=512, description="Girişten sonra açılacak panel yolu"),
):
    cfg, _secret = await _oidc_enabled()
    nxt = sso.safe_next(next)
    if next and nxt is None:
        # Açık yönlendirme denemesi (başka site, //, \, şema): reddedilir, sessizce düzeltilmez
        log.warning("OIDC: geçersiz dönüş yolu reddedildi")
        raise HTTPException(status_code=400, detail="Geçersiz dönüş adresi.")
    try:
        doc = await asyncio.to_thread(sso_oidc.discover, cfg)
    except sso_oidc.OidcError as exc:
        return _fail(cfg, "unavailable", exc.message)
    state = secrets.token_urlsafe(32)
    nonce = secrets.token_urlsafe(32)
    verifier = secrets.token_urlsafe(64)
    await sso.put_flow("oidc_state", state, {"nonce": nonce, "verifier": verifier, "binding": b, "next": nxt},
                       sso.OIDC_STATE_TTL)
    resp = _redirect(sso_oidc.authorize_url(cfg, doc, state, nonce, verifier), cfg, clear_cookie=False)
    resp.set_cookie(OIDC_COOKIE, state, max_age=sso.OIDC_STATE_TTL, path=_cookie_path(cfg),
                    secure=cfg["redirect_uri"].startswith("https://"), httponly=True, samesite="lax")
    return resp


@router.get("/api/auth/oidc/callback", include_in_schema=False)
@limiter.limit("20/minute")
async def oidc_callback(request: Request, state: str = "", code: str = "", error: str = ""):
    cfg, secret = await _oidc_enabled()
    if not state or len(state) > 128:
        return _fail(cfg, "state", "state yok")
    flow = await sso.take_flow("oidc_state", state)   # tek kullanımlık: eşleşmese de silinir
    cookie = request.cookies.get(OIDC_COOKIE, "")
    if not flow or not hmac.compare_digest(cookie.encode("utf-8"), state.encode("utf-8")):
        return _fail(cfg, "state", "state kayıtta ya da çerezde yok")
    if error:
        return _fail(cfg, "provider", "sağlayıcı reddetti: %s" % error[:60])
    if not code or len(code) > 4096:
        return _fail(cfg, "token", "kod yok")
    try:
        doc = await asyncio.to_thread(sso_oidc.discover, cfg)
        id_token = await asyncio.to_thread(sso_oidc.exchange_code, cfg, doc, secret, code, flow["verifier"])
        claims = await asyncio.to_thread(sso_oidc.validate_id_token, cfg, doc, id_token, flow["nonce"])
    except sso_oidc.OidcError as exc:
        return _fail(cfg, exc.code if exc.code in ("unavailable", "token") else "token", exc.message)
    ident = sso_oidc.identity(cfg, claims)
    # Buradan sonrası kimliği sağlayıcının imzasıyla doğrulanmış kişi içindir: erişimi kalmadıysa oturumları kapanır
    linked = await execute_query("SELECT id FROM users WHERE auth_source = 'oidc' AND external_id = $1",
                                 (ident["external_id"],), fetch=True)
    linked_id = linked[0]["id"] if linked else None
    if (cfg.get("username_claim") or "email") == "email" and not ident["email_verified"]:
        # Doğrulanmamış bir e-posta başkasının adını (ve önceden açılmış hesabını) almaya yaramasın
        return _fail(cfg, "access", "kullanıcı adı olan e-posta doğrulanmamış (email_verified)", user=ident["username"])
    if cfg.get("allowed_domains") and not sso_oidc.domain_allowed(cfg, ident):
        await sso.revoke_sessions(linked_id, "e-posta alan adı izinli değil")
        return _fail(cfg, "access", "e-posta alan adı izinli değil ya da doğrulanmamış", user=ident["username"])
    mapped = sso.map_role(ident["groups"], cfg.get("group_map") or [], dn=False)
    if not mapped and cfg.get("default_role") and cfg.get("allowed_domains"):
        mapped = (cfg["default_role"], list(cfg.get("default_pages") or []), cfg.get("default_org_scope"))
    if not mapped:
        await sso.revoke_sessions(linked_id, "grup eşlemesi yok")
        return _fail(cfg, "access", "eşlenen grup yok", user=ident["username"])
    if not sso.valid_username(ident["username"]):
        return _fail(cfg, "token", "kullanıcı adı talebi boş ya da geçersiz (%s)" % cfg.get("username_claim"))
    try:
        user = await sso.link_user("oidc", ident["external_id"], ident["username"], mapped[0], mapped[1], mapped[2])
    except sso.SsoRefused as exc:
        return _fail(cfg, exc.code, exc.message, user=ident["username"])
    ticket = await sso.issue_ticket(user, "oidc", flow["binding"], flow.get("next"))
    log.info("OIDC girişi doğrulandı", extra={"user": user["username"], "role": user["role"]})
    return _redirect(_login_url(cfg) + "?" + urlencode({"sso": ticket}), cfg)


@router.post("/api/auth/sso/redeem")
@limiter.limit("10/minute")
async def sso_redeem(request: Request, data: SsoRedeemInput):
    """Panelin (login.php) sunucu tarafı: bileti, akışı başlatan PHP oturumunun bağıyla birlikte bozdurur."""
    t = await sso.take_flow("ticket", data.ticket)
    if not t or not hmac.compare_digest(str(t.get("binding", "")), sso.binding_hash(data.binding)):
        raise HTTPException(status_code=401, detail="Giriş bileti geçersiz ya da süresi dolmuş; yeniden deneyin.")
    rows = await execute_query(
        "SELECT id, username, role, permissions, totp_enabled, totp_secret, token_version, auth_source, org_scope "
        "FROM users WHERE id = $1", (t["user_id"],), fetch=True)
    if not rows or rows[0]["auth_source"] != t.get("source") or int(rows[0]["token_version"] or 0) != t.get("tv"):
        raise HTTPException(status_code=401, detail="Giriş bileti geçersiz ya da süresi dolmuş; yeniden deneyin.")
    return {**await finish_login(rows[0]), "next": t.get("next")}


# ── Ayarlar (yalnızca süper admin) ──────────────────────────────────────────────


def _clean(kind: str, data) -> dict:
    raw = data.model_dump(exclude={"enabled", "bind_password", "client_secret", "test_username"})
    try:
        return sso.clean_ldap(raw) if kind == "ldap" else sso.clean_oidc(raw)
    except ValueError as exc:
        raise HTTPException(status_code=400, detail=str(exc))


@router.get("/api/sso/settings")
async def get_settings(auth: dict = Depends(require_superadmin)):
    """İki sağlayıcının ayarları; sırlar dönmez (yalnızca kayıtlı olup olmadıkları)."""
    return {"ldap": await sso.public_state("ldap"), "oidc": await sso.public_state("oidc")}


async def _save(kind: str, data, secret: Optional[str], auth: dict) -> dict:
    cfg = _clean(kind, data)
    try:
        return await sso.save(kind, data.enabled, cfg, secret, auth.get("sub"))
    except ValueError as exc:
        raise HTTPException(status_code=400, detail=str(exc))


@router.put("/api/sso/settings/ldap")
async def put_ldap(data: LdapSettingsInput, auth: dict = Depends(require_superadmin)):
    return await _save("ldap", data, data.bind_password, auth)


@router.put("/api/sso/settings/oidc")
async def put_oidc(data: OidcSettingsInput, auth: dict = Depends(require_superadmin)):
    sso_oidc.clear_cache()
    return await _save("oidc", data, data.client_secret, auth)


@router.post("/api/sso/test/ldap")
@limiter.limit("10/minute")
async def test_ldap(request: Request, data: LdapTestInput, auth: dict = Depends(require_superadmin)):
    """Formdaki (kaydedilmemiş olabilir) ayarlarla bağlantıyı sınar. Şifre yazılmadıysa kayıtlı şifre, yalnızca
    sunucu ve hizmet hesabı kayıtlıyla aynıysa kullanılır."""
    cfg = _clean("ldap", data)
    if not cfg["host"] or not cfg["base_dn"]:
        return {"ok": False, "message": "Sunucu adı ve arama kökü gerekli."}
    _enabled, stored, stored_secret = await sso.load("ldap")
    secret = sso.secret_for_test("ldap", cfg, stored, stored_secret, data.bind_password)
    username = (data.test_username or "").strip() or None
    try:
        out = await asyncio.to_thread(sso_ldap.test, cfg, secret, username)
    except sso_ldap.LdapError as exc:
        return {"ok": False, "message": exc.message}
    if username and out.get("user_dn"):
        mapped = sso.map_role(out.get("groups") or [], cfg["group_map"], dn=True)
        out["role"] = mapped[0] if mapped else None
        out["pages"] = mapped[1] if mapped else []
        out["org_scope"] = mapped[2] if mapped else None
    return out


@router.post("/api/sso/test/oidc")
@limiter.limit("10/minute")
async def test_oidc(request: Request, data: OidcSettingsInput, auth: dict = Depends(require_superadmin)):
    cfg = _clean("oidc", data)
    try:
        out = await asyncio.to_thread(sso_oidc.test, cfg)
    except sso_oidc.OidcError as exc:
        return {"ok": False, "message": exc.message}
    if cfg.get("redirect_uri") and not sso.callback_ok(cfg["redirect_uri"]):
        out.update(ok=False, message="Dönüş adresi geçersiz.")
    return out
