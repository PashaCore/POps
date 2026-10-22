"""Sunucu saat dilimi ve zaman damgalarının biçimleri.

Zaman damgaları veritabanında TIMESTAMPTZ'dir (0031'e kadar eski tablolarda yerel saatli 'YYYY-AA-GG SS:DD:ss' metni
idi). Bu modül üç şeyi tek yerde toplar:

  * Sunucunun saat dilimi (zone()): gün sınırları, "bugün", CSV'deki okunur saat ve API'deki ISO 8601 metninin
    ofseti. Sırayla: POPS_TZ ortam değişkeni (IANA adı, ör. Europe/Istanbul), sürecin yerel saat dilimi (TZ ortam
    değişkeni ya da /etc/localtime), veritabanının TimeZone ayarı, UTC.
  * API çıktısı (iso()): sunucunun saat diliminde, saniye hassasiyetinde ve ofsetli ISO 8601, ör.
    "2026-10-05T11:58:11+03:00". Bütün zaman alanları bu biçimde döner.
  * Denetim zinciri (audit_zone(), local_text()): device_audit_logs özetleri zamanı eski metin biçiminde
    ('YYYY-AA-GG SS:DD:ss') içerir. Metin, saklanan değerden 0031'in eski metni okuduğu saat diliminde üretilir; o
    dilim veritabanında sabitlenir (global_settings.audit_time_zone) ki POPS_TZ sonradan değişse de eski kayıtların
    özeti tutsun.

Yalnızca standart kütüphaneyi kullanır: migrate.py (yalnızca asyncpg + python-dotenv kurulu CI işinde de) içe aktarır.
"""

import datetime
import logging
import os
from typing import Optional

try:
    from zoneinfo import ZoneInfo, ZoneInfoNotFoundError
except ImportError:  # pragma: no cover - Python 3.9+ her zaman içerir
    ZoneInfo = None
    ZoneInfoNotFoundError = Exception

log = logging.getLogger("pops.timeutil")

# Eski metin biçimi: denetim özeti, CSV ve ajan tepsisinin gösterdiği zaman
TEXT_FORMAT = "%Y-%m-%d %H:%M:%S"
# global_settings anahtarı: denetim zincirinin (ve 0031'de eski metinlerin) saat dilimi
AUDIT_ZONE_KEY = "audit_time_zone"

_UTC_NAMES = ("UTC", "Etc/UTC", "Etc/Universal", "Universal", "Zulu", "Etc/Zulu", "GMT", "Etc/GMT", "UCT", "Etc/UCT")

_state = {"name": None, "zone": None, "audit_name": None, "audit_zone": None}


def tzinfo_for(name: Optional[str]) -> Optional[datetime.tzinfo]:
    """IANA adının saat dilimi; tanınmıyorsa None."""
    name = (name or "").strip().lstrip(":")
    if not name:
        return None
    if name in _UTC_NAMES:
        return datetime.timezone.utc
    if ZoneInfo is None:
        return None
    try:
        return ZoneInfo(name)
    except (ZoneInfoNotFoundError, ValueError, OSError):
        return None


def _local_zone_name() -> Optional[str]:
    """Python sürecinin yerel saat diliminin IANA adı (TZ, /etc/localtime, /etc/timezone); bulunamazsa None."""
    tz_env = (os.environ.get("TZ") or "").strip().lstrip(":")
    if tz_env:
        if tz_env.startswith("/"):
            # TZ=/usr/share/zoneinfo/Europe/Istanbul biçimi
            marker = "zoneinfo/"
            idx = tz_env.find(marker)
            return tz_env[idx + len(marker):] if idx >= 0 else None
        return tz_env if tzinfo_for(tz_env) is not None else None
    try:
        target = os.path.realpath("/etc/localtime")
        marker = "/zoneinfo/"
        idx = target.find(marker)
        if idx >= 0 and os.path.exists(target):
            name = target[idx + len(marker):]
            if name.startswith(("posix/", "right/")):
                name = name.split("/", 1)[1]
            if tzinfo_for(name) is not None:
                return name
    except OSError:
        pass
    try:
        with open("/etc/timezone", "r", encoding="utf-8") as f:
            name = f.read().strip()
        if tzinfo_for(name) is not None:
            return name
    except OSError:
        pass
    if not os.path.exists("/etc/localtime") and not os.environ.get("TZ"):
        return "UTC"   # Docker ince imajları: /etc/localtime yoksa C kütüphanesi UTC kullanır
    return None


def detect_zone_name(db_timezone: Optional[str] = None) -> str:
    """Sunucunun saat dilimi adı: POPS_TZ > sürecin yerel dilimi > veritabanının TimeZone ayarı > UTC."""
    configured = (os.environ.get("POPS_TZ") or "").strip()
    if configured:
        if tzinfo_for(configured) is not None:
            return configured
        log.warning("POPS_TZ tanınmadı, yok sayıldı", extra={"POPS_TZ": configured})
    local = _local_zone_name()
    if local:
        return local
    if db_timezone and tzinfo_for(db_timezone) is not None:
        return db_timezone
    return "UTC"


