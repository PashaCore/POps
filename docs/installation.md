# Installing the POps Server

Native install, **no Docker required** — the server uses your distribution's Python (3.10 or newer) and
PostgreSQL. Tested on AlmaLinux/RHEL/Rocky (`dnf`) and Debian/Ubuntu (`apt`).
Docker is optional: to run the server with Docker Compose instead, see [`docker.md`](docker.md).

## One command

```bash
git clone https://github.com/PashaCore/POps.git
cd POps
sudo Installer/server/install.sh
```

That script:

1. installs PostgreSQL and Python if missing; the backend's venv uses the first of `python3.12`, `python3.11`,
   `python3.10` and `python3` that is 3.10 or newer. On AlmaLinux/RHEL 9 (`python3` is 3.9) it installs the
   `python3.12` package; on an apt system without one (Ubuntu 20.04) it stops and says what to install,
2. creates a service user, a database and a role (with a generated password),
3. creates a virtualenv and installs the backend dependencies,
4. writes `/opt/pops/.env` with freshly generated `JWT_SECRET` / `BYPASS_SECRET` and a random panel-admin password,
5. runs the database migrations,
6. installs and starts a `pops.service` systemd unit, and
7. health-checks the backend and prints the admin password.

Overridable with environment variables, e.g. `sudo APP_DIR=/srv/pops PORT=8080 DB_NAME=pops Installer/server/install.sh`.

## Web server and TLS (done by the installer)

The backend listens on `127.0.0.1:8000`; `install.sh` also installs nginx and PHP-FPM in front of it and
sets up TLS, because **the agent refuses a non-TLS server address**. Give it the name agents will use:

```bash
sudo POPS_DOMAIN=pops.okul.local Installer/server/install.sh
```

- Default (`TLS_MODE=internal`): a school-internal certificate authority is created on the server
  (`pops-tls`), works without internet. Install agents with `SERVER_CA_CERT=<pops-ca.pem>` and add the
  CA to browsers. Renewed automatically.
- Internet-facing server: `TLS_MODE=letsencrypt LE_EMAIL=you@example.com` (falls back to the internal
  CA when the challenge fails).
- Your own certificate: `TLS_MODE=existing TLS_CERT=/path/cert.pem TLS_KEY=/path/key.pem`.
- Your own web server: `TLS_MODE=none` installs the backend only; the site template is
  [`../Installer/server/nginx.pops.conf.in`](../Installer/server/nginx.pops.conf.in).

Details, renewal and how to distribute the CA: [`tls.md`](tls.md).

## After install

- Point agents at `https://<your-domain>` (installed via the agent MSI with `SERVER_URL=` and `ENROLL_TOKEN=`).
- Generate enrollment tokens and dispatch signed updates from the **Sistem** panel page.
- To update the server later from a git checkout, run `Installer/server/pops-deploy-backend` (health-checked, auto-rollback).
- Optional: enable **one-click server self-update from the panel** (no SSH) by installing the systemd path-unit — see [`self-update.md`](self-update.md). Until you do, the panel's self-update button shows "not installed" (safe default).
