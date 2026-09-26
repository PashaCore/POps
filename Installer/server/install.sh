#!/usr/bin/env bash
# POps sunucusunu TEK KOMUTLA kurar — native, Docker GEREKMEZ (her sunucuda Python/Postgres olur).
# AlmaLinux/RHEL/Rocky (dnf) ve Debian/Ubuntu (apt) destekler. root ile çalıştırın:
#     sudo Installer/server/install.sh
#
# Yaptıkları: PostgreSQL + Python kur (yoksa) -> servis kullanıcısı -> DB + rol (parola üret)
# -> venv + pip install -> .env (secret'lar üretilir) -> migrate -> systemd birimi -> sağlık kontrolü.
# Web katmanı (Apache/nginx + TLS + PHP paneli) ortama özel olduğu için Installer/server/ altına
# örnek proxy yapılandırması bırakılır ve adımları yazılır (mevcut web sunucunuz ezilmesin diye).
set -euo pipefail

APP_DIR=${APP_DIR:-/opt/pops}
SVC_USER=${SVC_USER:-pops}
DB_NAME=${DB_NAME:-pops}
DB_USER=${DB_USER:-pops}
PORT=${PORT:-8000}
SRC=$(cd "$(dirname "$0")/../.." && pwd)   # repo kökü (Backend/ burada)

[ "$(id -u)" -eq 0 ] || { echo "root olarak çalıştırın (sudo)"; exit 1; }
[ -f "$SRC/Backend/server.py" ] || { echo "Backend/server.py bulunamadı ($SRC); repodan çalıştırın"; exit 1; }

echo "==> Paketler kuruluyor..."
if command -v dnf >/dev/null 2>&1; then
    dnf install -y python3 python3-pip postgresql-server postgresql >/dev/null
    [ -f /var/lib/pgsql/data/PG_VERSION ] || postgresql-setup --initdb >/dev/null 2>&1 || /usr/bin/postgresql-setup initdb >/dev/null 2>&1 || true
    systemctl enable --now postgresql
elif command -v apt-get >/dev/null 2>&1; then
    export DEBIAN_FRONTEND=noninteractive
    apt-get update -qq
    apt-get install -y -qq python3 python3-venv python3-pip postgresql >/dev/null
    systemctl enable --now postgresql
else
    echo "Desteklenmeyen dağıtım (dnf/apt yok). PostgreSQL + python3-venv'i elle kurup tekrar deneyin."; exit 1
fi

echo "==> Servis kullanıcısı: $SVC_USER"
id "$SVC_USER" >/dev/null 2>&1 || useradd -r -s /usr/sbin/nologin "$SVC_USER" 2>/dev/null || useradd -r -s /sbin/nologin "$SVC_USER"

echo "==> Veritabanı + rol: $DB_NAME / $DB_USER"
DB_PASS=$(python3 -c "import secrets;print(secrets.token_urlsafe(24))")
sudo -u postgres psql -tAc "SELECT 1 FROM pg_roles WHERE rolname='$DB_USER'" | grep -q 1 \
    && sudo -u postgres psql -qc "ALTER ROLE $DB_USER LOGIN PASSWORD '$DB_PASS'" \
    || sudo -u postgres psql -qc "CREATE ROLE $DB_USER LOGIN PASSWORD '$DB_PASS'"
sudo -u postgres psql -tAc "SELECT 1 FROM pg_database WHERE datname='$DB_NAME'" | grep -q 1 \
    || sudo -u postgres createdb -O "$DB_USER" "$DB_NAME"
# 127.0.0.1 üzerinden parola (md5) auth'u garanti et (bazı dağıtımlar ident/peer varsayar)
PGHBA=$(sudo -u postgres psql -tAc "SHOW hba_file" 2>/dev/null || true)
if [ -n "$PGHBA" ] && ! grep -qE "^host\s+$DB_NAME\s+$DB_USER\s+127.0.0.1/32\s+md5" "$PGHBA" 2>/dev/null; then
    echo "host    $DB_NAME    $DB_USER    127.0.0.1/32    md5" >> "$PGHBA"
    echo "host    $DB_NAME    $DB_USER    ::1/128         md5" >> "$PGHBA"
    systemctl reload postgresql || systemctl restart postgresql
