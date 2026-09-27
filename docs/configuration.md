# Configuration

POps is configured in four places:

1. the **backend** environment (`.env`),
2. the **panel** (PHP) environment and `Dashboard/includes/config.php`,
3. the **agent** settings on each Windows PC (written by the MSI),
4. a few **runtime settings** stored in the database and changed from the panel.

No passwords, secrets or server addresses are compiled into the code; they all come from the places below.

## Backend environment

The backend reads its settings from environment variables. `Backend/pops/config.py` loads a `.env` file with
python-dotenv: the nearest `.env` found walking up from the backend package, so `<backend dir>/.env` on an
installed server (for example `/opt/pops/.env` after `install.sh`) or the project-root `.env` in a git checkout.
Variables already present in the process environment (for example systemd `Environment=`) **take precedence**
over the file. Keep `.env` readable only by the service user (`chmod 600`).

Ways to create it:

- `Installer/server/install.sh` writes a complete `.env` with generated secrets (see [`installation.md`](installation.md)).
- `python3 Backend/setup_env.py` builds `.env` from `.env.example`, fills empty `JWT_SECRET` / `BYPASS_SECRET`,
  and rotates the database and panel-admin passwords. Options: `--env PATH`, `--skip-install`, `--no-rotate-db`,
  `--no-rotate-admin`, `--yes`.
- Copy `.env.example` to `.env` and fill it in by hand.

### Variables

| Variable | Required | Default | Description |
| --- | --- | --- | --- |
| `JWT_SECRET` | yes | – | Key that signs panel JWTs (HS256). Generate with `python3 -c "import secrets; print(secrets.token_hex(32))"`. Changing it signs everyone out. |
| `JWT_EXPIRE_HOURS` | no | `12` | Lifetime of a panel token, in hours. |
| `DB_HOST` | no | `localhost` | PostgreSQL host. |
| `DB_PORT` | no | `5432` | PostgreSQL port. |
| `DB_USER` | yes | – | PostgreSQL role. |
| `DB_PASS` | yes | – | Its password. |
| `DB_NAME` | yes | – | Database name. |
| `PANEL_ADMIN_USER` | no | `admin` | Name of the first panel account. |
| `PANEL_ADMIN_PASS` | no | – | If no user with `PANEL_ADMIN_USER` exists at startup, the backend creates it as `superadmin` with this password. It is never written again once the account exists; change the password in the panel afterwards and you may delete the value. |
| `BYPASS_SECRET` | no | empty | Secret shared with the agents for offline quarantine bypass codes. Must equal the agents' `BypassSecret`. Empty: the panel does not issue bypass codes. The code changes daily with the server's local date, so keep the server and the PCs in the same time zone. |
| `CORS_ALLOWED_ORIGINS` | no | empty | Comma-separated list of extra origins allowed to call the API from a browser (for example `https://pops.example.com`). Leave empty when the panel and the API share one origin, which is the normal setup. |
| `WOL_BROADCAST_ADDR` | no | `255.255.255.255` | Where the server sends Wake-on-LAN packets. For a routed lab subnet use its broadcast address, e.g. `10.0.5.255`. |
| `WOL_PORT` | no | `9` | Wake-on-LAN UDP port. |
| `POPS_GITHUB_REPO` | no | `PashaCore/POps` | GitHub repository used for the release check, the server update check and **GitHub'dan indir ve doğrula**. |
| `POPS_SELFUPDATE_DIR` | no | `/var/lib/pops` | Spool directory for panel-triggered server self-update. Self-update counts as installed only if the backend user can write here. See [`self-update.md`](self-update.md). |
| `POPS_VERSION` | no | – | Overrides the version the server reports. Normally unset; the version is read from the `VERSION` file next to the backend (or one level up), then from `CHANGELOG.md`. |

The backend refuses to start (`RuntimeError: Ortam değişkeni tanımlı değil: …`) when `JWT_SECRET`, `DB_USER`,
`DB_PASS` or `DB_NAME` is missing.

`POPS_WOL_CONFIRM_PASSWORD` from older versions is no longer used and can be deleted.

