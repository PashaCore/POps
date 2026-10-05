"""Dosya aktarımının sunucu tarafı deposu (uçlar: pops/routers/files.py): dosyalar, adlar, yol denetimi, temizlik.

Dosyalar FILES_DIR altında yalnızca sunucunun ürettiği adlarla durur (push-<yükleme>.bin, pull-<aktarım>.bin);
yöneticinin ya da ajanın verdiği ad yalnızca veritabanında ve indirme başlığında kullanılır. Klasör statik sunulmaz:
gönderilen dosyayı yalnızca hedef ajan tek kullanımlık jetonla, alınan dosyayı yalnızca admin panelden indirir.

Temizlik (zamanlayıcı, en fazla 5 dakikada bir; purge()):
  * jetonu kullanılmadan süresi dolan aktarım "expired" olur;
  * gönderilen dosya, o yüklemenin bütün jetonları kullanıldıktan 10 dk sonra ya da hepsinin süresi dolunca silinir;
  * bilgisayardan alınan dosya PULL_KEEP_DAYS (7) gün sonra silinir; satır kalır, indirme bağlantısı kalkar;
  * hiçbir satırın göstermediği ve bir günden eski dosya (yarıda kalan yükleme) silinir.
Satırların kendisi kayıt saklama süresiyle (retention_days_tasks) silinir (bkz. pops/retention.py).
"""

import asyncio
import hashlib
import logging
import os
import re
import tempfile
import time
import unicodedata
from typing import Optional, Tuple

from pops import db
from pops.config import FILES_DIR

log = logging.getLogger("pops.files")

# Sınırlar (ileride Sistem ayarı olabilir; şimdilik sabit)
PUSH_MAX_BYTES = 200 * 1024 * 1024
PULL_MAX_BYTES = 200 * 1024 * 1024
TOKEN_TTL_SECONDS = 3600
PULL_KEEP_DAYS = 7
PUSH_KEEP_AFTER_USE_SECONDS = 600
ORPHAN_SECONDS = 24 * 3600
PURGE_EVERY_SECONDS = 300

# Ajanın yalnızca allow_exec ile yazdığı türler (sözleşme). Sunucu da aynı kuralı uygular: işaretsiz istek reddedilir.
EXEC_EXTENSIONS = (".lnk", ".url", ".scr")

_BLOB_RE = re.compile(r"^(push|pull)-[A-Za-z0-9_-]{8,64}\.bin$")
_ID_RE = re.compile(r"^[A-Za-z0-9_-]{8,64}$")
_WIN_RESERVED = {"CON", "PRN", "AUX", "NUL"} | {"COM%d" % i for i in range(1, 10)} | {"LPT%d" % i for i in range(1, 10)}
_NAME_BAD = set('<>:"/\\|?*')
_PATH_BAD = set('<>:"|?*')


def valid_id(value) -> bool:
    """Aktarım ya da yükleme kimliği biçimi (URL'de ve dosya adında kullanılır)."""
    return isinstance(value, str) and bool(_ID_RE.match(value))


def _strip_controls(text: str) -> str:
    # Denetim ve biçim karakterleri (U+202E gibi yön değiştiriciler adı "fdp.exe" gibi gösterebilir) atılır
    return "".join(ch for ch in text if unicodedata.category(ch)[0] != "C")


def clean_name(raw) -> Optional[str]:
    """Bilgisayara yazılacak dosya adı: yol parçaları, Windows'ta geçersiz ve görünmez karakterler atılır, ayrılmış
    adlar (CON, NUL, COM1…) önek alır, en fazla 200 karakter (uzantı korunur). Türkçe harfler kalır. Kullanılamazsa
    None."""
    name = unicodedata.normalize("NFC", str(raw or ""))
    name = name.replace("\\", "/").rsplit("/", 1)[-1]
    name = "".join(ch for ch in _strip_controls(name) if ch not in _NAME_BAD)
    name = name.strip().rstrip(". ")
    if not name or set(name) <= {"."}:
        return None
    if name.split(".", 1)[0].strip().upper() in _WIN_RESERVED:
        name = "_" + name
    if len(name) > 200:
        base, ext = os.path.splitext(name)
        ext = ext[:20]
        name = base[: 200 - len(ext)].rstrip(". ") + ext
    return name


def needs_exec(name: str) -> bool:
    return os.path.splitext(name or "")[1].lower() in EXEC_EXTENSIONS


