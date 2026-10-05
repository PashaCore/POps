#!/usr/bin/env bash
# Install or update the POps public demo on this host. Run as root; every step is safe to repeat.
#   sudo bash setup-host.sh [git tag or branch]        (default: main)
# Optional environment: DEMO_DIR (/opt/pops-demo), DEMO_REPO (GitHub URL), DEMO_DOMAIN (demo.pashacore.com.tr),
# DEMO_UNIT_DIR (/etc/systemd/system)
# See README.md next to this file.
set -euo pipefail
# Everything runs inside main(): bash reads the whole function before step 1 replaces this file with another version
main() {
REF="${1:-main}"
DIR="${DEMO_DIR:-/opt/pops-demo}"
REPO="${DEMO_REPO:-https://github.com/PashaCore/POps.git}"
DOMAIN="${DEMO_DOMAIN:-demo.pashacore.com.tr}"
UNITS="${DEMO_UNIT_DIR:-/etc/systemd/system}"
[ "$(id -u)" = 0 ] || { echo "setup-host: run as root" >&2; exit 1; }

# 1. Code: clone once, then check out the requested tag or branch (the checkout is never edited by hand)
[ -d "$DIR/.git" ] || git clone --quiet "$REPO" "$DIR"
git -C "$DIR" fetch --quiet --tags --force origin
git -C "$DIR" checkout --quiet --force "$REF"
if git -C "$DIR" show-ref --verify --quiet "refs/remotes/origin/$REF"; then
    git -C "$DIR" reset --quiet --hard "origin/$REF"
fi
cd "$DIR/deploy/demo"

# 2. Secrets: .env.demo is created once with random values (mode 600) and kept on later runs; the image tag follows
#    the checked-out ref
created=0
if [ ! -f .env.demo ]; then
    (umask 077 && cp .env.demo.example .env.demo)
    for key in DB_PASS JWT_SECRET DEMO_ENROLL_TOKEN; do
        sed -i "s|^$key=.*|$key=$(openssl rand -hex 32)|" .env.demo
    done
    sed -i "s|^PANEL_ADMIN_PASS=.*|PANEL_ADMIN_PASS=$(openssl rand -base64 24 | tr -dc 'A-Za-z0-9' | head -c 24)|" .env.demo
    created=1
fi
sed -i "s|^POPS_IMAGE_TAG=.*|POPS_IMAGE_TAG=${REF#v}|" .env.demo
chown root:root .env.demo && chmod 600 .env.demo
dc() { docker compose -p pops-demo --env-file .env.demo -f docker-compose.demo.yml "$@"; }

# 3. Images from GHCR when that tag is published there, otherwise built from this checkout; then a fresh start
#    (down -v, up, migrate, seed, fleet), the same as the nightly reset
dc pull --quiet backend dashboard 2>/dev/null || dc build --pull backend dashboard
bash ./reset.sh

# 4. Nightly reset at 03:30 (local time)
cat > "$UNITS/pops-demo-reset.service" <<EOF
[Unit]
Description=POps demo reset (wipe and reseed)
Requires=docker.service
After=docker.service

[Service]
Type=oneshot
WorkingDirectory=$DIR/deploy/demo
ExecStart=/usr/bin/env bash $DIR/deploy/demo/reset.sh
TimeoutStartSec=20min
EOF
cat > "$UNITS/pops-demo-reset.timer" <<EOF
[Unit]
Description=POps demo reset every night at 03:30

[Timer]
OnCalendar=*-*-* 03:30:00
Persistent=true

[Install]
WantedBy=timers.target
EOF
systemctl daemon-reload
systemctl enable --now pops-demo-reset.timer

# 5. What is left for the owner: the Apache site (Virtualmin) in front of 127.0.0.1:8180
if [ "$created" = 1 ]; then
    echo
    echo "Superadmin (shown once; also in $DIR/deploy/demo/.env.demo):"
    echo "    user: $(sed -n 's/^PANEL_ADMIN_USER=//p' .env.demo)   password: $(sed -n 's/^PANEL_ADMIN_PASS=//p' .env.demo)"
fi
cat <<EOF

Demo is running on http://127.0.0.1:$(sed -n 's/^POPS_DEMO_PORT=//p' .env.demo)/ (demo / demo). Apache step, once:

  virtualmin create-domain --domain $DOMAIN --parent ${DOMAIN#*.} --dir --web --ssl --logrotate
  WEB=\$(virtualmin list-domains --domain $DOMAIN --home-only)/public_html
  OWNER=\$(virtualmin list-domains --domain $DOMAIN --user-only)
  rm -f "\$WEB/index.html"
  install -m 644 -o "\$OWNER" -g "\$OWNER" $DIR/deploy/demo/demo.htaccess "\$WEB/.htaccess"
  virtualmin generate-letsencrypt-cert --domain $DOMAIN --renew

Cloudflare: keep the record proxied and set SSL/TLS mode to "Full". Details: $DIR/deploy/demo/README.md
EOF
}
main "$@"
