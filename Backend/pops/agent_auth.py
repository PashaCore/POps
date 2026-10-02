"""Ajan kimlik doğrulaması: enroll token, cihaz-başı secret, HTTP uçlarında kimlik-hedef bağlama."""

import hashlib
import hmac
from typing import Optional

from fastapi import HTTPException, Request, status

from pops.db import execute_query


# ── Ajan kimlik doğrulama (Faz 3): enroll token + per-cihaz secret ──────────────


def _hash_secret(secret: str) -> str:
    return hashlib.sha256(secret.encode('utf-8')).hexdigest()


async def enforce_agent_auth_enabled() -> bool:
    """global_settings.enforce_agent_auth = '1' ise kimliksiz ajan bağlantıları reddedilir. Ayar yoksa KAPALI
    (eski kurulumlar; yeni kurulumda migration 0014 açık başlatır). Ayar okunamazsa (veritabanı hatası) zorlama
    AÇIK sayılır: kimlik kontrolü hata verince kapı açık kalmamalı."""
    try:
        rows = await execute_query("SELECT value FROM global_settings WHERE key='enforce_agent_auth'", fetch=True)
    except Exception:
        return True
    return bool(rows and str(rows[0]["value"]) == '1')


async def verify_agent_secret(pc_name: str, secret: Optional[str]) -> bool:
    """Ajanın sunduğu secret, o cihaz için saklanan SHA-256 hash ile sabit-zamanlı karşılaştırılır."""
    if not secret:
        return False
    try:
        rows = await execute_query("SELECT secret_hash FROM agent_secrets WHERE pc_name=$1", (pc_name,), fetch=True)
    except Exception:
        return False
    if not rows:
        return False
    return hmac.compare_digest(str(rows[0]["secret_hash"]), _hash_secret(secret))


def hash_enroll_token(token: str) -> str:
    """Kayıt jetonları veritabanında yalnızca SHA-256 özetiyle tutulur (migration 0014)."""
    return hashlib.sha256(token.encode('utf-8')).hexdigest()


async def valid_enroll_token(token: Optional[str]) -> Optional[dict]:
    """Kullanılmamış ve süresi dolmamış enroll jetonunu döndürür (henüz tüketmez); yoksa None. Tüketim, anahtar
    üretimiyle aynı işlemde ve koşullu yapılır (bkz. routers/agents.py _enroll)."""
    if not token or len(token) > 200:
        return None
    try:
        rows = await execute_query(
            "SELECT id, lab_name FROM enroll_tokens "
            "WHERE token_hash=$1 AND NOT is_used AND expires_at > NOW() AND use_count < max_uses",
            (hash_enroll_token(token),),
            fetch=True,
        )
    except Exception:
        return None
    return rows[0] if rows else None


async def agent_http_auth(request: Request) -> Optional[str]:
    """Ajan HTTP uçları (inventory/logs/auth/policy_alert) için accept-both kimlik.
    DOĞRULANAN X-Agent-Id'yi döndürür (uçlar bunu hedef pc_name/hw_id ile karşılaştırıp
    cross-device sahteciliği engeller — bkz. _bind_agent). Geçerli X-Agent-Secret sunulursa
    kimlik döner. enforce_agent_auth AÇIKKEN geçerli secret ZORUNLU (yoksa 401). KAPALIYKEN
    (varsayılan) eksik/geçersiz secret legacy kabul edilir (None döner, bağlama yapılamaz) —
    böylece mevcut/secret'sız ajanlar düşmez. Panel uçları bundan etkilenmez."""
    hwid = request.headers.get("X-Agent-Id")
    secret = request.headers.get("X-Agent-Secret")
    if hwid and secret and await verify_agent_secret(hwid, secret):
        return hwid
    if await enforce_agent_auth_enabled():
        raise HTTPException(status_code=status.HTTP_401_UNAUTHORIZED, detail="Ajan kimlik dogrulamasi gerekli")
    return None


def _bind_agent(agent_id: Optional[str], target: str):
    """Doğrulanan ajan kimliğini hedef cihazla eşle. Eşleşmezse 403. agent_id None ise (legacy,
    enforce kapalı) bağlama yapılamaz — accept-both'un kabul ettiği artık risk; enforce açılınca kapanır."""
    if agent_id is not None and agent_id != target:
        raise HTTPException(status_code=status.HTTP_403_FORBIDDEN, detail="Ajan kimliği hedef cihazla eşleşmiyor")
