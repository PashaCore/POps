"""Panel girişi (şifre + opsiyonel TOTP), 2FA yönetimi ve kullanıcı yönetimi uçları."""

import asyncio
import json
import logging
from typing import Optional

import asyncpg
import bcrypt
from fastapi import APIRouter, Depends, HTTPException, Request

from pops import secretbox, sso, sso_ldap, tenancy, timeutil
from pops.audit import add_audit_log
from pops.db import execute_query
from pops.models import (
    AdminLoginInput,
    TotpDisableInput,
    TotpEnableInput,
    TotpLoginInput,
    UserCreateInput,
    UserUpdateInput,
)
from pops.security import (
    _totp_new_secret,
    consume_totp,
    create_jwt,
    create_totp_challenge,
    limiter,
    require_admin_session,
    require_superadmin,
    require_user_session,
    totp_provisioning_uri,
    verify_totp_challenge,
)

log = logging.getLogger("pops.auth")

router = APIRouter()

# F18: bcrypt saniyenin onda biri ile dörtte biri arası CPU yer. Tek uvicorn sürecinin olay döngüsünde çalışırsa o
# sürede heartbeat'ler, komutlar ve WebSocket'ler bekler; bu yüzden ayrı bir iş parçacığında çalışır. Olmayan
# kullanıcı için karşılaştırılan sahte özet açılışta bir kez üretilir (eskiden her istekte yeni hash üretiliyordu).
_DUMMY_HASH = bcrypt.hashpw(b"pops-dummy-password", bcrypt.gensalt())


def _check_password(password: str, stored_hash: str) -> bool:
    # Yalnızca bcrypt kabul edilir. Tuzsuz SHA256 özetleri ve '!disabled' gibi
    # geçersiz değerler bcrypt'te ValueError verir; bu hesaplar giriş yapamaz.
    try:
        return bcrypt.checkpw(password.encode(), stored_hash.encode())
    except ValueError:
        return False


async def _hash_password(password: str) -> str:
    return (await asyncio.to_thread(bcrypt.hashpw, password.encode(), bcrypt.gensalt())).decode()


async def _login_success(u: dict) -> dict:
    # Kurum birimi kapsamı (pops/tenancy.py): panel yan menüde birim adını gösterir; kapsamsızda boş
    scope = await tenancy.principal_info({"sub": u['username'], "role": u['role'], "org_scope": u.get('org_scope')})
    return {
        "status": "success",
        "message": "Giriş Başarılı",
        "role": u['role'],
        "username": u['username'],
        "permissions": u.get('permissions', '[]'),
        "token": create_jwt(u['username'], u['role'], u.get('token_version', 0)),
        **scope,
    }


async def _mark_login(user_id: int):
    await execute_query("UPDATE users SET last_login=$1 WHERE id=$2", (timeutil.now(), user_id))


async def finish_login(u: dict, otp: Optional[str] = None) -> dict:
    """Kimliği doğrulanmış kullanıcı (yerel şifre, dizin ya da OIDC): 2FA açıksa kod yoksa ikinci adımın challenge'ı,
    kod varsa burada doğrulanır; sonra oturum jetonu."""
    if u.get('totp_enabled'):
        if otp:
            if not await consume_totp(u['id'], secretbox.unseal(u.get('totp_secret')), otp):
                raise HTTPException(status_code=401, detail="Doğrulama kodu geçersiz")
        else:
            return {
                "status": "totp_required",
                "challenge": create_totp_challenge(u['username'], u.get('token_version') or 0),
            }
    await _mark_login(u['id'])
    return await _login_success(u)


_BAD_LOGIN = "Geçersiz kullanıcı adı veya şifre"


async def _dummy_check(password: str) -> None:
    # Olmayan kullanıcıda ve dizinin reddettiği girişte de bir bcrypt süresi harcanır: yerel hesapların adları
    # yanıt süresinden anlaşılmasın
    await asyncio.to_thread(bcrypt.checkpw, password.encode(), _DUMMY_HASH)


