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

Requirements: Linux with systemd, PostgreSQL, Python 3.9 or newer, PHP 8, and a web server that can proxy
WebSockets. `install.sh` is tested on AlmaLinux/RHEL/Rocky and Debian/Ubuntu.

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
  panel's JavaScript calls the API and WebSockets on the page's own origin, and the Deployment page builds the
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

`Dashboard/.htaccess` contains rewrite-proxy rules for `/api/` and `/ws/` only (they need `mod_rewrite`, the proxy
modules and `AllowOverride`); `/updates/` and `/download/` must still be proxied in the virtual host, as above.

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

1. refuses to run if `Backend/` has uncommitted or untracked files (only committed code is deployed),
2. exits if the live code already matches the checkout,
3. backs up the complete live code set to `<app>/.deploy-backups/code-<timestamp>-<pid>.tgz` (the last 10 are kept),
4. copies the tracked `Backend/*.py` files (including the `pops/` package, excluding tests), `migrations/`,
   `requirements.txt` (and runs `pip install` if it changed), `VERSION` and the release public key,
5. restarts the service and checks `/api/health` (200), `/api/agent_policies` (200) and `/api/devices` (401),
6. on failure restores the previous code set exactly (also removing files the failed deploy added) and restarts.

The paths at the top of the script (`REPO`, `APP=/opt/PashaCore_API`, `SVC=pashacore`, `OWNER=pashacore_admin`) are
those of the project's own server. For an `install.sh` layout set them to your checkout, `/opt/pops`, `pops` and
`pops`, and change the port in the health-check URLs if you did not use 8000. The script in the repository is a reference copy; install the one you run as root, for example to
`/usr/local/sbin/pops-deploy-backend`, so a deploy never overwrites the script while it runs.

The script deploys the **backend** only. The panel is served directly from the checkout's `Dashboard/` folder, so
updating the checkout (`git pull`) updates the panel.

### Self-update from the panel

With the systemd path unit installed, a superadmin can run the same deploy from **Sistem & Sürüm** without SSH.
It fetches `origin/main` (fast-forward only) and runs `/usr/local/sbin/pops-deploy-backend`. Setup and design:
[`self-update.md`](self-update.md). `pops-selfupdate` also contains the checkout path and the backend user; adjust
them the same way.

## Updating the agents

Agent updates are signed MSI packages distributed from **Sistem & Sürüm**: stage a release (download from GitHub
or upload it), then send it to all agents, one lab or selected PCs. Only online agents receive it. See
[`agent.md`](agent.md#updates).

## Releases

A `v*` tag runs `.github/workflows/release.yml`, which builds and attaches:

| File | Contents |
| --- | --- |
| `pops-server-<version>.tar.gz` | `Backend/`, `Dashboard/`, `docs/`, `keys/`, `VERSION`, `.env.example` and the top-level documents. |
| `POps-Agent-<version>-win-x64.msi` | Agent MSI (service, tray, watchdog, updater). |
| `POps-Agent-<version>-win-x64.zip` | The same files as a zip, for manual installs. |
| `manifest.json`, `manifest.json.sig` | SHA-256 of every file, version, tag and time, signed with ed25519. |
| `SHA256SUMS` | Plain checksums. |

The signing key exists only as a GitHub secret; the public key is `keys/pops_release_ed25519.pub.pem`
([`keys/README.md`](../keys/README.md)). The workflow refuses to publish unless the tag equals `v<VERSION>` and
`CHANGELOG.md` has a section for that version.

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
