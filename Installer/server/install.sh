#!/usr/bin/env bash
# POps sunucusunu TEK KOMUTLA kurar — native, Docker GEREKMEZ (her sunucuda Python/Postgres olur).
# AlmaLinux/RHEL/Rocky (dnf) ve Debian/Ubuntu (apt) destekler. root ile çalıştırın:
#     sudo Installer/server/install.sh
#
# Yaptıkları: PostgreSQL + Python kur (yoksa) -> servis kullanıcısı -> DB + rol (parola üret)
# -> venv + pip install -> .env (secret'lar üretilir) -> migrate -> systemd birimi -> sağlık kontrolü.
# -> nginx + php-fpm + TLS: varsayılan olarak KURUM İÇİ sertifika (pops-tls; internetsiz de çalışır), istenirse
#    Let's Encrypt (TLS_MODE=letsencrypt LE_EMAIL=...) ya da elde var olan sertifika (TLS_MODE=existing TLS_CERT= TLS_KEY=).
#    Panel ve ajanlar yalnızca https://POPS_DOMAIN üzerinden konuşur; ajan http'yi zaten reddeder.
#    Kendi web sunucunuzu kullanacaksanız TLS_MODE=none (yalnızca backend kurulur; bkz. nginx.pops.conf.in).
set -euo pipefail

APP_DIR=${APP_DIR:-/opt/pops}
SVC_USER=${SVC_USER:-pops}
DB_NAME=${DB_NAME:-pops}
DB_USER=${DB_USER:-pops}
PORT=${PORT:-8000}
POPS_DOMAIN=${POPS_DOMAIN:-}               # ajanların ve panelin kullanacağı ad (https://<POPS_DOMAIN>)
TLS_MODE=${TLS_MODE:-internal}             # internal | letsencrypt | existing | none
LE_EMAIL=${LE_EMAIL:-}                     # letsencrypt için (bitiş uyarıları)
TLS_CERT=${TLS_CERT:-}; TLS_KEY=${TLS_KEY:-}   # existing için
SRC=$(cd "$(dirname "$0")/../.." && pwd)   # repo kökü (Backend/ burada)

[ "$(id -u)" -eq 0 ] || { echo "root olarak çalıştırın (sudo)"; exit 1; }
[ -f "$SRC/Backend/server.py" ] || { echo "Backend/server.py bulunamadı ($SRC); repodan çalıştırın"; exit 1; }

case "$TLS_MODE" in internal|letsencrypt|existing|none) ;; *) echo "TLS_MODE geçersiz: $TLS_MODE"; exit 1;; esac
if [ "$TLS_MODE" != none ] && [ -z "$POPS_DOMAIN" ]; then
    if [ -t 0 ]; then
        read -r -p "Sunucunun alan adı (ajanlar ve panel bu adı kullanacak) [$(hostname -f)]: " POPS_DOMAIN
    fi
    POPS_DOMAIN=${POPS_DOMAIN:-$(hostname -f)}
fi
[ "$TLS_MODE" = existing ] && { [ -f "$TLS_CERT" ] && [ -f "$TLS_KEY" ] || { echo "TLS_MODE=existing için TLS_CERT ve TLS_KEY dosyaları gerekli"; exit 1; }; }

WEB_PKGS=""; NGINX_USER=nginx; PHP_USER=apache; PHP_SOCK=/run/php-fpm/www.sock; NGINX_SITE=/etc/nginx/conf.d/pops.conf
if command -v apt-get >/dev/null 2>&1; then NGINX_USER=www-data; PHP_USER=www-data; NGINX_SITE=/etc/nginx/sites-available/pops; fi
[ "$TLS_MODE" != none ] && WEB_PKGS="nginx php-fpm"

echo "==> Paketler kuruluyor..."
if command -v dnf >/dev/null 2>&1; then
    # shellcheck disable=SC2086
    dnf install -y python3 python3-pip postgresql-server postgresql openssl $WEB_PKGS >/dev/null
    [ -f /var/lib/pgsql/data/PG_VERSION ] || postgresql-setup --initdb >/dev/null 2>&1 || /usr/bin/postgresql-setup initdb >/dev/null 2>&1 || true
    systemctl enable --now postgresql
elif command -v apt-get >/dev/null 2>&1; then
    export DEBIAN_FRONTEND=noninteractive
    apt-get update -qq
    # shellcheck disable=SC2086
    apt-get install -y -qq python3 python3-venv python3-pip postgresql openssl $WEB_PKGS >/dev/null
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
    # pops-backup her yedeği bu geçici veritabanına açıp sınar
    echo "host    ${DB_NAME}_restorecheck    $DB_USER    127.0.0.1/32    md5" >> "$PGHBA"
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
# Panel (PHP) repo kökündeki .env'i okur: tarayıcının kullandığı API adresi + iç adres
if [ "$TLS_MODE" != none ]; then
    touch "$SRC/.env"
    grep -q '^POPS_API_URL=' "$SRC/.env" || echo "POPS_API_URL=https://$POPS_DOMAIN/api" >> "$SRC/.env"
    grep -q '^POPS_API_INTERNAL_URL=' "$SRC/.env" || echo "POPS_API_INTERNAL_URL=http://127.0.0.1:$PORT" >> "$SRC/.env"
    chown "root:$PHP_USER" "$SRC/.env" 2>/dev/null || true; chmod 640 "$SRC/.env"
    [ -f "$SRC/Dashboard/includes/config.php" ] || cp "$SRC/Dashboard/includes/config.example.php" "$SRC/Dashboard/includes/config.php"