### Listening address

Host and port are not environment variables; they are arguments of the service's `uvicorn` command. The unit
written by `install.sh` runs `uvicorn server:app --host 127.0.0.1 --port 8000` (`PORT` overrides the port at
install time). Keep the backend on `127.0.0.1` and let the web server be the only public entry point.

### Files and folders the backend uses

Relative to the backend directory:

| Path | Purpose |
| --- | --- |
| `storage/` | Files uploaded on the Deployment page, served at `/download/`. |
| `updates/` | The agent MSI being dispatched, served at `/updates/`. |
| `releases/<version>/` | Staged, signature-verified releases. |
| `keys/pops_release_ed25519.pub.pem` (or `../keys/…`) | Public key used to verify releases. Without it, release upload and download return `503`. |
| `VERSION` (or `../VERSION`) | Running version shown in the panel. |
| `migrations/` | Database migrations, applied at startup. |

### Installer options (`install.sh`)

`Installer/server/install.sh` accepts these environment variables:

| Variable | Default | Meaning |
| --- | --- | --- |
| `APP_DIR` | `/opt/pops` | Backend directory (code, `venv`, `.env`). |
| `SVC_USER` | `pops` | System user the service runs as. |
| `DB_NAME` / `DB_USER` | `pops` / `pops` | Database and role to create. |
| `PORT` | `8000` | Backend port on `127.0.0.1`. |
| `ADMIN_PASS` | random | Initial panel admin password. |

Example: `sudo APP_DIR=/srv/pops PORT=8080 DB_NAME=pops Installer/server/install.sh`.

## Panel (PHP) configuration

The panel needs `Dashboard/includes/config.php`. It is not tracked in git; create it from the template:

```bash
cp Dashboard/includes/config.example.php Dashboard/includes/config.php
```

`config.php` reads two values, each first from the web server's environment (`getenv`) and then from the
project-root `.env` (two levels above `Dashboard/includes/`):

| Variable | Default | Used for |
| --- | --- | --- |
| `POPS_API_INTERNAL_URL` | `http://localhost:8000` | The address PHP uses to reach the backend for the login request (server to server). Set it if the backend is not on `localhost:8000`. |
| `POPS_API_URL` | placeholder | Defines the `API_URL` constant. The current pages do not use it. |

`install.sh` writes `POPS_API_INTERNAL_URL` into the backend's `.env` (for example `/opt/pops/.env`), which the panel
does not read. If you installed with a `PORT` other than 8000, set the variable for PHP as well.

In the browser, the panel always calls the API and WebSockets on **its own origin** (`assets/pops_config.js` uses
`window.location`): `https://<panel host>/api/…` and `wss://<panel host>/ws/…`. The web server must therefore proxy
`/api/` and `/ws/` of the panel's host to the backend; see [`deployment.md`](deployment.md).

The session and JWT cookies are marked `Secure` automatically when the request is HTTPS (directly or through
`X-Forwarded-Proto: https`).

## Agent configuration

The Windows agent is configured by the MSI at install time. Full details are in
[`Installer/README.md`](../Installer/README.md) and [`Agent/README.md`](../Agent/README.md); this is a summary.

### MSI properties

```
msiexec /i POps-Agent-<version>-win-x64.msi /qn /l*v C:\POpsLogs\msi-install.log SERVER_URL=https://pops.example.com ENROLL_TOKEN=<token>
```

| Property | Written to | Notes |
| --- | --- | --- |
| `SERVER_URL` | `ServerUrl` in `appsettings.json` (install folder) | Required on a first install. Must be `https://`; `http://` only for `127.0.0.1` / `localhost`. |
| `ENROLL_TOKEN` | `C:\POpsData\secure\enroll.token` | Enrollment token from **Sistem & Sürüm**. Hidden from the MSI log. |
| `BYPASS_SECRET` | `C:\POpsData\secure\bypass.secret` | Must equal the server's `BYPASS_SECRET` for offline bypass codes. Hidden from the MSI log. |
| `PERSIST_DIR` | `PersistDir` in `appsettings.json` | Local NTFS folder that freeze software does not roll back; the device secret is mirrored there. |
| `TERMINAL_ENABLED` | `C:\POpsData\secure\capabilities.json` | `1` / `0`: allow or forbid remote commands (`execute`) on this PC. |
| `VISION_ENABLED` | same file | `1` / `0`: allow or forbid screen streaming, previews and remote input. |
| `INSTALLFOLDER` | – | Install folder, default `C:\Program Files\POps`. |

