"""Panel kimlik doğrulaması: JWT (token_version iptali), rol bağımlılıkları, CSRF, TOTP 2FA, rate limiter."""

import base64
import datetime
import hashlib
import hmac
import secrets
import struct
import time
from typing import Optional
from urllib.parse import quote

import jwt  # PyJWT
from fastapi import Depends, HTTPException, Request, status
from fastapi.security import HTTPAuthorizationCredentials, HTTPBearer
from slowapi import Limiter
from slowapi.util import get_remote_address

from pops.config import CSRF_SAFE_METHODS, JWT_ALGO, JWT_COOKIE_NAME, JWT_EXPIRE_H, JWT_SECRET
from pops.db import execute_query


limiter = Limiter(key_func=get_remote_address)


security_scheme = HTTPBearer(auto_error=False)


def create_jwt(username: str, role: str, token_version: int = 0) -> str:
    expire = datetime.datetime.now(datetime.timezone.utc) + datetime.timedelta(hours=JWT_EXPIRE_H)
    return jwt.encode(
        {'sub': username, 'role': role, 'tv': int(token_version or 0), 'exp': expire}, JWT_SECRET, algorithm=JWT_ALGO
    )


def verify_jwt(token: str) -> dict:
    try:
        payload = jwt.decode(token, JWT_SECRET, algorithms=[JWT_ALGO])
    except jwt.PyJWTError:
        return None
    # 2FA challenge jetonu (twofa=pending) bir OTURUM jetonu DEĞİLDİR: şifre doğrulandıktan
    # sonra, OTP girilmeden önce üretilir. Aynı JWT_SECRET ile imzalı olduğundan burada açıkça
    # reddedilmezse require_auth-only uçlara (ör. /api/admin/2fa/*) veya WebSocket'e sunulup
    # 2FA atlatılabilirdi. Challenge yalnızca verify_totp_challenge (kendi decode'u) ile geçerlidir.
    if payload.get('twofa'):
        return None
    return payload


async def verify_session(payload: Optional[dict]) -> Optional[dict]:
    """JWT imza/exp doğrulandıktan SONRA, oturumu DB'ye karşı kontrol eder (F4 iptal):
    - kullanıcı silinmişse (satır yok) reddet;
    - JWT'deki token_version DB'dekiyle uyuşmuyorsa reddet (rol/şifre değişimi eski jetonları geçersizler);
    - ROLÜ JWT iddiasından DEĞİL, DB'den döndür (rol düşürme anında geçerli olur).
    Eski (tv iddiası olmayan) jetonlar tv=0 sayılır (geçiş uyumu)."""
    if not payload:
        return None
    sub = payload.get('sub')
    if not sub:
        return None
    try:
        rows = await execute_query("SELECT role, token_version FROM users WHERE username=$1", (sub,), fetch=True)
    except Exception:
        return None
    if not rows:
        return None
    if int(payload.get('tv', 0)) != int(rows[0].get('token_version') or 0):
        return None
    return {'sub': sub, 'role': rows[0].get('role')}


async def require_auth(request: Request, creds: HTTPAuthorizationCredentials = Depends(security_scheme)):
    token = creds.credentials if creds else request.cookies.get(JWT_COOKIE_NAME)
    if not token:
        raise HTTPException(status_code=status.HTTP_401_UNAUTHORIZED, detail='Token gerekli')
    # Çerezle doğrulanan, durum değiştiren isteklerde CSRF koruması: özel başlık zorunlu
    # (başka bir site bu başlığı CORS izni olmadan gönderemez)
    if (
        not creds
        and request.method not in CSRF_SAFE_METHODS
        and request.headers.get('X-Requested-With') != 'XMLHttpRequest'
    ):
        raise HTTPException(status_code=status.HTTP_403_FORBIDDEN, detail='CSRF doğrulaması başarısız')
    session = await verify_session(verify_jwt(token))
    if not session:
        raise HTTPException(
            status_code=status.HTTP_401_UNAUTHORIZED, detail='Geçersiz, süresi dolmuş ya da iptal edilmiş oturum'
        )
    return session


async def require_admin(payload: dict = Depends(require_auth)):
    if payload.get('role') not in ['admin', 'superadmin']:
        raise HTTPException(status_code=status.HTTP_403_FORBIDDEN, detail="Bu islem icin admin yetkisi gereklidir.")
    return payload


async def require_superadmin(payload: dict = Depends(require_auth)):
    if payload.get('role') != 'superadmin':
        raise HTTPException(
            status_code=status.HTTP_403_FORBIDDEN, detail="Bu islem icin superadmin yetkisi gereklidir."
        )
    return payload


# ── İki adımlı doğrulama (TOTP, RFC 6238) — opt-in, ek bağımlılık yok ────────────
# Standart TOTP: base32 gizli anahtar + HMAC-SHA1, 30 sn'lik pencere. Python
# standart kütüphanesiyle üretilir (pyotp gibi ek paket internetsiz okullarda
# pip gerektirmesin diye kullanılmaz). Google Authenticator/Authy uyumludur.
_TOTP_STEP = 30