fi
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

echo "==> Gece yedeği: pops-backup.timer (/var/backups/pops, 14 gün)"
install -m 755 "$SRC/Installer/server/pops-backup" "$SRC/Installer/server/pops-restore" /usr/local/sbin/
install -m 644 "$SRC/Installer/server/pops-backup.service" "$SRC/Installer/server/pops-backup.timer" /etc/systemd/system/
install -d -m 755 /etc/pops
if [ ! -f /etc/pops/backup.conf ]; then
    sed -e "s#^POPS_APP=.*#POPS_APP=$APP_DIR#" -e "s#^POPS_SERVICE_USER=.*#POPS_SERVICE_USER=$SVC_USER#" \
        -e "s#^POPS_HEALTH_URL=.*#POPS_HEALTH_URL=http://127.0.0.1:$PORT/api/health#" \
        "$SRC/Installer/server/backup.conf.example" > /etc/pops/backup.conf
    chmod 600 /etc/pops/backup.conf
fi
systemctl daemon-reload
systemctl enable --now pops-backup.timer

echo "==> Sağlık kontrolü"
ok=0
for _ in $(seq 1 15); do
    if curl -sf "http://127.0.0.1:$PORT/api/health" >/dev/null 2>&1; then ok=1; break; fi
    sleep 1
done

[ "$ok" = 1 ] || { echo "!!! Backend sağlık kontrolü başarısız. Log:  journalctl -u pops -n 30 --no-pager"; exit 1; }