def configure(db_timezone: Optional[str] = None, audit_zone_name: Optional[str] = None,
              name: Optional[str] = None) -> str:
    """Saat dilimlerini ayarlar (açılışta, veritabanına bağlanınca). Sunucu dilimi adını döner. name verilirse
    algılanmaz, o kullanılır."""
    name = name or detect_zone_name(db_timezone)
    _state["name"], _state["zone"] = name, tzinfo_for(name) or datetime.timezone.utc
    if audit_zone_name and tzinfo_for(audit_zone_name) is not None:
        _state["audit_name"], _state["audit_zone"] = audit_zone_name, tzinfo_for(audit_zone_name)
    return name


async def configure_from_db(conn) -> str:
    """configure(): veritabanının TimeZone ayarı ve sabitlenmiş denetim dilimiyle (asyncpg bağlantısı). Sorgularda
    AT TIME ZONE ile de kullanıldığı için PostgreSQL'in tanımadığı ad yerine veritabanının kendi dilimi alınır."""
    db_tz = await conn.fetchval("SHOW TimeZone")
    try:
        audit = await conn.fetchval("SELECT value FROM global_settings WHERE key = $1", AUDIT_ZONE_KEY)
    except Exception:   # 0031 öncesi şema (ör. eski yedeğin doğrulaması): sabit dilim yok
        audit = None
    name = configure(db_tz, audit)
    try:
        await conn.fetchval("SELECT now() AT TIME ZONE $1", name)
    except Exception:
        log.warning("saat dilimi PostgreSQL'de tanınmadı, veritabanınınki kullanılıyor",
                    extra={"zone": name, "db_zone": db_tz})
        name = configure(db_tz, audit, name=db_tz)
    return name


def zone_name() -> str:
    if _state["zone"] is None:
        configure()
    return _state["name"]


def zone() -> datetime.tzinfo:
    if _state["zone"] is None:
        configure()
    return _state["zone"]


def audit_zone_loaded() -> bool:
    return _state["audit_zone"] is not None


def set_audit_zone(name: Optional[str]) -> None:
    tz = tzinfo_for(name)
    if tz is not None:
        _state["audit_name"], _state["audit_zone"] = name, tz


def audit_zone() -> datetime.tzinfo:
    """Denetim özetinin saat dilimi: veritabanında sabitlenen; okunmadıysa sunucunun dilimi."""
    return _state["audit_zone"] or zone()


def now() -> datetime.datetime:
    """Şu an, sunucunun saat diliminde, saniye hassasiyetinde (eski metin biçimiyle aynı hassasiyet)."""
    return datetime.datetime.now(zone()).replace(microsecond=0)


def local_text(value, tz: Optional[datetime.tzinfo] = None) -> Optional[str]:
    """'YYYY-AA-GG SS:DD:ss' (sunucunun ya da verilen dilimde). Metin olduğu gibi döner; None -> None."""
    if value is None:
        return None
    if isinstance(value, datetime.datetime):
        if value.tzinfo is None:
            return value.strftime(TEXT_FORMAT)
        return value.astimezone(tz or zone()).strftime(TEXT_FORMAT)
    return str(value)


def iso(value) -> Optional[str]:
    """API biçimi: sunucunun saat diliminde ofsetli ISO 8601, saniye hassasiyeti. Tarih YYYY-AA-GG kalır."""
    if value is None:
        return None
    if isinstance(value, datetime.datetime):
        if value.tzinfo is None:
            value = value.replace(tzinfo=zone())
        return value.astimezone(zone()).isoformat(timespec="seconds")
    if isinstance(value, datetime.date):
        return value.isoformat()
    return str(value)


def iso_row(row) -> dict:
    """Satırdaki bütün tarih/zaman değerleri iso() biçiminde (SELECT * dönen uçlar)."""
    out = dict(row)
    for k, v in out.items():
        if isinstance(v, (datetime.datetime, datetime.date)):
            out[k] = iso(v)
    return out


def today() -> datetime.date:
    """Sunucunun saat dilimine göre bugünün tarihi."""
    return datetime.datetime.now(zone()).date()


def day_start(day: datetime.date) -> datetime.datetime:
    """Günün başlangıcı (00:00) sunucunun saat diliminde."""
    return datetime.datetime(day.year, day.month, day.day, tzinfo=zone())


def ago(days: float = 0, seconds: float = 0) -> datetime.datetime:
    """Şimdiden bu kadar önce (karşılaştırma için)."""
    return now() - datetime.timedelta(days=days, seconds=seconds)