_TOTP_DIGITS = 6


def _totp_new_secret(nbytes: int = 20) -> str:
    """Yeni base32 gizli anahtar (dolgu '='siz; authenticator uygulamaları böyle bekler)."""
    return base64.b32encode(secrets.token_bytes(nbytes)).decode('ascii').rstrip('=')


def _totp_code(secret_b32: str, counter: int) -> str:
    key = base64.b32decode(secret_b32 + '=' * ((8 - len(secret_b32) % 8) % 8))
    digest = hmac.new(key, struct.pack('>Q', counter), hashlib.sha1).digest()
    offset = digest[-1] & 0x0F
    bincode = struct.unpack('>I', digest[offset : offset + 4])[0] & 0x7FFFFFFF
    return str(bincode % (10**_TOTP_DIGITS)).zfill(_TOTP_DIGITS)


def totp_step(secret_b32: Optional[str], code: Optional[str], window: int = 1) -> Optional[int]:
    """Kod ±1 pencerede (saat kayması toleransı) geçerliyse eşleştiği zaman adımı, değilse None. Sabit zamanlı
    karşılaştırma. Adım, aynı kodun ikinci kez kullanılmasını engellemek için saklanır (bkz. consume_totp)."""
    if not secret_b32 or not code:
        return None
    code = code.strip().replace(' ', '')
    if not code.isdigit() or len(code) != _TOTP_DIGITS:
        return None
    now = int(time.time() // _TOTP_STEP)
    found = None
    try:
        for w in range(-window, window + 1):
            if hmac.compare_digest(_totp_code(secret_b32, now + w), code):
                found = now + w
    except Exception:
        return None
    return found


def verify_totp(secret_b32: Optional[str], code: Optional[str], window: int = 1) -> bool:
    return totp_step(secret_b32, code, window) is not None


async def consume_totp(user_id: int, secret_b32: Optional[str], code: Optional[str]) -> bool:
    """Kodu doğrular ve TÜKETİR: aynı kullanıcı için daha önce kullanılan adımdan (ya da öncesinden) bir kod bir daha
    kabul edilmez. Böylece omuz üstünden ya da ağdan yakalanan bir kod, geçerlilik süresi içinde tekrar kullanılamaz.
    Koşullu UPDATE atomiktir: aynı kodla iki eşzamanlı istekten yalnızca biri geçer."""
    step = totp_step(secret_b32, code)
    if step is None:
        return False
    rows = await execute_query(
        "UPDATE users SET totp_last_step = $1 WHERE id = $2 "
        "AND (totp_last_step IS NULL OR totp_last_step < $1) RETURNING id",
        (step, user_id),
        fetch=True,
    )
    return bool(rows)


def totp_provisioning_uri(secret_b32: str, username: str, issuer: str = "POps") -> str:
    """Authenticator'a QR/manuel eklemek için otpauth:// URI'si. Etiket 'issuer:hesap'
    biçimindedir; ayraç ':' literal kalır (Google Authenticator vb. böyle bekler), parçalar
    ayrı ayrı yüzde-kodlanır."""
    label = "%s:%s" % (quote(issuer, safe=''), quote(username, safe=''))
    return "otpauth://totp/%s?secret=%s&issuer=%s&digits=%d&period=%d" % (
        label,
        secret_b32,
        quote(issuer, safe=''),
        _TOTP_DIGITS,
        _TOTP_STEP,
    )


def create_totp_challenge(username: str, token_version: int = 0) -> str:
    """Şifre doğrulandıktan sonra 2. adım (kod) için kısa ömürlü (5 dk) challenge jetonu.
    'twofa=pending' taşır; normal oturum jetonu olarak KULLANILAMAZ (role yok → require_admin reddeder).
    Kullanıcının token_version'ı da içindedir: arada şifre/rol değişirse ya da oturumlar iptal edilirse
    bekleyen ikinci adım da geçersiz olur."""
    expire = datetime.datetime.now(datetime.timezone.utc) + datetime.timedelta(minutes=5)
    return jwt.encode(
        {'sub': username, 'twofa': 'pending', 'tv': int(token_version or 0), 'exp': expire},
        JWT_SECRET,
        algorithm=JWT_ALGO,
    )


def verify_totp_challenge(token: str) -> Optional[tuple]:
    """Geçerli challenge ise (kullanıcı adı, token_version), değilse None."""
    try:
        payload = jwt.decode(token, JWT_SECRET, algorithms=[JWT_ALGO])
    except jwt.PyJWTError:
        return None
    if payload.get('twofa') != 'pending' or not payload.get('sub'):
        return None
    return payload.get('sub'), int(payload.get('tv', 0))
