"""API jetonları: otomasyonun (betik, izleme, dış sistem) /api/v1'i panel girişi olmadan kullanması için.

Yalnızca süper admin oluşturur, listeler ve iptal eder (require_superadmin API jetonunu da reddeder: jeton jeton
yönetemez). Jeton "pops_" + 32 bayt rastgeledir, yalnızca oluşturulurken bir kez döner; veritabanında SHA-256 özeti
ve tanımak için ilk 8 karakteri kalır. Doğrulama ve yetki: pops/security.py (verify_api_token). Oluşturma ve iptal
hash-zincirli denetim kaydına yazılır. Bkz. docs/api.md "API tokens", docs/decisions.md D-21.
"""

import re

import asyncpg
from fastapi import APIRouter, Depends, HTTPException

from pops import timeutil
from pops.audit import add_audit_log
from pops.db import execute_query
from pops.models import ApiTokenCreateInput
from pops.security import API_TOKEN_PREFIX, api_token_hash, new_api_token, require_superadmin

router = APIRouter()

# Ad görev ve denetim kayıtlarına "token:<ad>" olarak yazılır: harf, rakam, boşluk, nokta, alt çizgi, tire
_NAME_RE = re.compile(r"[\w .-]{1,64}")
_PREFIX_LEN = 8
_COLUMNS = (
    "id, name, role, token_prefix, created_by, created_at, expires_at, last_used_at, revoked_at, "
    "CASE WHEN revoked_at IS NOT NULL THEN 'revoked' WHEN expires_at <= NOW() THEN 'expired' "
    "ELSE 'active' END AS state"
)


@router.get("/api/tokens")
async def list_tokens(auth: dict = Depends(require_superadmin)):
    """Bütün jetonlar, yeniden eskiye (iptal edilmiş ve süresi dolmuşlar da). Jetonun kendisi hiçbir zaman dönmez."""
    rows = await execute_query("SELECT %s FROM api_tokens ORDER BY id DESC LIMIT 500" % _COLUMNS, fetch=True)
    return [timeutil.iso_row(r) for r in rows or []]


@router.post("/api/tokens")
async def create_token(data: ApiTokenCreateInput, auth: dict = Depends(require_superadmin)):
    name = data.name.strip()
    if not _NAME_RE.fullmatch(name):
        raise HTTPException(
            status_code=400,
            detail="Ad en çok 64 karakter olmalı; harf, rakam, boşluk, nokta, alt çizgi ve tire içerebilir.",
        )
    token = new_api_token()
    prefix = token[len(API_TOKEN_PREFIX):len(API_TOKEN_PREFIX) + _PREFIX_LEN]
    try:
        rows = await execute_query(
            "INSERT INTO api_tokens (name, token_hash, token_prefix, role, created_by, expires_at) "
            "VALUES ($1, $2, $3, $4, $5, "
            "CASE WHEN $6::int IS NULL THEN NULL ELSE NOW() + make_interval(days => $6) END) "
            "RETURNING " + _COLUMNS,
            (name, api_token_hash(token), prefix, data.role, auth.get("sub"), data.expires_days),
            fetch=True,
        )
    except asyncpg.UniqueViolationError:
        raise HTTPException(
            status_code=409, detail="Bu adla bir jeton zaten var (iptal edilmiş olsa da); başka bir ad seçin."
        )
    row = rows[0]
    await add_audit_log(
        "*",
        "api_token_created",
        "API jetonu oluşturuldu: %s (%s)" % (name, data.role),
        {
            "id": row["id"], "name": name, "role": data.role, "prefix": prefix, "by": auth.get("sub"),
            "expires_at": timeutil.iso(row["expires_at"]),
        },
    )
    # Jeton yalnızca bu yanıtta görünür
    return {"status": "success", "token": token, **timeutil.iso_row(row)}


@router.delete("/api/tokens/{token_id}")
async def revoke_token(token_id: int, auth: dict = Depends(require_superadmin)):
    """İptal anında geçerlidir: jeton her istekte veritabanına karşı denetlenir. Kayıt silinmez (kim, ne zaman)."""
    rows = await execute_query(
        "UPDATE api_tokens SET revoked_at = NOW() WHERE id = $1 AND revoked_at IS NULL RETURNING name, role",
        (token_id,),
        fetch=True,
    )
    if not rows:
        exists = await execute_query("SELECT 1 FROM api_tokens WHERE id = $1", (token_id,), fetch=True)
        if not exists:
            raise HTTPException(status_code=404, detail="Jeton bulunamadı.")
        return {"status": "success", "already_revoked": True}
    await add_audit_log(
        "*",
        "api_token_revoked",
        "API jetonu iptal edildi: %s" % rows[0]["name"],
        {"id": token_id, "name": rows[0]["name"], "role": rows[0]["role"], "by": auth.get("sub")},
    )
    return {"status": "success", "already_revoked": False}
