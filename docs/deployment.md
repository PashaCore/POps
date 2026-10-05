# Deployment

This page describes a production POps server: what runs where, how to put the web server and TLS in front of
the backend, and how to update it. The first installation itself is covered in
[`installation.md`](installation.md). Running the server in containers instead is described in
[`docker.md`](docker.md); this page covers the native installation.

## Layout

```
Windows PCs (POpsAgent)              Browsers (admins)
        │  https / wss                     │  https / wss
        ▼                                  ▼
┌──────────────────── web server (nginx or Apache), TLS, port 443 ────────────────────┐
│  /            → Dashboard/ (PHP)                                                     │
│  /api/  /ws/  /updates/  /download/  → backend on 127.0.0.1:8000                     │
└──────────────────────────────────────────────────────────────────────────────────────┘
                                           │
                        backend: uvicorn server:app (systemd service)
                                           │
                                     PostgreSQL
```

| Part | Runs as | Notes |
| --- | --- | --- |
| PostgreSQL | `postgres` | One database for everything ([`database.md`](database.md)). |
| Backend | systemd service (`pops.service` after `install.sh`), unprivileged user | `uvicorn server:app --host 127.0.0.1 --port 8000`. Applies database migrations at startup. |
| Panel | the web server's PHP (PHP 8 with the `curl` extension) | Served straight from the checkout's `Dashboard/` folder. |
| Web server | nginx or Apache | Terminates TLS and proxies four paths to the backend. |

Requirements: Linux with systemd, PostgreSQL, Python 3.10 or newer (3.12 recommended), PHP 8, and a web server that
can proxy WebSockets. `install.sh` is tested on AlmaLinux/RHEL/Rocky and Debian/Ubuntu. AlmaLinux/RHEL 9's own
`python3` is 3.9; `install.sh` installs the `python3.12` package there. Ubuntu 22.04 (3.10), Debian 12 (3.11) and
Ubuntu 24.04 (3.12) need nothing extra. Why 3.9 was dropped: [`decisions.md`](decisions.md) D-20.

### One worker

Run the backend with a **single** uvicorn worker. The list of connected agents, the panel sockets and the
remote-control session grants are kept in the process's memory, so an agent and a panel connected to different
workers would not see each other. One worker is enough for a whole district: the [capacity report](kapasite/README.md) measured 5,000 agents back
11 s after a restart with no failed attempt and 40 % of one core in steady state, and gives hardware sizing by fleet
size (raw numbers in [`BENCHMARKS.md`](../BENCHMARKS.md)).

## Web server and TLS

The agent refuses a non-TLS server address (except loopback), so production must use `https://` and `wss://`.
The web server must:

- serve `Dashboard/` as the document root with PHP (`index.php` as the index),
- proxy `/api/`, `/ws/` (with WebSocket upgrade), `/updates/` and `/download/` to `http://127.0.0.1:8000`,
- serve all of this on **one host name** that both the administrators' browsers and the PCs can reach: the
  panel's JavaScript calls the API and WebSockets on the page's own origin, and the **Dağıtım** page builds the
  package download links from that origin too. Use the same address as the agents' `SERVER_URL`.

Agents need `/ws/agent/…`, `/ws/vision/…`, `/api/…` and `/updates/…` (signed MSI updates). Deployment packages
are downloaded from `/download/…`.

### nginx

