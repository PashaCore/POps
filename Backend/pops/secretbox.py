"""Veritabanında saklanan gizli değerlerin şifrelenmesi (R-12): bugün panel kullanıcılarının TOTP (2FA) anahtarı.

Amaç: yalnızca veritabanı sızan biri (yedek dosyası, SQL okuma açığı) 2FA anahtarlarını kullanamasın. Anahtar
veritabanında değil sunucunun ortamındadır:
  * TOTP_ENCRYPTION_KEY (.env; Fernet anahtarı: 32 baytın urlsafe base64'ü) tanımlıysa o kullanılır;
  * tanımlı değilse JWT_SECRET'tan HKDF ile türetilen anahtar kullanılır (kurulum değişmeden çalışır).
Çözme her iki anahtarla da denenir; açılışta (reseal_totp_secrets) düz metin kalan ya da eski anahtarla şifreli
değerler birincil anahtarla yeniden şifrelenir. Böylece TOTP_ENCRYPTION_KEY sonradan eklenince bir yeniden
başlatma yeter; ondan sonra JWT_SECRET değiştirilebilir (önce anahtar eklenmezse 2FA anahtarları çözülemez).

Biçim: "v1:" + Fernet jetonu. Öneki olmayan değer eski (düz metin) kayıttır ve okunabilir.
"""

import base64
import logging
import os
from typing import Optional

from cryptography.fernet import Fernet, InvalidToken, MultiFernet
from cryptography.hazmat.primitives import hashes
from cryptography.hazmat.primitives.kdf.hkdf import HKDF

from pops.config import JWT_SECRET

log = logging.getLogger("pops.secretbox")

PREFIX = "v1:"


def _derived_key() -> bytes:
    raw = HKDF(algorithm=hashes.SHA256(), length=32, salt=None, info=b"pops/totp-secret/v1").derive(
        JWT_SECRET.encode("utf-8")
    )
    return base64.urlsafe_b64encode(raw)


def _build():
    keys = []
    explicit = os.environ.get("TOTP_ENCRYPTION_KEY", "").strip()
    if explicit:
        try:
            keys.append(Fernet(explicit))
        except (ValueError, TypeError) as exc:
            raise RuntimeError(
                "TOTP_ENCRYPTION_KEY geçersiz: 32 baytın urlsafe base64'ü olmalı "
                "(python -c 'from cryptography.fernet import Fernet; print(Fernet.generate_key().decode())')"
            ) from exc
    keys.append(Fernet(_derived_key()))
    return keys[0], MultiFernet(keys)


_primary, _box = _build()


def seal(plain: str) -> str:
    return PREFIX + _primary.encrypt(plain.encode("utf-8")).decode("ascii")


def unseal(stored: Optional[str]) -> Optional[str]:
    """Saklanan değerin açık hâli; çözülemezse (anahtar değişmiş) None ve hata logu."""
    if not stored:
        return None
    if not stored.startswith(PREFIX):
        return stored  # eski düz metin kayıt (açılışta şifrelenir)
    try:
        return _box.decrypt(stored[len(PREFIX):].encode("ascii")).decode("utf-8")
    except (InvalidToken, ValueError):
        log.error("şifreli değer çözülemedi: TOTP_ENCRYPTION_KEY ya da JWT_SECRET değişmiş olabilir")
        return None


def needs_reseal(stored: Optional[str]) -> bool:
    """Düz metin ya da birincil olmayan anahtarla şifreli mi."""
    if not stored:
        return False
    if not stored.startswith(PREFIX):
        return True
    try:
        _primary.decrypt(stored[len(PREFIX):].encode("ascii"))
        return False
    except (InvalidToken, ValueError):
        return True


async def reseal_totp_secrets(execute_query) -> int:
    """Açılışta: düz metin ya da eski anahtarla şifreli TOTP anahtarlarını birincil anahtarla yeniden yazar."""
    rows = await execute_query("SELECT id, totp_secret FROM users WHERE totp_secret IS NOT NULL", fetch=True)
    changed = 0
    for r in rows or []:
        if not needs_reseal(r["totp_secret"]):
            continue
        plain = unseal(r["totp_secret"])
        if plain is None:
            continue  # çözülemedi: dokunma (hata zaten loglandı)
        await execute_query(
            "UPDATE users SET totp_secret = $1 WHERE id = $2 AND totp_secret = $3",
            (seal(plain), r["id"], r["totp_secret"]),
        )
        changed += 1
    if changed:
        log.info("TOTP anahtarları şifrelendi", extra={"count": changed})
    return changed