async def _ldap_login(username: str, password: str, local: Optional[dict]) -> Optional[dict]:
    """Dizin hesabıyla giriş. LDAP kapalıysa None (bilinmeyen kullanıcı gibi davranılır). local: bu ada bağlı dizin
    kaynaklı yerel kayıt (varsa)."""
    enabled, cfg, secret = await sso.load("ldap")
    if not enabled:
        return None
    try:
        res = await asyncio.to_thread(sso_ldap.authenticate, cfg, secret, username, password)
    except sso_ldap.LdapError as exc:
        log.warning("dizin girişi yapılamadı: dizine ulaşılamıyor", extra={"user": username, "error": exc.message})
        raise HTTPException(
            status_code=503, detail="Dizin sunucusuna ulaşılamadı; yerel bir hesapla giriş yapabilirsiniz.")
    if res.status in ("bad_credentials", "not_found", "ambiguous"):
        if res.status == "not_found" and local and local.get("external_id"):
            # Şifre doğrulanmadan kimsenin oturumu kapatılmaz: yalnızca bağlı hesap değişmez kimliğiyle de dizinde
            # yoksa (silinmişse). Ad değişmiş ya da filtre başka bir öznitelikte arıyorsa oturumlar kalır.
            try:
                gone = not await asyncio.to_thread(sso_ldap.exists, cfg, secret, local["external_id"])
            except sso_ldap.LdapError:
                gone = False
            if gone:
                await sso.revoke_sessions(local["id"], "dizinde yok")
        await _dummy_check(password)
        raise HTTPException(status_code=401, detail=_BAD_LOGIN)
    # Buradan sonrası dizindeki şifre doğrulandıktan sonradır
    linked = await execute_query("SELECT id FROM users WHERE auth_source = 'ldap' AND external_id = $1",
                                 (res.external_id,), fetch=True)
    linked_id = linked[0]["id"] if linked else None
    if res.status == "disabled":
        await sso.revoke_sessions(linked_id, "dizinde devre dışı")
        raise HTTPException(status_code=403, detail="Bu hesap dizinde devre dışı bırakılmış.")
    mapped = sso.map_role(res.groups, cfg.get("group_map") or [], dn=True)
    if not mapped:
        await sso.revoke_sessions(linked_id, "grup eşlemesi yok")
        raise HTTPException(
            status_code=403, detail="Bu dizin hesabının panele erişimi yok (eşlenen bir grupta değil).")
    try:
        return await sso.link_user("ldap", res.external_id, res.username, mapped[0], mapped[1], mapped[2])
    except sso.SsoRefused as exc:
        raise HTTPException(status_code=exc.status, detail=exc.message)


@router.post("/api/admin/login")
@limiter.limit("10/minute")
async def admin_login(request: Request, data: AdminLoginInput):
    rows = await execute_query(
        "SELECT id, username, role, permissions, password_hash, totp_enabled, totp_secret, token_version, "
        "auth_source, external_id, org_scope FROM users WHERE lower(username) = lower($1)",
        (data.username,),
        fetch=True,
    )
    exact = next((r for r in rows if r['username'] == data.username), None)
    if exact and exact['auth_source'] == 'local':
        if not await asyncio.to_thread(_check_password, data.password, exact['password_hash'] or ''):
            raise HTTPException(status_code=401, detail=_BAD_LOGIN)
        u = exact
    else:
        # Yerel hesabı olmayan ad dizin açıksa dizine sorulur. Yerel hesaplar (büyük/küçük harf farkıyla yazılmış
        # olsalar da) hiçbir zaman dizine gitmez: yerel şifre başka bir sunucuya gönderilmez.
        u = None
        if not any(r['auth_source'] != 'ldap' for r in rows):
            local = exact or (rows[0] if len(rows) == 1 else None)
            u = await _ldap_login(data.username, data.password, local)
        if u is None:
            # Olmayan kullanıcıda da aynı süre harcanır (kullanıcı adı tahmini zamanlamadan anlaşılmasın)
            await _dummy_check(data.password)
            raise HTTPException(status_code=401, detail=_BAD_LOGIN)

    # İki adımlı doğrulama etkinse: kod yoksa challenge dön (2. adım), varsa burada doğrula.
    return await finish_login(u, data.otp)


@router.post("/api/admin/login/totp")
@limiter.limit("10/minute")
async def admin_login_totp(request: Request, data: TotpLoginInput):
    """Girişin 2. adımı: şifre doğrulandıktan sonra dönen challenge + authenticator kodu.
    Böylece şifre 2. adımda tekrar taşınmaz."""
    challenge = verify_totp_challenge(data.challenge)
    if not challenge:
        raise HTTPException(status_code=401, detail="Oturum doğrulaması süresi doldu, tekrar giriş yapın")
    username, challenge_tv = challenge
    user = await execute_query(
        "SELECT id, username, role, permissions, totp_enabled, totp_secret, token_version, org_scope "
        "FROM users WHERE username = $1",
        (username,),
        fetch=True,
    )
    if not user or not user[0].get('totp_enabled'):
        raise HTTPException(status_code=401, detail="Geçersiz istek")
    u = user[0]
    # Şifre adımından sonra şifre/rol değiştiyse ya da oturumlar iptal edildiyse bekleyen ikinci adım geçmez
    if challenge_tv != int(u.get('token_version') or 0):
        raise HTTPException(status_code=401, detail="Oturum doğrulaması süresi doldu, tekrar giriş yapın")
    if not await consume_totp(u['id'], secretbox.unseal(u.get('totp_secret')), data.otp):
        raise HTTPException(status_code=401, detail="Doğrulama kodu geçersiz")
    await _mark_login(u['id'])
    return await _login_success(u)


