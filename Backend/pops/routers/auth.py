"""Panel girişi (şifre + opsiyonel TOTP), 2FA yönetimi ve kullanıcı yönetimi uçları."""

import asyncio
import datetime
import json
from typing import Optional

import asyncpg
import bcrypt
from fastapi import APIRouter, Depends, HTTPException, Request

from pops import secretbox
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


def _login_success(u: dict) -> dict:
    return {
        "status": "success",
        "message": "Giriş Başarılı",
        "role": u['role'],
        "username": u['username'],
        "permissions": u.get('permissions', '[]'),
        "token": create_jwt(u['username'], u['role'], u.get('token_version', 0)),
    }


async def _mark_login(user_id: int):
    now = datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S")
    await execute_query("UPDATE users SET last_login=$1 WHERE id=$2", (now, user_id))


@router.post("/api/admin/login")
@limiter.limit("10/minute")
async def admin_login(request: Request, data: AdminLoginInput):
    user = await execute_query(
        "SELECT id, username, role, permissions, password_hash, totp_enabled, totp_secret, token_version "
        "FROM users WHERE username = $1",
        (data.username,),
        fetch=True,
    )
    if not user:
        # Olmayan kullanıcıda da aynı süre harcanır (kullanıcı adı tahmini zamanlamadan anlaşılmasın)
        await asyncio.to_thread(bcrypt.checkpw, data.password.encode(), _DUMMY_HASH)
        raise HTTPException(status_code=401, detail="Geçersiz kullanıcı adı veya şifre")

    u = user[0]
    if not await asyncio.to_thread(_check_password, data.password, u['password_hash'] or ''):
        raise HTTPException(status_code=401, detail="Geçersiz kullanıcı adı veya şifre")

    # İki adımlı doğrulama etkinse: kod yoksa challenge dön (2. adım), varsa burada doğrula.
    if u.get('totp_enabled'):
        if data.otp:
            if not await consume_totp(u['id'], secretbox.unseal(u.get('totp_secret')), data.otp):
                raise HTTPException(status_code=401, detail="Doğrulama kodu geçersiz")
        else:
            return {
                "status": "totp_required",
                "challenge": create_totp_challenge(u['username'], u.get('token_version') or 0),
            }

    await _mark_login(u['id'])
    return _login_success(u)


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
        "SELECT id, username, role, permissions, totp_enabled, totp_secret, token_version "
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
    return _login_success(u)


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
    users = await execute_query(
        "SELECT id, username, role, last_login, permissions FROM users ORDER BY id ASC", fetch=True
    )
    return {"status": "success", "users": users}


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


@router.post("/api/admin/users")
async def create_user(data: UserCreateInput, auth=Depends(require_superadmin)):
    username, permissions = _clean_user_fields(data.username, data.role, data.permissions)
    if not data.password:
        raise HTTPException(status_code=400, detail="Şifre boş olamaz.")
    hashed_pw = await _hash_password(data.password)
    try:
        await execute_query(
            "INSERT INTO users (username, password_hash, role, permissions) VALUES ($1, $2, $3, $4)",
            (username, hashed_pw, data.role, permissions),
        )
    except asyncpg.UniqueViolationError:
        raise HTTPException(status_code=409, detail="Bu kullanıcı adı zaten var.")
    return {"status": "success", "message": "Kullanıcı başarıyla oluşturuldu."}


@router.put("/api/admin/users/{user_id}")
async def update_user(user_id: int, data: UserUpdateInput, auth=Depends(require_superadmin)):
    username, permissions = _clean_user_fields(data.username, data.role, data.permissions)
    current = await execute_query("SELECT role FROM users WHERE id=$1", (user_id,), fetch=True)
    if not current:
        raise HTTPException(status_code=404, detail="Kullanıcı bulunamadı.")
    if (
        current[0]["role"] == 'superadmin'
        and data.role != 'superadmin'
        and await _superadmin_count(exclude_id=user_id) == 0
    ):
        raise HTTPException(status_code=400, detail="Son superadmin hesabının rolü düşürülemez.")
    try:
        # F4: her düzenlemede token_version artar → bu kullanıcının eldeki eski JWT'leri anında
        # geçersiz olur (şifre sıfırlama/rol düşürme sonrası 12 saat beklenmez).
        if data.password:
            hashed_pw = await _hash_password(data.password)
            await execute_query(
                "UPDATE users SET username=$1, password_hash=$2, role=$3, permissions=$4, "
                "token_version=token_version+1 WHERE id=$5",
                (username, hashed_pw, data.role, permissions, user_id),
            )
        else:
            await execute_query(
                "UPDATE users SET username=$1, role=$2, permissions=$3, token_version=token_version+1 WHERE id=$4",
                (username, data.role, permissions, user_id),
            )
    except asyncpg.UniqueViolationError:
        raise HTTPException(status_code=409, detail="Bu kullanıcı adı zaten var.")
    return {"status": "success", "message": "Kullanıcı başarıyla güncellendi."}


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