fi

echo "==> Uygulama + venv: $APP_DIR"
mkdir -p "$APP_DIR"
cp -a "$SRC/Backend/." "$APP_DIR/"
[ -d "$SRC/keys" ] && cp -a "$SRC/keys" "$APP_DIR/keys"
[ -f "$SRC/VERSION" ] && cp -a "$SRC/VERSION" "$APP_DIR/VERSION"
rm -rf "$APP_DIR/__pycache__" "$APP_DIR/tests/__pycache__" 2>/dev/null || true
python3 -m venv "$APP_DIR/venv"
"$APP_DIR/venv/bin/pip" install -q --disable-pip-version-check -r "$APP_DIR/requirements.txt"

echo "==> Yapılandırma (.env)"
JWT=$(python3 -c "import secrets;print(secrets.token_hex(32))")
BYPASS=$(python3 -c "import secrets;print(secrets.token_hex(16))")
ADMIN_PASS=${ADMIN_PASS:-$(python3 -c "import secrets;print(secrets.token_urlsafe(12))")}
umask 077
cat > "$APP_DIR/.env" <<ENV
JWT_SECRET=$JWT
BYPASS_SECRET=$BYPASS
DB_HOST=127.0.0.1
DB_PORT=5432
DB_USER=$DB_USER
DB_PASS=$DB_PASS
DB_NAME=$DB_NAME
PANEL_ADMIN_USER=admin
PANEL_ADMIN_PASS=$ADMIN_PASS
CORS_ALLOWED_ORIGINS=
POPS_API_INTERNAL_URL=http://127.0.0.1:$PORT
ENV
chown -R "$SVC_USER:$SVC_USER" "$APP_DIR"

echo "==> Migration"
( cd "$APP_DIR" && sudo -u "$SVC_USER" env DB_HOST=127.0.0.1 DB_PORT=5432 DB_USER="$DB_USER" DB_PASS="$DB_PASS" DB_NAME="$DB_NAME" "$APP_DIR/venv/bin/python" migrate.py )

echo "==> systemd birimi: pops.service"
cat > /etc/systemd/system/pops.service <<UNIT
[Unit]
Description=POps Central Server (API & WebSocket)
After=network.target postgresql.service

[Service]
User=$SVC_USER
Group=$SVC_USER
WorkingDirectory=$APP_DIR
EnvironmentFile=$APP_DIR/.env
ExecStart=$APP_DIR/venv/bin/uvicorn server:app --host 127.0.0.1 --port $PORT
Restart=always
RestartSec=5

[Install]
WantedBy=multi-user.target
UNIT
systemctl daemon-reload
systemctl enable --now pops.service

echo "==> Sağlık kontrolü"
ok=0
for _ in $(seq 1 15); do
    if curl -sf "http://127.0.0.1:$PORT/api/health" >/dev/null 2>&1; then ok=1; break; fi
    sleep 1
done

echo
if [ "$ok" = 1 ]; then
    echo "===================== KURULUM TAMAM ====================="
    echo "  Backend:      http://127.0.0.1:$PORT (systemd: pops.service)"
    echo "  Panel admin:  admin / $ADMIN_PASS"
    echo "  App dizini:   $APP_DIR   (.env burada, 600)"
    echo
    echo "  SONRAKİ ADIM (web + TLS + panel — ortama özel, elle):"
    echo "   1. Bir web sunucusu (Apache/nginx) $PORT'u https'e proxyleyin;"
    echo "      /api ve /ws'i (WebSocket upgrade dahil) $PORT'a yönlendirin,"
    echo "      Dashboard/ klasörünü PHP ile sunun. Örnek yapılandırma:"
    echo "         $(dirname "$0")/nginx.example.conf"
    echo "   2. TLS (Let's Encrypt/certbot) ekleyin — ajan http'yi reddeder, wss şart."
else
    echo "!!! Backend sağlık kontrolü başarısız. Log:  journalctl -u pops -n 30 --no-pager"
    exit 1
fi
