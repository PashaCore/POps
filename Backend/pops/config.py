"""Ortam/yapılandırma sabitleri (.env, JWT, yollar, WoL). Başka pops modülüne bağımlı değildir."""

import os


from dotenv import load_dotenv

load_dotenv()  # ortam değişkenleri bu modül ilk import edildiğinde yüklenir


def require_env(name: str) -> str:
    """Zorunlu ortam değişkenini oku; tanımlı değilse sunucu açıklayıcı bir hatayla durur."""
    value = os.environ.get(name)
    if not value:
        raise RuntimeError(f"Ortam değişkeni tanımlı değil: {name} (bkz. .env.example)")
    return value


# ─── Güvenlik Sabitleri ────────────────────────────────────────────────────────
# Şifre, anahtar ve IP gibi ortama özel değerler koda gömülmez; .env / os.environ'dan okunur.
JWT_SECRET = require_env('JWT_SECRET')


JWT_ALGO = 'HS256'


JWT_EXPIRE_H = int(os.environ.get('JWT_EXPIRE_HOURS', '12'))  # Token ömrü (saat)


# Çevrimdışı bypass kodları için ajanlarla paylaşılan gizli anahtar (ajan: BypassSecret / POPS_BYPASS_SECRET)
BYPASS_SECRET = os.environ.get('BYPASS_SECRET', '').strip()


# Panel JWT'yi httpOnly çerezde taşır (JS erişemez); diğer istemciler Authorization: Bearer kullanabilir
JWT_COOKIE_NAME = 'pops_jwt'


CSRF_SAFE_METHODS = {"GET", "HEAD", "OPTIONS"}


# ──────────────────────────────────────────────────────────────────────────────


# Backend/ kökü (bu dosya Backend/pops/ altında) — storage/updates yolları taşınmadan önceki ile AYNI
BASE_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


# Yükleme klasörü sabit ve çözümlenmiş (realpath) bir yoldur; kullanıcı girdisinden türetilmez
UPLOAD_DIR = os.path.realpath(os.path.join(BASE_DIR, "storage"))


UPDATES_DIR = os.path.join(BASE_DIR, "updates")


USE_V2_SCHEMA = True


# Ajan loglarının yazıldığı tablo; istatistik ve silme işlemleri de bunu kullanır
LOG_TABLE = "agent_logs_v2" if USE_V2_SCHEMA else "agent_logs"


# Parametreler ayrı verilir; şifredeki '@', ':' gibi karakterler DSN'i bozmaz
DB_CONFIG = {
    "host": os.environ.get('DB_HOST', 'localhost'),
    "port": int(os.environ.get('DB_PORT', '5432')),
    "user": require_env('DB_USER'),
    "password": require_env('DB_PASS'),
    "database": require_env('DB_NAME'),
}


# Wake-on-LAN yayın hedefi ('<broadcast>' = 255.255.255.255)
WOL_BROADCAST_ADDR = os.environ.get('WOL_BROADCAST_ADDR') or '<broadcast>'


WOL_PORT = int(os.environ.get('WOL_PORT', '9'))
