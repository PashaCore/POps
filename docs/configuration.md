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
| `DB_POOL_MIN` | no | `2` | Connections the backend keeps open to PostgreSQL. |
| `DB_ACQUIRE_TIMEOUT` | no | `10` | Seconds a request waits for a free pool connection before it fails (it used to wait forever). |
| `DB_CONNECT_TIMEOUT` | no | `10` | Seconds to open a database connection. |
| `DB_COMMAND_TIMEOUT` | no | `30` | Longest single query, in seconds. Migrations run on a separate connection without this limit. |
| `DB_IDLE_IN_TRANSACTION_MS` | no | `60000` | PostgreSQL closes a session that sits idle inside a transaction this long, so it cannot hold locks. |
| `POPS_TZ` | no | the process time zone | Server time zone as an IANA name, for example `Europe/Istanbul`. Used for day boundaries (log and report filters, "today"), the readable times in CSV exports and the offset of the times the API returns. Unset: the backend process's own time zone (`TZ`, `/etc/localtime`), then the database's `TimeZone` setting. Migration `0031` read the old text timestamps in this zone; set it before that update only if the backend ran in a different zone than the one detected now. |
| `HEARTBEAT_FLUSH_SECONDS` | no | `2` | Agent heartbeats are collected and written in one statement this often. |
| `PEER_CACHE_SEED_TIMEOUT_SECONDS` | no | `600` | Lab-local peer cache: how long a lab's seed PC has to report `verified`, and then a successful result, before the next PC becomes the seed (see [`agent.md`](agent.md#peer-cache-contract)). Tests use `4`. |
| `SCHEDULE_VALID_MINUTES` | no | `60` | A scheduled run that could not be sent within this many minutes of its time becomes `Expired`. |
| `SCHEDULE_MISFIRE_MINUTES` | no | `60` | If the server was down longer than this past a run time, that run is skipped and reported as missed. |
| `TOTP_ENCRYPTION_KEY` | no | derived from `JWT_SECRET` | Fernet key that encrypts the 2FA secrets and the per-device bypass keys in the database. Set it before ever changing `JWT_SECRET`; see [`security.md`](security.md#two-factor-authentication). |
| `DISK_CHECK_PATHS` | no | – | Extra comma-separated paths for the hourly free-space check (the backend folder and `storage/` are always checked). |
| `TLS_CERT_FILES` | no | `/etc/pops/tls/server.crt,/etc/pops/ca/pops-ca.pem` | Certificate files whose expiry is checked daily (missing files are skipped). |
| `TLS_CHECK_URL` | no | the `https` origins in `CORS_ALLOWED_ORIGINS` | Panel address(es) whose live certificate expiry is checked daily, for example `https://pops.okul.k12.tr`. |
| `DB_POOL_MAX` | no | `20` | Largest number of database connections. When the pool is full, requests wait for a free connection. Keep it below the PostgreSQL server's `max_connections` (100 by default), especially when other applications share that server, and raise both together if needed. |
| `PANEL_ADMIN_USER` | no | `admin` | Name of the first panel account. |
| `PANEL_ADMIN_PASS` | no | – | If no user with `PANEL_ADMIN_USER` exists at startup, the backend creates it as `superadmin` with this password. It is never written again once the account exists; change the password in the panel afterwards and you may delete the value. |
| `BYPASS_SECRET` | no | empty | **Legacy.** Fleet-wide secret for offline quarantine bypass codes, needed only for agents older than 0.1.12 and for devices that have not yet confirmed their per-device key (0.1.12+ agents receive one from the server on their first authenticated connection; see `Backend/pops/bypass.py`). Must equal those agents' `BypassSecret`. Empty: such devices get no code. The code changes daily with the server's local date, so keep the server and the PCs in the same time zone. |
| `CORS_ALLOWED_ORIGINS` | no | empty | Comma-separated list of extra origins allowed to call the API from a browser (for example `https://pops.example.com`). Leave empty when the panel and the API share one origin, which is the normal setup. |
| `WOL_BROADCAST_ADDR` | no | `255.255.255.255` | Where the server sends Wake-on-LAN packets. For a routed lab subnet use its broadcast address, e.g. `10.0.5.255`. |
| `WOL_PORT` | no | `9` | Wake-on-LAN UDP port. |
| `POPS_GITHUB_REPO` | no | `PashaCore/POps` | GitHub repository used for the release check, the server update check and the agent package download on **Sistem** → **Ajanlar**. |
| `POPS_FILES_DIR` | no | `Backend/transfers` | Where file transfers are kept on the server (files sent to PCs until they are downloaded, files fetched from PCs for 7 days). Not served over HTTP and not part of `pops-backup`. In Docker it is the `transfers` volume. |
| `POPS_SELFUPDATE_DIR` | no | `/var/lib/pops` | Spool directory for panel-triggered server self-update. Self-update counts as installed only if the backend user can write here. See [`self-update.md`](self-update.md). |
| `POPS_VERSION` | no | – | Overrides the version the server reports. Normally unset; the version is read from the `VERSION` file next to the backend (or one level up), then from `CHANGELOG.md`. |
| `SMTP_HOST` | no | empty | Mail server for e-mail notifications. E-mail is sent only when `SMTP_HOST` and a sender (`SMTP_FROM` or `SMTP_USER`) are set. |
| `SMTP_PORT` | no | `587` | Mail server port. |
| `SMTP_SECURITY` | no | `starttls` | `starttls` (typically port 587), `ssl` (implicit TLS, typically 465) or `none` (only for a local or trusted relay). |
| `SMTP_USER` | no | empty | Login name; when empty the server sends without logging in. |
| `SMTP_PASS` | no | empty | Password for `SMTP_USER`. |
| `SMTP_FROM` | no | `SMTP_USER` | Sender address. |
| `NOTIFY_WEBHOOK_ALLOW_PRIVATE` | no | off | By default the notification webhook may only point at public internet addresses: its host is resolved and loopback, private ranges, link-local (including `169.254.169.254`), CGNAT and reserved addresses are refused. `1` (or `true` / `yes`) also allows those, for a webhook receiver inside the school network. Multicast and unspecified addresses stay refused. |
| `GLPI_ALLOW_PRIVATE` | no | off | Like `NOTIFY_WEBHOOK_ALLOW_PRIVATE`, for the GLPI export ([`integrations/glpi.md`](integrations/glpi.md)): `1` allows a GLPI on a private, loopback or link-local address, the usual case for a GLPI on the school network. With it, plain `http://` is accepted for a host whose addresses are all private; a GLPI on the internet always needs `https://`. |
| `GLPI_CA_FILE` | no | empty | Path to a PEM file with the certificate authority that signed the GLPI server's certificate, when it is not one the system trusts (a school's own CA). |
| `LOG_LEVEL` | no | `INFO` | Backend log level (`DEBUG`, `INFO`, `WARNING`, `ERROR`). |
| `LOG_FORMAT` | no | `json` | `json`: one JSON object per line (for journald and log collectors). `text`: readable lines for development. |
| `METRICS_TOKEN` | no | unset | Turns on the Prometheus `/metrics` endpoint; at least 16 characters, sent as `Authorization: Bearer <token>`. Unset: the endpoint returns 404. See [`backend.md`](backend.md#logs-metrics-and-diagnostics). |
| `POPS_DEMO_USERS` | no | empty | Comma-separated panel user names that are **read-only demo accounts** (for a public demo). They can sign in and read, but every other request (anything but `GET`, `HEAD`, `OPTIONS`) returns `403` with `Demo hesabında değiştirilemez`, including their own password and 2FA. See [Public demo](#public-demo-read-only-accounts). |

The backend refuses to start (`RuntimeError: Ortam değişkeni tanımlı değil: …`) when `JWT_SECRET`, `DB_USER`,
`DB_PASS` or `DB_NAME` is missing.

`POPS_WOL_CONFIRM_PASSWORD` from older versions is no longer used and can be deleted.

The SMTP settings are kept only in `.env`; the panel shows whether SMTP is configured but never the values.
The recipients, the webhook address and the minimum severity are set on the panel (see
[Notification settings](#notification-settings)).

### Listening address

Host and port are not environment variables; they are arguments of the service's `uvicorn` command. The unit
written by `install.sh` runs `uvicorn server:app --host 127.0.0.1 --port 8000` (`PORT` overrides the port at
install time). Keep the backend on `127.0.0.1` and let the web server be the only public entry point.

### Files and folders the backend uses

Relative to the backend directory:

| Path | Purpose |
| --- | --- |
| `storage/` | Files uploaded on the **Dağıtım** page, served at `/download/`. |
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
| `POPS_API_URL` | placeholder | Defines the `API_URL` constant; the sign-in page uses it for the organisation logo. |
| `POPS_DEMO_LOGIN` | empty | `user:password` of a public demo account. The sign-in page shows "Demo: kullanıcı …, şifre … (salt okunur)" and pre-fills the user name. Empty: nothing is shown. |

`install.sh` writes `POPS_API_INTERNAL_URL` into the backend's `.env` (for example `/opt/pops/.env`), which the panel
does not read. If you installed with a `PORT` other than 8000, set the variable for PHP as well.

In the browser, the panel always calls the API and WebSockets on **its own origin** (`assets/pops_config.js` sets
`window.POPS_API` from `window.location`): `https://<panel host>/api/…` and `wss://<panel host>/ws/…`. The web server must therefore proxy
`/api/` and `/ws/` of the panel's host to the backend; see [`deployment.md`](deployment.md).

The session and JWT cookies are marked `Secure` automatically when the request is HTTPS (directly or through
`X-Forwarded-Proto: https`).

### Public demo (read-only accounts)

A public demo panel needs an account that anyone may use. Two settings make one:

1. **Backend** `POPS_DEMO_USERS=demo` (comma list). The check is in `require_auth`
   (`Backend/pops/security.py`), which every panel endpoint goes through, so it covers endpoints added later too.
   Only `GET`, `HEAD` and `OPTIONS` pass, plus `POST /api/tasks/status`, which reads task states. Everything else,
   the account's own password and 2FA included, returns `403` with `Demo hesabında değiştirilemez`. Give the
   account the `viewer` role as well: admin-only pages and endpoints then stay closed for reading too.
2. **Panel** `POPS_DEMO_LOGIN=demo:demo` shows the credentials on the sign-in page (escaped) and pre-fills the
   user name. Only `config.php` files made from the current template read it (`define('POPS_DEMO_LOGIN', …)`);
   an older `config.php` simply shows nothing.

The user itself is created like any other viewer. `deploy/demo/` contains a complete demo (fake fleet, seed data,
nightly reset) built on these two settings.

## Agent configuration

The Windows agent is configured by the MSI at install time. Full details are in
[`Installer/README.md`](../Installer/README.md) and [`Agent/README.md`](../Agent/README.md); this is a summary.

### MSI properties

```
msiexec /i POps-Agent-<version>-win-x64.msi /qn /l*v C:\Windows\Temp\pops-msi-install.log SERVER_URL=https://pops.example.com ENROLL_TOKEN=<token>
```

| Property | Written to | Notes |
| --- | --- | --- |
| `SERVER_URL` | `ServerUrl` in `appsettings.json` (install folder) | Required on a first install. Must be `https://`; `http://` only for `127.0.0.1` / `localhost`. |
| `ENROLL_TOKEN` | `C:\POpsData\secure\enroll.token` | Enrollment token from **Sistem**. Hidden from the MSI log. |
| `BYPASS_SECRET` | `C:\POpsData\secure\bypass.secret` | Must equal the server's `BYPASS_SECRET` for offline bypass codes. Hidden from the MSI log. |
| `PERSIST_DIR` | `PersistDir` in `appsettings.json` | Local NTFS folder that freeze software does not roll back; the device secret is mirrored there. |
| `TERMINAL_ENABLED` | `C:\POpsData\secure\capabilities.json` | `1` / `0`: allow or forbid remote commands (`execute`) on this PC. |
| `VISION_ENABLED` | same file | `1` / `0`: allow or forbid screen streaming, previews and remote input. |
| `EXAM_ENABLED` | same file | `1` / `0`: allow or forbid exam mode (network limited to an allow list, apps closed). Default `1`. |
| `FILES_ENABLED` | same file | `1` / `0`: allow or forbid file push and pull. Default `1`. |
| `INSTALLFOLDER` | – | Install folder, default `C:\Program Files\POps`. |

On an upgrade every property is optional: a property that is not given keeps the installed value. A first
install without `TERMINAL_ENABLED` / `VISION_ENABLED` / `EXAM_ENABLED` / `FILES_ENABLED` enables all four; a capability missing from
an older `capabilities.json` counts as enabled.

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
| `concurrent_limit` | **Ayarlar** → "Görev kuyruğu" → "Eşzamanlı görev sınırı" (1–200), **Dağıtım** → "Eşzamanlı kurulum sınırı" (1–100) or the limit button on **İşlemler** (1–500) | How many devices run a queued task at the same time. Default `5`. The backend treats `0` as no limit (API or SQL only). |
| `enforce_agent_auth` | **Sistem** → "Kimlik zorlaması" | `1`: agents without a valid secret or enrollment token are rejected (WebSocket `4401`, HTTP `401`). Default off (accept-both). |
| `update_peer_cache` | **Sistem** → **Ajanlar** → "Sınıf içinde eşten dağıt" | Default off (`1` turns it on): while on, a PC that holds a package opens a port to its local subnet. Agents that support the lab-local peer cache are updated one PC per lab first, the rest of the lab fetch the package from it ([`agent.md`](agent.md#peer-cache-contract)). Off: every PC downloads from the server at once, as before. |
| `agent_policies` | **Politikalar** | JSON policy read by agents every 60 seconds (below). |
| `verified_release_version`, `verified_release_manifest` | **Sistem** → **Ajanlar** | The staged, verified agent release. Set only by a successful upload or GitHub download. |
| `auto_enroll_lab` | **Sınıflar** → **Sınıf işlemleri** → **Otomatik kayıt…** | JSON `{"lab": "<lab>", "until": "YYYY-MM-DD"}`. Devices that connect for the **first time** on or before `until` (server date) are put into that lab. A lab from the enrollment token takes precedence, devices that are already known keep their lab, and a value without `until` (from older versions) is ignored. |
| `notify_enabled`, `notify_min_severity`, `notify_email_to`, `notify_webhook_url` | **Sistem** → **Bildirimler** | Notification delivery, see below. |

```sql
-- Emergency: turn agent-auth enforcement off (for example if a lab was enforced before it enrolled)
UPDATE global_settings SET value = '0' WHERE key = 'enforce_agent_auth';
```

### Identity providers

Directory (LDAP / Active Directory) and OpenID Connect sign-in are set by a superadmin on **Ayarlar** → **Güvenlik**
→ **Kimlik sağlayıcıları** and stored in the `sso_providers` table, one row per kind (`ldap`, `oidc`), not in
`global_settings`. API: `GET /api/sso/settings`, `PUT /api/sso/settings/ldap`, `PUT /api/sso/settings/oidc`,
`POST /api/sso/test/ldap`, `POST /api/sso/test/oidc` (superadmin, also under `/api/v1`). The secrets (service-account
password, client secret) are write-only: leave the field out (or `null`) to keep the stored one, send `""` to delete
it. When the host, port, connection type, service account or CA certificate (LDAP), or the issuer, client ID or CA
certificate (OIDC) changes, the secret must be sent again. A provider can be saved switched off with incomplete
fields; switching it on needs the required ones. Switching a provider off ends the open sessions of its accounts. How the
sign-in works and what is checked: [`security.md`](security.md#directory-and-single-sign-on).

**LDAP / Active Directory** (users sign in with the normal **Kullanıcı adı** / **Şifre** form):

| Field | Default | Meaning |
| --- | --- | --- |
| `enabled` | `false` | Directory accounts may sign in. |
| `host`, `port` | `""`, `636` | Directory server (a name or IP address, no `ldap://`). Use the name in the server's certificate. |
| `security` | `ldaps` | `ldaps` (TLS from the start, usually port 636) or `starttls` (port 389, upgraded before anything is sent). Plain LDAP is refused. |
| `ca_pem` | `""` | CA certificate(s) in PEM that signed the server's certificate. Empty: the server's system CA store. When set, only this CA is trusted. |
| `bind_dn`, `bind_password` | | Service account used to find users; read-only rights are enough. Required to switch on. |
| `base_dn` | | Where users are searched (subtree), e.g. `DC=okul,DC=local`. |
| `user_filter` | `(sAMAccountName={username})` | LDAP filter with `{username}` (escaped). Example that also skips disabled AD accounts: `(&(objectCategory=person)(sAMAccountName={username})(!(userAccountControl:1.2.840.113556.1.4.803:=2)))`. OpenLDAP: `(uid={username})`. |
| `username_attribute` | `sAMAccountName` | Attribute that holds the panel user name (`uid` on OpenLDAP); its value is used, so `ALI` and `ali` are the same account. |
| `group_base_dn`, `group_filter` | `""`, `(\|(member={user_dn})(uniqueMember={user_dn}))` | Optional group search in addition to the user's `memberOf`. `{user_dn}` and `{username}` are escaped. Nested AD groups: `(member:1.2.840.113556.1.4.1941:={user_dn})`. |
| `group_map` | `[]` | List of `{"group": "<group DN>", "role": "viewer" \| "admin" \| "superadmin", "pages": ["devices", ...]}`. DNs compare case-insensitively and ignore spaces after commas. The highest matching role wins; pages are the union of all matches; `superadmin` gets every page. Optional `"org_scope"`: unit ids or `"all"` (see [Organisational units](api.md#organisational-units-scope)); the scopes of the matching entries are combined, and an entry without one does not widen it. When no matching entry sets a scope, a new account gets an empty scope while units exist (it sees no device until a superadmin chooses its units). |
| `timeout` | `5` | Seconds for connecting and for each answer (1–30). |
| `allow_insecure_for_tests` | `false` | Allows `security: "plain"`, and only if the backend runs with `POPS_SSO_ALLOW_INSECURE_FOR_TESTS=1` (otherwise `400`). For automated tests only (CI sets the variable); never set it on a real server. The panel never shows or sends the field, so saving from the panel turns it off. |

**OpenID Connect** (the sign-in page shows "*<name>* ile giriş yap"):

| Field | Default | Meaning |
| --- | --- | --- |
| `enabled` | `false` | Show the button and accept sign-ins. |
| `display_name` | `""` | Name on the button (e.g. "Okul hesabı"); empty: "Kurumsal hesap". |
| `issuer` | | Provider address; `<issuer>/.well-known/openid-configuration` is read and its `issuer` must be the same. Must be https. Examples: `https://login.microsoftonline.com/<tenant-id>/v2.0`, `https://accounts.google.com`, `https://sso.okul.local/realms/okul`. |
| `client_id`, `client_secret` | | The panel's registration at the provider. Without a secret the panel is a public client (PKCE only). The secret is sent with HTTP Basic, or in the body if the provider supports only `client_secret_post`. |
| `redirect_uri` | | `https://<panel address>/api/auth/oidc/callback`; register exactly this at the provider. http is accepted only for a panel on `localhost`/`127.0.0.1`. The panel's login page and the state cookie's path are derived from it. |
| `scopes` | `openid email profile` | `openid` is added when missing. Add the scope your provider needs for groups, if any. |
| `username_claim` | `email` | Claim that becomes the panel user name. `email` is lower-cased and accepted only with `email_verified: true` (an unverified address must not take someone else's name); providers that do not send `email_verified`, such as Microsoft Entra ID, need `preferred_username` or `upn` here. |
| `groups_claim` | `groups` | Claim with the user's groups (a list or one string). Entra ID sends group object IDs; Keycloak sends group paths such as `/pops-admins` with a group mapper. Empty: no groups. |
| `group_map` | `[]` | As for LDAP, but with claim values, compared case-insensitively. |
| `allowed_domains` | `[]` | If set, only users whose **verified** e-mail (`email_verified: true`) is in one of these domains can sign in, whatever their groups. |
| `default_role`, `default_pages` | `""`, `[]` | Role (`viewer` or `admin`, never `superadmin`) and pages for users of an allowed domain who match no group. Empty: they cannot sign in. Needs `allowed_domains`. |
| `default_org_scope` | `null` | Scope for those users: unit ids or `"all"`; `null` behaves like a group entry without a scope. |
| `ca_pem` | `""` | CA certificate for a provider with an internal certificate (on-premises Keycloak, AD FS). |
| `allow_insecure_for_tests` | `false` | Allows an http issuer, only with `POPS_SSO_ALLOW_INSECURE_FOR_TESTS=1` on the backend. For automated tests only; not in the panel. |

To switch both off from the server, for example when a wrong mapping locked the directory accounts out (local
accounts are not affected):

```sql
UPDATE sso_providers SET enabled = false;
```

### Agent policy object

`POST /api/agent_policies` (admin) stores, and `GET /api/agent_policies` returns:

| Field | Type | Meaning |
| --- | --- | --- |
| `fair_use_text` | string | Text the tray shows to the user in a notice that must be acknowledged. |
| `dns_categories` | list | Active DNS categories. The **Politikalar** page offers `pornografi`, `yasadisi_bahis`, `teror_siddet`, `zararli_yazilim` and `okul_ozel` (the school's own list). |
| `auto_quarantine` | bool | Quarantine a device after `quarantine_threshold` DNS violations. |
| `quarantine_threshold` | int | Violation count for `auto_quarantine`. |
| `dns_domains` | object | `{"<category>": ["example.com", ...]}`. Only these domains and their subdomains are matched. Empty means no DNS detection. |

The **Politikalar** page edits all of these fields, with one box of domains per category (one domain per line).
The server cleans the lists: lower case; `http(s)://`, paths, a leading `*.` or `.` and a trailing `.` are removed;
duplicates are dropped; at most 5000 domains per category. A request that omits `dns_domains` keeps the stored
lists; send `"dns_domains": {}` to clear them.

Agents up to 0.1.4-alpha do not start their DNS monitoring, so they report no violations and never quarantine a
PC automatically; they do show the fair-use text. See [`agent.md`](agent.md#policies).

### Notification settings

Set by a superadmin on **Sistem** → **Bildirimler** (`POST /api/system/notify-settings`) and stored in
`global_settings`:

| Key | Values | Meaning |
| --- | --- | --- |
| `notify_enabled` | `1` / `0` (default `0`) | Send notifications out by e-mail and/or webhook. **Bildirimler** in the panel's sidebar works regardless. |
| `notify_min_severity` | `info`, `medium`, `high` (default), `critical` | Lowest severity that is sent out. |
| `notify_email_to` | comma-separated addresses (at most 20) | E-mail recipients. Needs the `SMTP_*` settings in `.env`. |
| `notify_webhook_url` | `http://` or `https://` URL (at most 500 characters) that resolves to public addresses only, unless `NOTIFY_WEBHOOK_ALLOW_PRIVATE` is set | Receives a JSON `POST` for each notification. Redirects are not followed; any status of 300 or above counts as a failed delivery. |

The webhook body contains `text` (for Slack), `content` (for Discord, cut to 1900 characters), `event`, `severity`,
`title`, `detail` and `pc_name`. Which events notify, and the limits, are described in
[`security.md`](security.md#notifications).
