"""Panel kimlik doğrulaması: JWT (token_version iptali), API jetonları, rol bağımlılıkları, CSRF, TOTP 2FA, rate
limiter."""

import base64
import datetime
import hashlib
import hmac
import logging
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

from pops.config import (
    CSRF_SAFE_METHODS, DEMO_USERS, JWT_ALGO, JWT_COOKIE_NAME, JWT_EXPIRE_H, JWT_SECRET, REDIS_PREFIX, REDIS_URL,
)
from pops.db import execute_query

log = logging.getLogger("pops.security")


if REDIS_URL:
    # Birden fazla süreçte istek sınırları bütün süreçlerde ortak (Redis). Redis'e ulaşılamazsa süreç kendi bellek
    # sayacına geçer ve Redis'i aralıklarla yeniden dener; kısa zaman aşımları olay döngüsünü bekletmesin diye.
    limiter = Limiter(
        key_func=get_remote_address,
        storage_uri=REDIS_URL,
        storage_options={"socket_connect_timeout": 0.5, "socket_timeout": 0.5},
        in_memory_fallback_enabled=True,
        key_prefix=REDIS_PREFIX,
    )
else:
    limiter = Limiter(key_func=get_remote_address)


# Demo hesapları (POPS_DEMO_USERS) yalnızca okur: GET/HEAD/OPTIONS dışındaki her panel isteği burada reddedilir.
# Gövdesi sorgu taşıyan, hiçbir şey değiştirmeyen POST uçları ayrıca izinlidir.
DEMO_DENIED = "Demo hesabında değiştirilemez"
DEMO_READ_ONLY_POSTS = frozenset({"/api/tasks/status"})


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
    - ROLÜ JWT iddiasından DEĞİL, DB'den döndür (rol düşürme anında geçerli olur); kurum birimi kapsamı da
      (org_scope, bkz. pops/tenancy.py) her istekte DB'den okunur.
    Eski (tv iddiası olmayan) jetonlar tv=0 sayılır (geçiş uyumu)."""
    if not payload:
        return None
    sub = payload.get('sub')
    if not sub:
        return None
    try:
        rows = await execute_query(
            "SELECT role, token_version, org_scope FROM users WHERE username=$1", (sub,), fetch=True
        )
    except Exception:
        return None
    if not rows:
        return None
    if int(payload.get('tv', 0)) != int(rows[0].get('token_version') or 0):
        return None
    scope = rows[0].get('org_scope')
    return {'sub': sub, 'role': rows[0].get('role'), 'org_scope': list(scope) if scope is not None else None}


# ── API jetonları (otomasyon) ──────────────────────────────────────────────────
# "pops_" + 32 bayt rastgele (urlsafe). Sunucuda yalnızca SHA-256 özeti saklanır; jeton oluşturulurken bir kez
# gösterilir. Yalnızca Authorization: Bearer başlığıyla kabul edilir (çerezde gelen jeton JWT sayılır, geçmez).
# Rolü viewer ya da admin'dir, asla superadmin değildir; süper admin uçlarına, kullanıcı/jeton yönetimine, 2FA'ya
# ve uzak ekrana (panel oturumu ister) ulaşamaz. İptal ve süre her istekte veritabanından okunur.
API_TOKEN_PREFIX = "pops_"
API_TOKEN_ROLES = ("viewer", "admin")
# last_used_at en çok bu sıklıkta yazılır (her istekte yazma olmasın)
API_TOKEN_TOUCH_SECONDS = 60
_API_TOKEN_MAX_LEN = 128


def new_api_token() -> str:
    return API_TOKEN_PREFIX + secrets.token_urlsafe(32)


def api_token_hash(token: str) -> str:
    return hashlib.sha256(token.encode("utf-8")).hexdigest()


def is_api_token(payload: Optional[dict]) -> bool:
    """Kimlik bir API jetonundan mı (panel oturumundan değil)?"""
    return bool(payload and payload.get('api_token_id'))


async def verify_api_token(token: str) -> Optional[dict]:
    """Geçerli (iptal edilmemiş, süresi dolmamış) jetonun kimliği: sub = "token:<ad>" (görev ve denetim kayıtlarına
    böyle yazılır), role = viewer | admin. Geçersizse None."""
    if not token.startswith(API_TOKEN_PREFIX) or len(token) > _API_TOKEN_MAX_LEN:
        return None
    try:
        rows = await execute_query(
            "SELECT id, name, role, org_scope, "
            "(last_used_at IS NULL OR last_used_at < NOW() - make_interval(secs => $2)) "
            "AS stale FROM api_tokens WHERE token_hash = $1 AND revoked_at IS NULL "
            "AND (expires_at IS NULL OR expires_at > NOW())",
            (api_token_hash(token), float(API_TOKEN_TOUCH_SECONDS)),
            fetch=True,
        )
    except Exception:
        log.warning("API jetonu doğrulanamadı", exc_info=True)
        return None
    if not rows or rows[0]['role'] not in API_TOKEN_ROLES:
        return None
    row = rows[0]
    if row['stale']:
        try:
            await execute_query(
                "UPDATE api_tokens SET last_used_at = NOW() WHERE id = $1 "
                "AND (last_used_at IS NULL OR last_used_at < NOW() - make_interval(secs => $2))",
                (row['id'], float(API_TOKEN_TOUCH_SECONDS)),
            )
        except Exception:
            log.warning("API jetonunun son kullanımı yazılamadı", exc_info=True, extra={"token_id": row['id']})
    scope = row.get('org_scope')
    return {
        'sub': 'token:' + row['name'], 'role': row['role'], 'api_token_id': row['id'],
        'org_scope': list(scope) if scope is not None else None,
    }


async def require_auth(request: Request, creds: HTTPAuthorizationCredentials = Depends(security_scheme)):
    token = creds.credentials if creds else request.cookies.get(JWT_COOKIE_NAME)
    if not token:
        raise HTTPException(status_code=status.HTTP_401_UNAUTHORIZED, detail='Token gerekli')
    if creds and token.startswith(API_TOKEN_PREFIX):
        # API jetonu çerez değildir, tarayıcı onu kendiliğinden göndermez: CSRF başlığı gerekmez
        principal = await verify_api_token(token)
        if not principal:
            raise HTTPException(
                status_code=status.HTTP_401_UNAUTHORIZED,
                detail='Geçersiz, süresi dolmuş ya da iptal edilmiş API jetonu',
            )
        if principal['role'] == 'viewer' and request.method not in CSRF_SAFE_METHODS:
            raise HTTPException(
                status_code=status.HTTP_403_FORBIDDEN, detail='Görüntüleyici jetonu yalnızca okuma (GET) yapabilir.'
            )
        return principal
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
    # Bütün panel uçları (require_admin, require_superadmin, modül denetimi) buradan geçer: demo hesabının yazma
    # isteği rolüne bakılmadan tek yerde reddedilir (kendi şifresi, 2FA'sı dahil)
    if (
        session['sub'] in DEMO_USERS
        and request.method not in CSRF_SAFE_METHODS
        and request.url.path not in DEMO_READ_ONLY_POSTS
    ):
        raise HTTPException(status_code=status.HTTP_403_FORBIDDEN, detail=DEMO_DENIED)
    return session


async def require_admin(payload: dict = Depends(require_auth)):
    if payload.get('role') not in ['admin', 'superadmin']:
        raise HTTPException(status_code=status.HTTP_403_FORBIDDEN, detail="Bu islem icin admin yetkisi gereklidir.")
    return payload


async def require_superadmin(payload: dict = Depends(require_auth)):
    # API jetonunun rolü hiçbir zaman superadmin değildir; yine de açıkça reddedilir
    if payload.get('role') != 'superadmin' or is_api_token(payload):
        raise HTTPException(
            status_code=status.HTTP_403_FORBIDDEN, detail="Bu islem icin superadmin yetkisi gereklidir."
        )
    return payload


_SESSION_ONLY = "Bu işlem yalnızca panel oturumuyla yapılır; API jetonu kullanılamaz."


async def require_user_session(payload: dict = Depends(require_auth)):
    """Panel kullanıcısının kendi oturumu (her rol); API jetonu reddedilir. Kullanıcının kendi 2FA ayarları gibi."""
    if is_api_token(payload):
        raise HTTPException(status_code=status.HTTP_403_FORBIDDEN, detail=_SESSION_ONLY)
    return payload


async def require_admin_session(payload: dict = Depends(require_admin)):
    """Panel oturumundaki admin/superadmin; API jetonu reddedilir. Kullanıcı listesi ve uzak ekran/uzaktan girdi
    (oturumu açan kişinin panel soketine bağlıdır, otomasyonla kullanılmaz)."""
    if is_api_token(payload):
        raise HTTPException(status_code=status.HTTP_403_FORBIDDEN, detail=_SESSION_ONLY)
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