# ── 2FA kayıt/yönetim (giriş yapmış kullanıcı kendi 2FA'sını yönetir; API jetonu kullanamaz) ──


@router.get("/api/admin/2fa/status")
async def totp_status(auth: dict = Depends(require_user_session)):
    rows = await execute_query("SELECT totp_enabled FROM users WHERE username=$1", (auth['sub'],), fetch=True)
    return {"enabled": bool(rows and rows[0].get('totp_enabled'))}


@router.post("/api/admin/2fa/setup")
@limiter.limit("10/minute")
async def totp_setup(request: Request, auth: dict = Depends(require_user_session)):
    """Yeni gizli anahtar üretir (henüz zorunlu DEĞİL; onaylanınca aktifleşir). QR için
    otpauth URI'si + manuel giriş için base32 anahtar döner."""
    rows = await execute_query("SELECT totp_enabled FROM users WHERE username=$1", (auth['sub'],), fetch=True)
    if rows and rows[0].get('totp_enabled'):
        raise HTTPException(status_code=400, detail="2FA zaten aktif. Önce devre dışı bırakın.")
    secret = _totp_new_secret()
    await execute_query(
        "UPDATE users SET totp_secret=$1, totp_enabled=FALSE, totp_last_step=NULL WHERE username=$2",
        (secretbox.seal(secret), auth['sub']),
    )
    return {"secret": secret, "otpauth_uri": totp_provisioning_uri(secret, auth['sub'])}


@router.post("/api/admin/2fa/enable")
@limiter.limit("10/minute")
async def totp_enable(request: Request, data: TotpEnableInput, auth: dict = Depends(require_user_session)):
    """Kurulumdaki anahtarı bir kod ONAYLAYARAK aktifleştirir. Kod doğrulanmadan aktif
    edilmez → yanlış kurulumla kilitlenme olmaz. OTP doğrulayan bu uç ve /disable brute-force'a
    karşı rate-limitlidir (login-TOTP yoluyla aynı korumada)."""
    rows = await execute_query(
        "SELECT id, totp_secret, totp_enabled FROM users WHERE username=$1", (auth['sub'],), fetch=True
    )
    if not rows or not rows[0].get('totp_secret'):
        raise HTTPException(status_code=400, detail="Önce 2FA kurulumunu başlatın.")
    if rows[0].get('totp_enabled'):
        return {"ok": True, "enabled": True}
    if not await consume_totp(rows[0]['id'], secretbox.unseal(rows[0]['totp_secret']), data.otp):
        raise HTTPException(status_code=400, detail="Kod doğrulanamadı. Authenticator saatini kontrol edin.")
    await execute_query("UPDATE users SET totp_enabled=TRUE WHERE username=$1", (auth['sub'],))
    return {"ok": True, "enabled": True}


@router.post("/api/admin/2fa/disable")
@limiter.limit("10/minute")
async def totp_disable(request: Request, data: TotpDisableInput, auth: dict = Depends(require_user_session)):
    """2FA'yı kapatır. Aktifse geçerli bir kod ister (oturum çalınmışsa saldırgan kapatamasın).
    Kod doğrulaması rate-limitli: çalınmış oturumla bile 6 haneli kod brute-force edilemez."""
    rows = await execute_query(
        "SELECT id, totp_secret, totp_enabled FROM users WHERE username=$1", (auth['sub'],), fetch=True
    )
    if rows and rows[0].get('totp_enabled'):
        if not await consume_totp(rows[0]['id'], secretbox.unseal(rows[0].get('totp_secret')), data.otp):
            raise HTTPException(status_code=400, detail="Kapatmak için geçerli bir doğrulama kodu gerekir.")
    await execute_query(
        "UPDATE users SET totp_secret=NULL, totp_enabled=FALSE, totp_last_step=NULL WHERE username=$1",
        (auth['sub'],),
    )
    return {"ok": True, "enabled": False}