def check_pull_path(raw) -> str:
    """Bilgisayardan istenecek dosyanın yolu: sürücü harfiyle başlayan tam yol. Ağ yolu (\\\\sunucu\\paylaşım; ajan
    SYSTEM hesabıyla başka bir makineye bağlanmasın), aygıt yolu (\\\\?\\, \\\\.\\), '..', joker, alternatif veri
    akışı (dosya.txt:akış) ve denetim karakteri reddedilir. Geçersizse ValueError (Türkçe ileti)."""
    path = str(raw or "").strip().replace("/", "\\")
    if not path:
        raise ValueError("Dosyanın yolu gerekli.")
    if len(path) > 1024:
        raise ValueError("Yol çok uzun (en fazla 1024 karakter).")
    if not re.match(r"^[A-Za-z]:\\", path):
        raise ValueError("Tam yol yazın (ör. C:\\Users\\Public\\Documents\\rapor.pdf). Ağ yolları kabul edilmez.")
    if _strip_controls(path) != path:
        raise ValueError("Yolda geçersiz karakter var.")
    rest = path[3:]
    if any(ch in _PATH_BAD for ch in rest):
        raise ValueError('Yolda kullanılamayan karakter var (< > : " | ? *).')
    parts = rest.split("\\")
    if any(p in (".", "..") for p in parts):
        raise ValueError("Yolda '.' ya da '..' kullanılamaz.")
    if not parts[-1] or any(not p for p in parts):
        raise ValueError("Yol bir dosyayı göstermeli (klasör değil).")
    return path


def name_of_path(path: str) -> str:
    return clean_name(path.replace("\\", "/").rsplit("/", 1)[-1]) or "dosya"


def ensure_dir() -> str:
    os.makedirs(FILES_DIR, mode=0o750, exist_ok=True)
    return FILES_DIR


def blob_path(name: Optional[str]) -> Optional[str]:
    """Saklanan dosyanın tam yolu; ad sunucunun ürettiği biçimde değilse None (kayıt bozulsa da klasör dışına
    çıkılmaz)."""
    if not name or not _BLOB_RE.match(name):
        return None
    path = os.path.join(FILES_DIR, name)
    return path if os.path.dirname(os.path.realpath(path)) == FILES_DIR else None


class TooLarge(Exception):
    pass


def _new_temp() -> Tuple[int, str]:
    ensure_dir()
    return tempfile.mkstemp(dir=FILES_DIR, prefix=".upload-")


def _finish(tmp: str, name: str) -> None:
    os.chmod(tmp, 0o640)
    os.replace(tmp, os.path.join(FILES_DIR, name))


def _drop(tmp: Optional[str]) -> None:
    if tmp:
        try:
            os.unlink(tmp)
        except OSError:
            pass


def store_file(src, name: str, limit: int) -> Tuple[int, str]:
    """Dosya nesnesini (ör. yüklemenin geçici dosyası) geçici ada yazarken boyutu ve SHA-256'yı çıkarır, bitince
    yerine taşır (yarım dosya indirilemez). limit aşılırsa TooLarge, hiçbir şey kalmaz. İş parçacığında çağrılır."""
    h = hashlib.sha256()
    size = 0
    fd, tmp = _new_temp()
    try:
        with os.fdopen(fd, "wb") as out:
            for chunk in iter(lambda: src.read(1024 * 1024), b""):
                size += len(chunk)
                if size > limit:
                    raise TooLarge()
                h.update(chunk)
                out.write(chunk)
        _finish(tmp, name)
    except BaseException:
        _drop(tmp)
        raise
    return size, h.hexdigest()


async def store_stream(chunks, name: str, limit: int) -> Tuple[int, str]:
    """Ham istek gövdesini (async parça akışı) diske yazar; store_file gibi. Disk yazımı iş parçacığında yapılır
    (sunucunun diski zaman zaman takılır, olay döngüsü beklemesin); parçalar 1 MB'a kadar biriktirilir."""
    h = hashlib.sha256()
    size = 0
    fd, tmp = await asyncio.to_thread(_new_temp)
    out = os.fdopen(fd, "wb")
    try:
        buf = bytearray()
        async for chunk in chunks:
            if not chunk:
                continue
            size += len(chunk)
            if size > limit:
                raise TooLarge()
            h.update(chunk)
            buf += chunk
            if len(buf) >= 1024 * 1024:
                await asyncio.to_thread(out.write, bytes(buf))
                buf.clear()
        if buf:
            await asyncio.to_thread(out.write, bytes(buf))
        await asyncio.to_thread(out.close)
        await asyncio.to_thread(_finish, tmp, name)
    except BaseException:
        out.close()
        await asyncio.to_thread(_drop, tmp)
        raise
    return size, h.hexdigest()