[`Installer/server/nginx.example.conf`](../Installer/server/nginx.example.conf) is a complete example. Adjust
`server_name`, `root` (your checkout's `Dashboard/`), the certificate paths and the PHP-FPM socket, then add a
certificate, for example with `certbot --nginx -d pops.example.com`. The `/ws/` location sets the `Upgrade` /
`Connection` headers and a one-hour `proxy_read_timeout`.

### Apache

Enable `mod_proxy`, `mod_proxy_http` and `mod_proxy_wstunnel`, point `DocumentRoot` at `Dashboard/` (the repository
also has a `public` symlink to it), and add to the TLS virtual host:

```apache
ProxyPreserveHost On
ProxyRequests Off

ProxyPass        /api/      http://127.0.0.1:8000/api/
ProxyPassReverse /api/      http://127.0.0.1:8000/api/
ProxyPass        /ws/       ws://127.0.0.1:8000/ws/
ProxyPassReverse /ws/       ws://127.0.0.1:8000/ws/
ProxyPass        /download/ http://127.0.0.1:8000/download/
ProxyPassReverse /download/ http://127.0.0.1:8000/download/
ProxyPass        /updates/  http://127.0.0.1:8000/updates/
ProxyPassReverse /updates/  http://127.0.0.1:8000/updates/
```

Copy `Installer/server/apache-htaccess.example` to `Dashboard/.htaccess`. It proxies `/api/` and `/ws/` and maps the
panel's addresses without `.php` (`/devices` → `devices.php`, and redirects old `.php` addresses); it needs
`mod_rewrite`, the proxy modules and `AllowOverride FileInfo`. `/updates/` and `/download/` must still be proxied in
the virtual host, as above. `Dashboard/.htaccess` is not tracked in the repository: each server keeps its own copy and
updates never touch it, so after an update that changes the example, copy it again (the release notes say so).

### Panel configuration

Create `Dashboard/includes/config.php` from `config.example.php`. If the backend is not on
`http://localhost:8000` as seen from PHP, set `POPS_API_INTERNAL_URL` in the web server environment or in the
project-root `.env`. See [`configuration.md`](configuration.md#panel-php-configuration).

### Network

- Open only 443 (and 80 for the redirect) to clients. Keep port 8000 closed; `install.sh` binds the backend to
  `127.0.0.1`.
- Agents need outbound HTTPS to the server name.
- The server needs outbound access to GitHub for the version check and release download (optional; offline
  servers upload releases by hand), and to the SMTP server and webhook address if notifications are sent out.
- Wake-on-LAN packets are sent by the server to `WOL_BROADCAST_ADDR` (default `255.255.255.255`, which stays in
  the server's own subnet) and, for devices in a lab, also by an online agent in that lab. For a routed lab subnet
  set `WOL_BROADCAST_ADDR` to that subnet's broadcast address.
- Offline bypass codes are derived from the **server's local date**. Keep the server and the PCs in the same time
  zone.

## After the first start

1. Sign in with the admin password printed by `install.sh` (or the one `setup_env.py` printed), change it in
   **Ayarlar**, and turn on 2FA for admin accounts.
2. Create the other panel users with the least role they need ([`security.md`](security.md#roles)).
3. Enroll the PCs ([`quick-start.md`](quick-start.md)), then turn on agent-auth enforcement.

## Updating the server

Migrations run automatically when the backend starts. Take a database dump first when a release notes a data
migration.

### `install.sh` is for the first install only

Running it again rewrites `.env` with a new `JWT_SECRET` (signs everyone out), a new `BYPASS_SECRET` (bypass codes
stop matching the agents) and a new database password. Use one of the update paths below instead.

### `pops-deploy-backend`

`Installer/server/pops-deploy-backend` updates the backend from a git checkout, with a health check and automatic
rollback:

1. stops before changing anything if the checkout (`REPO`), the backend folder (`APP`), the service user or the
   systemd unit does not exist, or if `Backend/` has uncommitted or untracked files (only committed code is
   deployed),
2. exits if the live code already matches the checkout,
3. compares the venv's Python with the minimum in the new `requirements.txt` (the `# requires-python: >=3.10`
   line). When the venv is older, for example a server installed with Python 3.9, `pip` cannot install the new
   requirements into it, so the script builds a **new venv** with the first of `python3.12`, `python3.11`,
   `python3.10` and `python3` that is new enough, in `<app>/venv-py<version>-<timestamp>-<pid>`, and installs the
   requirements there while the service keeps running. If no interpreter is new enough, or the new venv cannot be
   built, it stops with nothing changed (exit code 3 for a missing interpreter, which the self-update status in
   **Sistem** turns into "install python3.12"),
4. backs up the complete live code set to `<app>/.deploy-backups/code-<timestamp>-<pid>.tgz`; when
   `requirements.txt` or `requirements.lock` changed or the venv is rebuilt, it also snapshots the whole venv to
   `venv-<timestamp>-<pid>.tgz` next to it before `pip` touches it (about 25 MB for the default requirements). If
   the snapshot fails, for example on a full disk, it stops without changing anything,
5. copies the tracked `Backend/*.py` files (including the `pops/` package, excluding tests), `migrations/`,
   `requirements.txt` and `requirements.lock` (and runs `pip install --require-hashes -r requirements.lock` into the
   live venv if either changed; a checkout without the lock installs `requirements.txt`), `VERSION` and the release
   public key. pip checks every downloaded file against the lock's SHA-256 hashes and stops before installing
   anything if one differs; the deploy then rolls back like any other failure. The new venv of a rebuild is installed
   the same way;
   after a rebuild it moves the old venv aside and makes `<app>/venv` a symbolic link to the new one, so the
   unit's `<app>/venv/bin/uvicorn` stays the same,
6. restarts the service and checks `/api/health` (200), `/api/agent_policies` (200) and `/api/devices` (401),
7. on **any** failure after the first change (`pip`, copying a file, the restart or the health check) restores the
   previous code set exactly (also removing files the failed deploy added) and, if it was snapshotted, the venv at
   the same absolute path, so the `#!` lines of its console scripts stay valid; after a rebuild it puts the old
   venv back in place and deletes the new one. Then it restarts the service. The restore itself is never cut short
   by an error or a signal. After a successful rebuild the old venv is deleted; its snapshot stays with the code
   backup.

The last `KEEP_BACKUPS` (10) rollback points are kept; a venv snapshot is deleted together with its code backup.
Database migrations are not rolled back (see [`decisions.md`](decisions.md)).

The script in the repository is a reference copy. Install the copy you run as root, so a deploy never overwrites
the script while it runs, and reinstall it when a release changes it (the CHANGELOG says so):

```bash
sudo install -m 755 Installer/server/pops-deploy-backend /usr/local/sbin/pops-deploy-backend
```

The script deploys the **backend** only. The panel is served directly from the checkout's `Dashboard/` folder, so
updating the checkout (`git pull`) updates the panel.

### Moving an existing server to Python 3.12

Servers installed before 0.1.22 on AlmaLinux/RHEL 9 run the backend in a Python 3.9 venv, and the backend now needs
3.10 or newer. Before the first update to 0.1.22 or later, as root:

```bash
dnf install -y python3.12
REPO=$(. /etc/pops/deploy.conf; echo "$REPO")      # the checkout; /etc/pops/deploy.conf names it
git -C "$REPO" fetch --tags origin
TAG=v0.1.22-alpha                                  # the release you update to
for s in pops-deploy-backend pops-selfupdate; do
    git -C "$REPO" show "$TAG:Installer/server/$s" > "/tmp/$s" && install -m 755 "/tmp/$s" "/usr/local/sbin/$s"
done
```

Then update from **Sistem → Sunucuyu güncelle** (or run `pops-deploy-backend` after checking out the tag). The deploy
builds the new venv with `python3.12`, swaps it in and keeps the health check and rollback. If git refuses the
checkout as "dubious ownership", add `-c safe.directory="$REPO"` after `git`.

Without these steps nothing breaks, but the server stays on the old release: the old `pops-deploy-backend` runs
`pip` in the 3.9 venv, `pip` finds no matching versions, and the deploy rolls back (status "deploy basarisiz"). The
new scripts without `python3.12` stop before changing anything and the status says to install it. Ubuntu 22.04,
Debian 12 and Ubuntu 24.04 servers already have a new enough `python3`; they only need the new scripts.

### `/etc/pops/deploy.conf`

`pops-deploy-backend` reads its paths from `/etc/pops/deploy.conf`, and `pops-selfupdate` reads the checkout path
(`REPO`) from the same file. `install.sh` writes it with the values it installed and leaves an existing file
alone. Without the file the `install.sh` defaults apply. Template:
[`Installer/server/deploy.conf.example`](../Installer/server/deploy.conf.example).

| Key | Default (no file) | Meaning |
| --- | --- | --- |
| `REPO` | the checkout the script runs from, if any | git checkout with `Backend/`; the panel is served from its `Dashboard/` |
| `APP` | `/opt/pops` | backend folder (`.env` and the venv `venv/` are here) |
| `SVC` | `pops` | systemd unit |
| `OWNER` | `pops` | service user; files are installed as this user and its primary group, and `pip` runs as this user |
| `HEALTH_BASE` | `http://127.0.0.1:8000` | backend address used by the health check |
| `HEALTH_WAIT` | `60` | seconds to wait for `/api/health` after the restart (pending migrations can slow the start) |
| `KEEP_BACKUPS` | `10` | rollback points kept (code backup plus its venv snapshot) |

Root sources the file as shell, so it must be owned by root, not writable by group or others and not a symbolic
link, and the same holds for `/etc/pops`. Otherwise both scripts refuse to read it and change nothing; they do not
fall back to the defaults. A server whose layout differs from `install.sh` (another folder, unit or user) must
have this file **before** the new scripts are installed, or the deploy stops at the first check.

### Self-update from the panel

With the systemd path unit installed, a superadmin can run the same deploy from **Sistem** without SSH.
`pops-selfupdate` fast-forwards the checkout to the newest release tag on `origin/main` (or to `origin/main` on the
`main` channel), checks the tag's SSH signature against `/etc/pops/allowed_signers` when that file exists, and runs
`/usr/local/sbin/pops-deploy-backend`. A tag whose signature cannot be verified is neither merged nor deployed.
Setup, signing and design: [`self-update.md`](self-update.md).

## Updating the agents

Agent updates are signed MSI packages distributed from **Sistem**: stage a release (download from GitHub
or upload it), then send it to all agents, one lab or selected PCs. Only online agents receive it. See
[`agent.md`](agent.md#updates).

## Releases

A `v*` tag runs `.github/workflows/release.yml`, which builds and attaches:

| File | Contents |
| --- | --- |
| `pops-server-<version>.tar.gz` | `Backend/`, `Dashboard/`, `docs/`, `keys/`, `Installer/server/` (install, deploy, self-update, backup and TLS scripts with their systemd units), the Docker files (`docker-compose.yml`, `docker/`, `.dockerignore`), `VERSION`, `.env.example` and the top-level documents. |
| `POps-Agent-<version>-win-x64.msi` | Agent MSI (service, tray, watchdog, updater). |
| `POps-Agent-<version>-win-x64.zip` | The same files as a zip, for manual installs. |
| `manifest.json`, `manifest.json.sig` | SHA-256 of every file, version, tag and time, signed with ed25519. |
| `SHA256SUMS` | Plain checksums. |

After the release is published, the same workflow pushes the Docker images
`ghcr.io/pashacore/pops-backend` and `ghcr.io/pashacore/pops-dashboard` with the tags `<version>` and `latest`
([`docker.md`](docker.md#images)).

The signing key exists only as a GitHub secret; the public key is `keys/pops_release_ed25519.pub.pem`
([`keys/README.md`](../keys/README.md)). The workflow refuses to publish unless the tag equals `v<VERSION>` and
`CHANGELOG.md` has a section for that version. Release tags can be signed with a separate SSH key (`git tag -s`), and
servers with `/etc/pops/allowed_signers` then self-update only to tags signed by a listed key
([`self-update.md`](self-update.md#sürüm-etiketlerinin-imzası)). The project signs its tags from **0.1.22-alpha** on;
the line to install is [`keys/allowed_signers`](../keys/allowed_signers).

## Logs

| What | Where |
| --- | --- |
| Backend | `journalctl -u pops` (the unit created by `install.sh`; use your unit name otherwise) |
| Deploy and self-update | output of `pops-deploy-backend`; for self-update `/var/lib/pops-state/deploy.log` and `/var/lib/pops-state/deploy-status.json` |
| Panel (PHP) | the web server's error log |
| Agents | see [`troubleshooting.md`](troubleshooting.md#where-the-logs-are) |

## TLS

`install.sh` puts nginx in front of the backend with TLS from the school-internal CA (`pops-tls`, default,
works offline), Let's Encrypt or your own certificate. The agent refuses `http://`; with the internal CA
agents are installed with `SERVER_CA_CERT` so they accept only that CA. See [`tls.md`](tls.md).

## Backups

`install.sh` enables a nightly, test-restored backup of the database, the backend `.env` and the uploaded and
staged files (`pops-backup.timer`). Keep a copy on another machine (`RSYNC_TARGET`). Setup and the recovery
runbook: [`backup.md`](backup.md).