@router.get("/api/admin/users")
async def get_users(auth=Depends(require_admin_session)):
    """Kapsamlı yönetici (pops/tenancy.py) yalnızca kendisini ve kapsamı kendi birimleri içinde kalan kullanıcıları
    görür; kapsamsız hesaplar ona görünmez."""
    scope = await tenancy.scope_of(auth)
    args = [auth.get("sub")]
    cond = tenancy.owned_scope_sql(scope, "org_scope", args)
    users = await execute_query(
        "SELECT id, username, role, last_login, permissions, auth_source, org_scope FROM users "
        "WHERE username = $1 OR " + cond + " ORDER BY id ASC",
        tuple(args),
        fetch=True,
    )
    out = []
    for u in users or []:
        row = timeutil.iso_row(u)
        row["org_units"] = await tenancy.unit_names(u["org_scope"])
        out.append(row)
    return {"status": "success", "users": out}


VALID_ROLES = ('superadmin', 'admin', 'viewer')


def _clean_user_fields(username: str, role: str, permissions: str) -> tuple:
    """Kullanıcı alanlarını doğrular; hatada 400 döner. Yetki listesi JSON dizisi olarak normalize edilir."""
    username = (username or '').strip()
    if not username:
        raise HTTPException(status_code=400, detail="Kullanıcı adı boş olamaz.")
    # "token:<ad>" görev ve denetim kayıtlarında API jetonunu gösterir; bir kullanıcı o adı taşıyamaz
    if username.lower().startswith('token:'):
        raise HTTPException(
            status_code=400, detail="Kullanıcı adı 'token:' ile başlayamaz (API jetonlarına ayrılmıştır)."
        )
    if role not in VALID_ROLES:
        raise HTTPException(status_code=400, detail="Geçersiz rol.")
    try:
        perms = json.loads(permissions or '[]')
    except ValueError:
        perms = None
    if not isinstance(perms, list) or not all(isinstance(p, str) for p in perms):
        raise HTTPException(status_code=400, detail="Yetki listesi geçerli bir JSON dizisi olmalı.")
    return username, json.dumps(perms)


async def _superadmin_count(exclude_id: Optional[int] = None) -> int:
    rows = await execute_query(
        "SELECT COUNT(*) AS c FROM users WHERE role = 'superadmin' "
        "AND password_hash LIKE '$2%' AND id IS DISTINCT "
        "FROM $1",
        (exclude_id,),
        fetch=True,
    )
    return rows[0]["c"] if rows else 0


# Kullanıcı oluşturma, düzenleme ve silme yalnızca superadmin'e açıktır;
# aksi halde bir admin kendine superadmin hesabı açabilirdi.


async def _first_local_superadmin() -> Optional[int]:
    rows = await execute_query(
        "SELECT min(id) AS id FROM users WHERE role = 'superadmin' AND auth_source = 'local'", fetch=True)
    return rows[0]["id"] if rows else None


@router.post("/api/admin/users")
async def create_user(data: UserCreateInput, auth=Depends(require_superadmin)):
    username, permissions = _clean_user_fields(data.username, data.role, data.permissions)
    org_scope = await tenancy.clean_scope(data.org_scope, data.role)
    if data.auth_source == 'local':
        if not data.password:
            raise HTTPException(status_code=400, detail="Şifre boş olamaz.")
        hashed_pw = await _hash_password(data.password)
    elif data.password:
        raise HTTPException(status_code=400, detail="Dizin ya da OIDC hesabının yerel şifresi olmaz.")
    else:
        # İlk girişte dizindeki/sağlayıcıdaki kişiye bağlanır; rol ve sayfalar o zaman eşlemeden yazılır
        hashed_pw = sso.SSO_PASSWORD_HASH
    try:
        await execute_query(
            "INSERT INTO users (username, password_hash, role, permissions, auth_source, org_scope) "
            "VALUES ($1, $2, $3, $4, $5, $6)",
            (username, hashed_pw, data.role, permissions, data.auth_source, org_scope),
        )
    except asyncpg.UniqueViolationError:
        raise HTTPException(status_code=409, detail="Bu kullanıcı adı zaten var.")
    return {"status": "success", "message": "Kullanıcı başarıyla oluşturuldu."}


