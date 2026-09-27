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


# Bağlantı havuzu. Paylaşılan bir PostgreSQL'de (varsayılan max_connections=100) havuz, sunucunun toplam
# sınırını tek başına tüketmesin: sorgular kısa ve asenkron olduğu için 20 bağlantı yüzlerce ajana yeter;
# havuz doluysa istekler sıra bekler (hata vermez). Yük artarsa max_connections ile birlikte artırın.
DB_POOL_MIN = int(os.environ.get('DB_POOL_MIN', '2'))


DB_POOL_MAX = int(os.environ.get('DB_POOL_MAX', '20'))


# Wake-on-LAN yayın hedefi ('<broadcast>' = 255.255.255.255)
WOL_BROADCAST_ADDR = os.environ.get('WOL_BROADCAST_ADDR') or '<broadcast>'


WOL_PORT = int(os.environ.get('WOL_PORT', '9'))


# ─── Bildirimler (e-posta) ────────────────────────────────────────────────────
# SMTP gizli bilgileri .env'de durur (veritabanına yazılmaz). Alıcılar ve webhook adresi panelden ayarlanır.
SMTP_HOST = os.environ.get('SMTP_HOST', '').strip()


SMTP_PORT = int(os.environ.get('SMTP_PORT', '587'))


SMTP_USER = os.environ.get('SMTP_USER', '').strip()


SMTP_PASS = os.environ.get('SMTP_PASS', '')


SMTP_FROM = os.environ.get('SMTP_FROM', '').strip() or SMTP_USER


# starttls (587) | ssl (465) | none (yalnızca yerel/güvenilir ağdaki relay için)
SMTP_SECURITY = os.environ.get('SMTP_SECURITY', 'starttls').strip().lower()


# Webhook yalnızca internetteki (genel) adreslere gider; iç ağ, loopback, link-local ve bulut metadata
# adresleri reddedilir (SSRF). Okul içindeki bir sisteme göndermek için bilerek açın: 1
NOTIFY_WEBHOOK_ALLOW_PRIVATE = os.environ.get('NOTIFY_WEBHOOK_ALLOW_PRIVATE', '').strip().lower() in (
    '1',
    'true',
    'yes',
)