def remove_blob(name: Optional[str]) -> bool:
    path = blob_path(name)
    if not path:
        return False
    try:
        os.unlink(path)
        return True
    except FileNotFoundError:
        return False


# ── Temizlik ────────────────────────────────────────────────────────────────────

async def _claim_blobs(where: str, params: tuple) -> list:
    """Koşula uyan satırların dosyasını bırakır (storage_path = NULL, purged_at) ve eski adları döndürür. Satırlar
    kilitlenerek alınır: aynı anda çalışan iki temizlik aynı dosyayı iki kez saymaz."""
    rows = await db.execute_query(
        "WITH old AS (SELECT id, storage_path FROM file_transfers WHERE storage_path IS NOT NULL AND (" + where + ") "
        "FOR UPDATE SKIP LOCKED) "
        "UPDATE file_transfers f SET storage_path = NULL, purged_at = NOW() FROM old WHERE f.id = old.id "
        "RETURNING old.storage_path AS blob",
        params,
        fetch=True,
    )
    return [r["blob"] for r in rows or []]


def _orphans(referenced: set) -> int:
    removed = 0
    now = time.time()
    try:
        entries = list(os.scandir(FILES_DIR))
    except FileNotFoundError:
        return 0
    for e in entries:
        if not e.is_file(follow_symlinks=False) or e.name in referenced:
            continue
        if not (_BLOB_RE.match(e.name) or e.name.startswith(".upload-")):
            continue
        try:
            if now - e.stat(follow_symlinks=False).st_mtime > ORPHAN_SECONDS:
                os.unlink(e.path)
                removed += 1
        except OSError:
            pass
    return removed


async def purge() -> dict:
    """Süresi dolan jetonlar, gönderilmiş ve saklama süresi dolan dosyalar (bkz. modül açıklaması). Dönen: sayılar."""
    out = {}
    expired = await db.execute_query(
        "UPDATE file_transfers SET status = 'expired', finished_at = NOW(), "
        "detail = COALESCE(detail, 'Jeton süresi içinde kullanılmadı.') "
        "WHERE status = 'sent' AND token_used_at IS NULL AND token_expires_at < NOW() RETURNING id",
        fetch=True,
    )
    out["expired"] = len(expired or [])
    # Gönderilen dosya: aynı yüklemenin (batch) hiçbir jetonu hâlâ geçerli ve kullanılmamış değilse, son kullanım da
    # 10 dk'dan eskiyse (indirme sürüyor olabilir)
    push = await _claim_blobs(
        "direction = 'push' AND NOT EXISTS (SELECT 1 FROM file_transfers o WHERE o.batch_id = file_transfers.batch_id "
        "AND ((o.token_used_at IS NULL AND o.token_expires_at > NOW()) "
        "OR o.token_used_at > NOW() - make_interval(secs => $1)))",
        (PUSH_KEEP_AFTER_USE_SECONDS,),
    )
    pull = await _claim_blobs(
        "direction = 'pull' AND created_at < NOW() - make_interval(days => $1)", (PULL_KEEP_DAYS,)
    )
    deleted = 0
    for name in set(push) | set(pull):
        if await asyncio.to_thread(remove_blob, name):
            deleted += 1
    out["push_files"] = len(set(push))
    out["pull_files"] = len(set(pull))
    refs = await db.execute_query(
        "SELECT DISTINCT storage_path FROM file_transfers WHERE storage_path IS NOT NULL", fetch=True
    )
    out["orphans"] = await asyncio.to_thread(_orphans, {r["storage_path"] for r in refs or []})
    if out["expired"] or deleted or out["orphans"]:
        log.info("dosya aktarımı temizliği", extra=out)
    return out


_last_purge = [float("-inf")]


async def purge_periodic() -> None:
    """Zamanlayıcı her turda çağırır; en fazla PURGE_EVERY_SECONDS'ta bir çalışır."""
    if time.monotonic() - _last_purge[0] < PURGE_EVERY_SECONDS:
        return
    _last_purge[0] = time.monotonic()
    await purge()
