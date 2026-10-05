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


# Salt okunur demo hesapları (virgülle ayrılmış kullanıcı adları, ör. "demo"): bu hesaplar oturum açar ve okur, ama
# hiçbir şeyi değiştiremez (şifre, 2FA, ayar, komut). Bkz. security.require_auth ve docs/configuration.md.
DEMO_USERS = frozenset(u.strip() for u in os.environ.get('POPS_DEMO_USERS', '').split(',') if u.strip())


# ──────────────────────────────────────────────────────────────────────────────


# Backend/ kökü (bu dosya Backend/pops/ altında) — storage/updates yolları taşınmadan önceki ile AYNI
BASE_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


# Yükleme klasörü sabit ve çözümlenmiş (realpath) bir yoldur; kullanıcı girdisinden türetilmez
UPLOAD_DIR = os.path.realpath(os.path.join(BASE_DIR, "storage"))


UPDATES_DIR = os.path.join(BASE_DIR, "updates")


# Dosya aktarımı (bkz. pops/filestore.py): bilgisayara gönderilen ve bilgisayardan alınan dosyalar. Yükleme
# klasörünün yanında durur ve statik sunulmaz; dosyaları yalnızca hedef ajan (tek kullanımlık jetonla) ve admin indirir.
FILES_DIR = os.path.realpath(os.environ.get('POPS_FILES_DIR') or os.path.join(BASE_DIR, "transfers"))


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


# Süre sınırları (saniye). Veritabanı yanıt vermezse istekler ve WebSocket işleyicileri sonsuza dek beklemesin:
# havuzdan bağlantı alma, bağlantı kurma ve tek sorgu. İşlem içinde boşta bekleyen oturum da kilit tutmasın diye
# sunucu tarafında kesilir. Migration'lar bu sınırların dışında, ayrı bağlantıda çalışır.
DB_ACQUIRE_TIMEOUT = float(os.environ.get('DB_ACQUIRE_TIMEOUT', '10'))


DB_CONNECT_TIMEOUT = float(os.environ.get('DB_CONNECT_TIMEOUT', '10'))


DB_COMMAND_TIMEOUT = float(os.environ.get('DB_COMMAND_TIMEOUT', '30'))


DB_IDLE_IN_TRANSACTION_MS = int(os.environ.get('DB_IDLE_IN_TRANSACTION_MS', '60000'))


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


# GLPI dışa aktarımı (pops/glpi.py): hedef adres webhook gibi denetlenir; iç ağ, loopback ve link-local adresler
# reddedilir. Okul ağındaki bir GLPI için bilerek açın: 1. Açıkken, adresin hepsi iç ağdaysa http:// da kabul edilir
# (internetteki bir GLPI'ye her zaman https:// ile, sertifikası doğrulanarak bağlanılır).
GLPI_ALLOW_PRIVATE = os.environ.get('GLPI_ALLOW_PRIVATE', '').strip().lower() in ('1', 'true', 'yes')


# GLPI'nin sertifikası kurumun kendi sertifika otoritesinden ise o otoritenin sertifikası (PEM dosyasının yolu);
# boşsa sistemin güvendiği sertifikalar kullanılır.
GLPI_CA_FILE = os.environ.get('GLPI_CA_FILE', '').strip()


# Prometheus /metrics ucu yalnızca bu jeton tanımlıysa açılır (en az 16 karakter) ve
# "Authorization: Bearer <jeton>" ister; tanımlı değilse uç 404 döner.
METRICS_TOKEN = os.environ.get('METRICS_TOKEN', '').strip()


# Yalnızca otomatik testler: kimlik sağlayıcısı ayarındaki allow_insecure_for_tests (şifresiz LDAP, http OIDC
# sağlayıcısı) ancak sunucu bu değişkenle (1) başlatıldıysa kabul edilir. Üretimde tanımlamayın.
SSO_ALLOW_INSECURE_FOR_TESTS = os.environ.get('POPS_SSO_ALLOW_INSECURE_FOR_TESTS', '').strip() == '1'