# ─── Web + TLS ─────────────────────────────────────────────────────────────────
WEB_OK=skip; CA_NOTE=""
if [ "$TLS_MODE" != none ]; then
    echo "==> TLS ($TLS_MODE) + nginx: https://$POPS_DOMAIN"
    install -m 755 "$SRC/Installer/server/pops-tls" /usr/local/sbin/
    CERT=/etc/pops/tls/server.crt; KEY=/etc/pops/tls/server.key
    case "$TLS_MODE" in
        existing) /usr/local/sbin/pops-tls import "$TLS_CERT" "$TLS_KEY" ;;
        *)  # internal ve letsencrypt: önce kurum sertifikası (LE başarısız olursa da https çalışsın)
            # shellcheck disable=SC2046
            /usr/local/sbin/pops-tls init "$POPS_DOMAIN" $(hostname -I 2>/dev/null | tr ' ' '\n' | grep -E '^[0-9.]+$' | head -3) ;;
    esac
    [ -S "$PHP_SOCK" ] || PHP_SOCK=$(ls /run/php/php*-fpm.sock /run/php-fpm/*.sock 2>/dev/null | head -1 || true)
    systemctl enable --now php-fpm >/dev/null 2>&1 || systemctl enable --now "$(systemctl list-unit-files 'php*-fpm.service' --no-legend 2>/dev/null | awk 'NR==1{print $1}')" >/dev/null 2>&1 || true
    [ -S "$PHP_SOCK" ] || PHP_SOCK=$(ls /run/php/php*-fpm.sock /run/php-fpm/*.sock 2>/dev/null | head -1 || true)
    [ -n "$PHP_SOCK" ] || { echo "!!! php-fpm soketi bulunamadı; nginx yapılandırmasında fastcgi_pass'i elle düzeltin"; PHP_SOCK=/run/php-fpm/www.sock; }
    mkdir -p "$(dirname "$NGINX_SITE")" /var/www/html
    sed -e "s#@DOMAIN@#$POPS_DOMAIN#g" -e "s#@DASHBOARD@#$SRC/Dashboard#g" -e "s#@PHP_SOCK@#$PHP_SOCK#g" \
        -e "s#@CERT@#$CERT#g" -e "s#@KEY@#$KEY#g" -e "s#@PORT@#$PORT#g" \
        "$SRC/Installer/server/nginx.pops.conf.in" > "$NGINX_SITE"
    [ -d /etc/nginx/sites-enabled ] && ln -sf "$NGINX_SITE" /etc/nginx/sites-enabled/pops && rm -f /etc/nginx/sites-enabled/default
    # SELinux: nginx'in backend'e bağlanması ve repo klonundaki paneli okuması
    if command -v getenforce >/dev/null 2>&1 && [ "$(getenforce 2>/dev/null)" = Enforcing ]; then
        setsebool -P httpd_can_network_connect 1 2>/dev/null || true
        chcon -R -t httpd_sys_content_t "$SRC/Dashboard" 2>/dev/null || true
    fi
    if command -v firewall-cmd >/dev/null 2>&1 && systemctl is-active -q firewalld; then
        firewall-cmd -q --permanent --add-service=http --add-service=https && firewall-cmd -q --reload || true
    fi
    if sudo -u "$NGINX_USER" test -r "$SRC/Dashboard/index.php" 2>/dev/null; then :; else
        echo "!!! $NGINX_USER kullanıcısı $SRC/Dashboard dosyalarını okuyamıyor (repo /root altında mı?). Repoyu /opt gibi bir yere klonlayıp yeniden çalıştırın."
    fi
    nginx -t >/dev/null 2>&1 && systemctl enable --now nginx >/dev/null && systemctl reload nginx || { echo "!!! nginx yapılandırması hatalı:"; nginx -t; }
    if [ "$TLS_MODE" = letsencrypt ]; then
        if command -v dnf >/dev/null 2>&1; then dnf install -y epel-release >/dev/null 2>&1 || true; dnf install -y certbot python3-certbot-nginx >/dev/null 2>&1 || true
        else apt-get install -y -qq certbot python3-certbot-nginx >/dev/null 2>&1 || true; fi
        if certbot --nginx -d "$POPS_DOMAIN" --non-interactive --agree-tos ${LE_EMAIL:+-m "$LE_EMAIL"} ${LE_EMAIL:---register-unsafely-without-email} --redirect >/dev/null 2>&1; then
            echo "    Let's Encrypt sertifikası alındı (certbot kendi zamanlayıcısıyla yeniler)."
        else
            TLS_MODE=internal
            echo "!!! Let's Encrypt alınamadı (alan adı bu sunucuya işaret etmiyor ya da 80/443 dışarıdan kapalı); kurum sertifikasıyla devam."
        fi
    fi
    if [ "$TLS_MODE" = internal ]; then
        install -m 644 "$SRC/Installer/server/pops-tls-renew.service" "$SRC/Installer/server/pops-tls-renew.timer" /etc/systemd/system/
        systemctl daemon-reload && systemctl enable --now pops-tls-renew.timer >/dev/null
        CA_NOTE="kurum CA'sı: /etc/pops/ca/pops-ca.pem (https://$POPS_DOMAIN/pops-ca.pem)"
    fi
    curl_ca=""; [ "$TLS_MODE" = internal ] && curl_ca="--cacert /etc/pops/ca/pops-ca.pem"
    # shellcheck disable=SC2086
    if curl -sf $curl_ca --resolve "$POPS_DOMAIN:443:127.0.0.1" "https://$POPS_DOMAIN/api/health" >/dev/null 2>&1; then WEB_OK=1; else WEB_OK=0; fi
fi

echo
echo "===================== KURULUM TAMAM ====================="
echo "  Backend:      http://127.0.0.1:$PORT (systemd: pops.service)"
echo "  Panel admin:  admin / $ADMIN_PASS"
echo "  App dizini:   $APP_DIR   (.env burada, 600)"
echo "  Yedek:        her gece /var/backups/pops (ayar: /etc/pops/backup.conf; başka makineye"
echo "                kopya için RSYNC_TARGET'ı doldurun). Geri dönüş: pops-restore, bkz. docs/backup.md"
case "$WEB_OK" in
    1) echo "  Panel:        https://$POPS_DOMAIN/   (nginx + TLS: $TLS_MODE)" ;;
    0) echo "!!! Panel:     https://$POPS_DOMAIN/ sağlık kontrolünden GEÇMEDİ: nginx -t; journalctl -u nginx -n 30" ;;
    *) echo "  Web:          kurulmadı (TLS_MODE=none). Örnek: $SRC/Installer/server/nginx.pops.conf.in" ;;
esac
if [ -n "$CA_NOTE" ]; then
    echo
    echo "  KURUM SERTİFİKASI — $CA_NOTE"
    echo "   • Parmak izi (kontrol için):  pops-tls show"
    echo "   • Ajan kurulumu: MSI'a SERVER_CA_CERT=<pops-ca.pem yolu> verin; ajan yalnızca bu CA'ya zincirlenen sunucuyu kabul eder."
    echo "   • Tarayıcılar (panel): pops-ca.pem'i 'Güvenilen Kök Sertifika Yetkilileri'ne ekleyin (GPO ile tüm okula dağıtılabilir)."
    echo "   • Sertifika 825 günlük; pops-tls-renew.timer 30 gün kala yeniler. Ayrıntı: docs/tls.md"
fi
echo "  Ajan kurulumu: msiexec /i POps-Agent-<sürüm>-win-x64.msi /qn SERVER_URL=https://${POPS_DOMAIN:-<alan-adı>} ENROLL_TOKEN=<panelden>"