On an upgrade every property is optional: a property that is not given keeps the installed value. A first
install without `TERMINAL_ENABLED` / `VISION_ENABLED` enables both.

### `appsettings.json` keys and environment variables

The agent looks for each setting in a **system** environment variable first, then in `appsettings.json` in its
install folder, then in `C:\POps\appsettings.json`.

| Key | Environment variable | Default | Description |
| --- | --- | --- | --- |
| `ServerUrl` | `POPS_SERVER_URL` | `http://127.0.0.1:8000` (with a warning in the log) | Backend URL, e.g. `https://pops.example.com`. A non-loopback `http://` address is refused: the agent does not connect and logs why. |
| `PersistDir` | `POPS_PERSIST_DIR` | – | See `PERSIST_DIR`. Ignored unless it is a fully qualified local path. |
| `EnrollToken` | `POPS_ENROLL_TOKEN` | – | One-way input: on start the agent moves the value into `C:\POpsData\secure\enroll.token` and removes it from the file or variable. |
| `BypassSecret` | `POPS_BYPASS_SECRET` | – | Same, into `C:\POpsData\secure\bypass.secret`. |

Restart the `POpsAgent` service after changing them. `appsettings.json` and `C:\POpsData\secure` are readable
only by SYSTEM and Administrators.

## Runtime settings (database)

These live in the `global_settings` table ([`database.md`](database.md#settings)). Change them from the panel;
SQL is shown for recovery situations.

| Setting | Where in the panel | Values |
| --- | --- | --- |
| `concurrent_limit` | **Ayarlar** → "Orkestrasyon Performansı" (1–200) | How many devices run a queued task at the same time. Default `5`. The backend treats `0` as no limit (API or SQL only). |
| `enforce_agent_auth` | **Sistem & Sürüm** → "Kimlik zorlaması" | `1`: agents without a valid secret or enrollment token are rejected (WebSocket `4401`, HTTP `401`). Default off (accept-both). |
| `agent_policies` | **Politikalar** | JSON policy read by agents every 60 seconds (below). |
| `verified_release_version`, `verified_release_manifest` | **Sistem & Sürüm** → agent update | The staged, verified agent release. Set only by a successful upload or GitHub download. |

```sql
-- Emergency: turn agent-auth enforcement off (for example if a lab was enforced before it enrolled)
UPDATE global_settings SET value = '0' WHERE key = 'enforce_agent_auth';
```

### Agent policy object

`POST /api/agent_policies` (admin) stores, and `GET /api/agent_policies` returns:

| Field | Type | Meaning |
| --- | --- | --- |
| `fair_use_text` | string | Text the tray shows to the user in a notice that must be acknowledged. |
| `dns_categories` | list | Active DNS categories. The **Politikalar** page offers `pornografi`, `yasadisi_bahis`, `teror_siddet`, `zararli_yazilim`. |
| `auto_quarantine` | bool | Quarantine a device after `quarantine_threshold` DNS violations. |
| `quarantine_threshold` | int | Violation count for `auto_quarantine`. |
| `dns_domains` | object | `{"<category>": ["example.com", ...]}`. Only these domains and their subdomains are matched. Empty means no DNS detection. |

The **Politikalar** page does not show `dns_domains`, and saving the page sends the policy without it, which
stores an empty `dns_domains`. To use domain lists, send the complete object (including `dns_domains`) with the
API, and do not save the page afterwards. In the current agent build the DNS monitoring loop is not started, so
no DNS violations are reported yet; the fair-use text is shown. See [`agent.md`](agent.md#policies).