@router.put("/api/admin/users/{user_id}")
async def update_user(user_id: int, data: UserUpdateInput, auth=Depends(require_superadmin)):
    username, permissions = _clean_user_fields(data.username, data.role, data.permissions)
    current = await execute_query("SELECT role, auth_source, org_scope FROM users WHERE id=$1", (user_id,), fetch=True)
    if not current:
        raise HTTPException(status_code=404, detail="Kullanıcı bulunamadı.")
    # Kapsam gönderilmezse değişmez (şifre sıfırlama gibi); süper admine yükseltilen hesabın kapsamı kalkar
    if "org_scope" in data.model_fields_set:
        org_scope = await tenancy.clean_scope(data.org_scope, data.role)
    else:
        org_scope = None if data.role == 'superadmin' else current[0]["org_scope"]
    if (
        current[0]["role"] == 'superadmin'
        and data.role != 'superadmin'
        and await _superadmin_count(exclude_id=user_id) == 0
    ):
        raise HTTPException(status_code=400, detail="Son superadmin hesabının rolü düşürülemez.")
    old_source = current[0]["auth_source"]
    source = data.auth_source or old_source
    if source != old_source:
        await _check_source_change(user_id, current[0]["role"], old_source, source, data.password)
    elif source != 'local' and data.password:
        raise HTTPException(status_code=400, detail="Dizin ya da OIDC hesabının yerel şifresi olmaz.")
    try:
        # F4: her düzenlemede token_version artar → bu kullanıcının eldeki eski JWT'leri anında
        # geçersiz olur (şifre sıfırlama/rol düşürme sonrası 12 saat beklenmez).
        if source != old_source:
            # Kaynak değişince kimlik bağı sıfırlanır; dizin/OIDC hesabına geçen yerel şifresini kaybeder
            hashed_pw = await _hash_password(data.password) if source == 'local' else sso.SSO_PASSWORD_HASH
            await execute_query(
                "UPDATE users SET username=$1, password_hash=$2, role=$3, permissions=$4, auth_source=$5, "
                "external_id=NULL, org_scope=$7, token_version=token_version+1 WHERE id=$6",
                (username, hashed_pw, data.role, permissions, source, user_id, org_scope),
            )
            await add_audit_log("*", "user_auth_source", "Kullanıcının kimlik kaynağı değişti: %s" % username, {
                "user": username, "before": old_source, "after": source, "by": auth.get('sub')})
        elif data.password:
            hashed_pw = await _hash_password(data.password)
            await execute_query(
                "UPDATE users SET username=$1, password_hash=$2, role=$3, permissions=$4, org_scope=$6, "
                "token_version=token_version+1 WHERE id=$5",
                (username, hashed_pw, data.role, permissions, user_id, org_scope),
            )
        else:
            await execute_query(
                "UPDATE users SET username=$1, role=$2, permissions=$3, org_scope=$5, token_version=token_version+1 "
                "WHERE id=$4",
                (username, data.role, permissions, user_id, org_scope),
            )
    except asyncpg.UniqueViolationError:
        raise HTTPException(status_code=409, detail="Bu kullanıcı adı zaten var.")
    return {"status": "success", "message": "Kullanıcı başarıyla güncellendi."}


async def _check_source_change(user_id: int, role: str, old: str, new: str, password: Optional[str]) -> None:
    """Yerel ↔ dizin/OIDC geçişi. İlk yerel süper admin yerel kalır: dizine ya da sağlayıcıya ulaşılamadığında da
    biri girebilsin (D-24). Yerele dönen hesaba yeni şifre gerekir."""
    if old == 'local':
        if user_id == await _first_local_superadmin():
            raise HTTPException(
                status_code=400,
                detail="İlk süper admin hesabı yerel kalır; dizin ya da OIDC hesabına çevrilemez.")
        if role == 'superadmin' and await _superadmin_count(exclude_id=user_id) == 0:
            raise HTTPException(status_code=400, detail="Son yerel süper admin hesabı yerel kalmalı.")
        if password:
            raise HTTPException(status_code=400, detail="Dizin ya da OIDC hesabının yerel şifresi olmaz.")
    elif new == 'local' and not password:
        raise HTTPException(status_code=400, detail="Yerel hesaba çevirmek için yeni bir şifre girin.")


@router.delete("/api/admin/users/{user_id}")
async def delete_user(user_id: int, auth=Depends(require_superadmin)):
    target = await execute_query("SELECT username, role FROM users WHERE id=$1", (user_id,), fetch=True)
    if not target:
        raise HTTPException(status_code=404, detail="Kullanıcı bulunamadı.")
    if target[0]["username"] == auth.get('sub'):
        raise HTTPException(status_code=400, detail="Kendi hesabınızı silemezsiniz.")
    if target[0]["role"] == 'superadmin' and await _superadmin_count(exclude_id=user_id) == 0:
        raise HTTPException(status_code=400, detail="Son superadmin hesabı silinemez.")
    await execute_query("DELETE FROM users WHERE id=$1", (user_id,))
    return {"status": "success", "message": "Kullanıcı silindi."}
