# Installing the POps Server

Native install, **no Docker required** — the server uses your system's Python and
PostgreSQL. Tested on AlmaLinux/RHEL/Rocky (`dnf`) and Debian/Ubuntu (`apt`).

## One command

```bash
git clone https://github.com/PashaCore/POps.git
cd POps
sudo Installer/server/install.sh
```

That script:

1. installs PostgreSQL and Python if missing,
2. creates a service user, a database and a role (with a generated password),
3. creates a virtualenv and installs the backend dependencies,
4. writes `/opt/pops/.env` with freshly generated `JWT_SECRET` / `BYPASS_SECRET` and a random panel-admin password,
5. runs the database migrations,
6. installs and starts a `pops.service` systemd unit, and
7. health-checks the backend and prints the admin password.

Overridable with environment variables, e.g. `sudo APP_DIR=/srv/pops PORT=8080 DB_NAME=pops Installer/server/install.sh`.

## The one manual step: web server + TLS

The backend listens on `127.0.0.1:8000`. Put a web server in front of it to serve the
PHP dashboard and to terminate TLS — **the agent refuses a non-TLS server address**, so
production must be `https://` / `wss://`.

- An example config is at [`../Installer/server/nginx.example.conf`](../Installer/server/nginx.example.conf):
  it proxies `/api` and `/ws` (with WebSocket upgrade), `/updates` and `/download` to the
  backend, and serves `Dashboard/` as PHP.
- Add a certificate, e.g. `certbot --nginx -d pops.example.com`.

## After install

- Point agents at `https://<your-domain>` (installed via the agent MSI with `SERVER_URL=` and `ENROLL_TOKEN=`).
- Generate enrollment tokens and dispatch signed updates from the **Sistem & Sürüm** panel page.
- To update the server later from a git checkout, see `Installer/server/pops-deploy-backend`.
