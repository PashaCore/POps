# Architecture decisions

This log records why POps is built the way it is. Each entry gives the **context** (the problem at the time), the
**decision** and its **consequences**, including the costs. The entries were reconstructed from the code,
[`CHANGELOG.md`](../CHANGELOG.md) and the git history; the commits named under "Since" are where the decision
landed.

Adding a decision: append an entry with the next number, and add it in the same pull request as the change. Do not
rewrite an accepted entry. When a decision changes, add a new entry and mark the old one "Superseded by D-NN".

## D-01 One backend process with in-memory connection state

**Since:** 0.1.0-alpha; documented and measured in 0.1.3-alpha (`3311bf9`).

- **Context:** The backend holds one WebSocket per agent plus the panel and Vision sockets, and routes commands,
  frames and remote input between them. Doing this across several processes needs a message broker.
- **Decision:** Run a single uvicorn process (the `install.sh` unit and the Docker image start uvicorn without
  `--workers`). Connected agents, panels and Vision sockets, remote-control session grants, pending screenshots,
  updates awaiting a result and the notification dedupe/rate counters live in memory (`pops/manager.py`,
  `pops/notify.py`). Everything else is in PostgreSQL.
- **Consequences:** Capacity is one process: [`BENCHMARKS.md`](../BENCHMARKS.md) measures a lab to a few hundred
  agents, and connections are shed when 500-1000 agents connect within 1-2 s. A restart drops all live state
  (devices are marked offline at startup; agents reconnect). `--workers N` or `--scale backend=N` breaks routing.
  The scheduler already uses an advisory lock. Scaling out is on the roadmap.

## D-02 PostgreSQL only; the schema is owned by numbered SQL migrations applied at startup

**Since:** 0.1.3-alpha (`4b22524`).

- **Context:** The schema was created by an inline `init_db()` in `server.py`. There was no record of what a
  database contained, and a fresh database did not get the full schema (`7d021ea`).
- **Decision:** PostgreSQL through asyncpg is the only store. `Backend/migrate.py` applies
  `Backend/migrations/NNNN_*.sql` in file-name order, each once and in its own transaction, recorded in
  `schema_migrations` and serialized by an advisory lock. The backend runs it at startup before serving.
  `0001_baseline.sql` is idempotent, so existing databases adopted it without change.
- **Consequences:** Schema changes are only new numbered files; an applied file is never edited, and no DDL runs
  from Python. CI builds the schema on an empty PostgreSQL 13 and checks that a second run applies nothing. A code
  rollback by `pops-deploy-backend` does not undo migrations, and the deploy does not back up the database, so all
  migrations so far are additive (`IF NOT EXISTS`). A migration that changes data needs a `pg_dump` first.

## D-03 The backend is a package of routers behind a full lint gate

**Since:** 0.1.4-alpha (`db417e4`, `42439bc`).

- **Context:** `server.py` had grown to about 2,100 lines with 1,241 flake8 findings, which made review slow and
  changes risky.
- **Decision:** `server.py` only builds the app (rate limiter, CORS, static mounts, startup/shutdown, router
  wiring). The code lives in `Backend/pops/` (config, db, panel security, agent auth, audit, connection manager,
  models, task queue, DNA, WoL, notifications, scheduler) with one `APIRouter` per endpoint group in
  `pops/routers/`. `system_routes.py` stays a separate router built with injected dependencies. The split did not
  change behaviour: the route table and responses were compared before and after.
- **Consequences:** New endpoints go into a router that `server.py` includes. Modules reach the pool as
  `db.db_pool`, never `from pops.db import db_pool`. `pops/config.py` imports no other `pops` module. flake8
  (`.flake8`, 120 columns) is a full CI gate, so any new finding fails the build.

## D-04 Plain PHP panel; JWT in an httpOnly cookie, CSRF blocked by a custom header

**Since:** 0.1.1-alpha (`47fdfb1`); session revocation in 0.1.4-alpha (`3e6664a`).

- **Context:** The panel is PHP 8 with plain JavaScript and no build step. Until 0.1.0 the JWT was kept in
  `localStorage` and passed in URLs (`?_jwt=`, `?token=`), and API values were rendered unescaped (stored XSS).
- **Decision:** Keep plain PHP pages. PHP signs the user in server to server (`POPS_API_INTERNAL_URL`) and keeps
  the JWT in the `pops_jwt` cookie (`httpOnly`, `SameSite=Strict`, `Secure` over HTTPS). The browser calls `/api`
  and `/ws` on the panel's own origin. The fetch wrapper in `Dashboard/includes/header.php` adds
  `X-Requested-With: XMLHttpRequest`, which the backend requires on state-changing requests authenticated by the
  cookie. API values go through `escapeHtml()` / `jsArg()`. Each request re-reads role and `token_version`.
- **Consequences:** No Node toolchain. The web server must proxy `/api/` and `/ws/` on the panel host. XSS safety
  depends on every page escaping its output. Page permissions only decide which pages open; the API authorizes by
  role. Scripts can use `Authorization: Bearer`, which needs no CSRF header.

## D-05 Agent identity: hardware DNA plus a per-device secret, rolled out in accept-both mode

**Since:** 0.1.3-alpha (`2ef38ef`, `e58175e`, `69985a4`); takeover and binding fixes in 0.1.4-alpha (`75da2b6`).

- **Context:** Any client could connect to `/ws/agent/{id}` and act as any PC. The installed agents sent no
  credentials, so rejecting unauthenticated agents at once would have disconnected the whole fleet.
- **Decision:** The server reconciles a device's ID from a hardware fingerprint (UUID, BIOS, disk, MAC, RAM;
  `pops/dna.py`). On first connect a lab-bound, expiring enrollment token buys a per-device secret; the server
  stores only its SHA-256, the agent keeps it in the SYSTEM-only `C:\POpsData\secure`. `enforce_agent_auth` (off
  by default) switches from accept-both to rejection (`4401` / `401`, audited). Re-enrolling an enrolled device
  needs a one-time superadmin permission; agent HTTP calls must match the target device.
- **Consequences:** The fleet kept working during rollout; with enforcement off, unauthenticated agents are accepted
  and logged. A shared secret, not mTLS: a leaked database cannot be replayed; mTLS or certificate pinning stay on
  the roadmap. Freeze-software machines enroll before freezing or use `PersistDir`. Newer agent endpoints
  (software, Windows Update) require a valid secret even with enforcement off.

## D-06 Signed releases verified by server and agent; MSI updates with transactional rollback

**Since:** 0.1.3-alpha (`a3aa9a6`, `76a98cb`, `686a415`, `a9fcd8f`, `d1b913c`).

- **Context:** Up to 0.1.2 an agent installed any zip the server sent together with its SHA-256, so a compromised
  server could push a trojaned agent. A failed in-place update could leave a PC without a working agent.
- **Decision:** CI signs `manifest.json` (SHA-256 of every file, version, tag, time) with an ed25519 key held only
  as a GitHub secret for the release job. The server verifies a release before staging it (`release_verify.py`).
  Each agent verifies it again with its compiled-in key, refuses anything not newer, downloads the MSI only from
  its own server and checks size and SHA-256. `POpsUpdater` installs it, waits up to 90 s for `health.json`, and
  otherwise reinstalls the previous MSI with `POPS_ROLLBACK=1` in one Windows Installer transaction.
- **Consequences:** A compromised server can only trigger a genuinely signed, newer release. A new key needs an
  agent release carrying it; without the old private key, agents move only by a local MSI reinstall. Freshness is
  not enforced (threat 8 in `SECURITY.md`); binaries are not Authenticode-signed. The installed version's updater
  runs, so rollback changes are proven one release later ([drill](../Agent/README.md#rollback-drill)).

## D-07 Server self-update through a root systemd path unit; the backend is never root

**Since:** 0.1.3-alpha (`76e7551`, `688eb12`).

- **Context:** Updating the server needed SSH. Letting the backend (a non-root service user) run git, pip or
  systemctl would turn any backend compromise into root.
- **Decision:** `POST /api/system/self-update` (superadmin) only writes `/var/lib/pops/deploy-request.json`.
  `pops-selfupdate.path` (root) sees it and starts `pops-selfupdate.service`, which fast-forwards to `origin/main`
  and runs `pops-deploy-backend`: back up the code set, deploy, health-check, restore exactly on failure. The
  panel reads the progress from `deploy-status.json`. The request file's content is never executed.
- **Consequences:** Only `origin/main` can be deployed; there is no version picker. Offline, the script redeploys
  the local checkout. Without the units the endpoint answers `503`. Not available in Docker. The deploy backs up
  code (and the venv when `requirements.txt` changes), not the database. With `/etc/pops/allowed_signers` only
  release tags signed by a listed SSH key are deployed. Details: [`self-update.md`](self-update.md).

## D-08 Capability policy: the PC decides, the server can only switch off

**Since:** 0.1.4-alpha (`6f091c3`, `ffb63cb`).

- **Context:** Threat 4 in `SECURITY.md`: whoever controls the server can run commands as SYSTEM on every PC and
  watch screens. Signing protects updates, not ordinary commands.
- **Decision:** `C:\POpsData\secure\capabilities.json` (SYSTEM and Administrators only) holds `terminal_enabled`
  (`execute`) and `vision_enabled` (streaming, previews, remote input). The MSI sets them either way
  (`TERMINAL_ENABLED`, `VISION_ENABLED`). The server can only send `false`; an "enable" from the server is ignored
  and logged. An unreadable file counts as both disabled. Refusals are reported as `capability_denied`; the server
  stores standing disables and re-applies them when the device reconnects.
- **Consequences:** Where a capability is off, even a compromised server cannot use it. Switching it back on needs a
  local administrator (MSI repair or reinstall). Only agents from 0.1.4-alpha enforce it; on other PCs the server
  and panel accounts remain the most valuable target.

## D-09 Remote input only in an accepted or announced session; frames only to that admin

**Since:** 0.1.4-alpha (`8589c7e`, `a9d1903`, `8017560`; pentest findings F1, F5, F12).

- **Context:** Remote mouse/keyboard and `execute` over the remote-input path were open to any signed-in user,
  including viewers; live frames went to every panel; any local user could pose as the tray on the named pipe.
- **Decision:** Server: input and previews require admin or superadmin; input also needs an open audit session for
  that device, opened with a reason and expiring after 30 minutes without activity. The session start goes to the
  hash-chained audit log (never keystrokes). Frames go only to panels of users who hold a live session for that
  device. Agent: input is applied only while a session is active that the tray started after consent or the
  mandatory notice, and the pipe accepts only the installed `POpsTray.exe` in a user session.
- **Consequences:** Two independent gates: a compromised server alone cannot drive a PC's input. Previews are
  announced on the tray instead of asking. Session grants are in memory (D-01), so a restart ends them. Any Vision
  scaling work must keep "frames only to the viewing admin".

## D-10 Security audit log: a server-only table with a SHA-256 hash chain

**Since:** 0.1.3-alpha (`4190339`); more events in 0.1.4-alpha (`3e6664a`).

- **Context:** `agent_logs_v2` is also written by agents (`POST /api/logs`), so it cannot prove what an admin did,
  and plain rows can be changed without trace.
- **Decision:** Security-relevant events (enrollment and rejections, session starts, lockdown, bypass codes, SYSTEM
  commands with their requester, update results, releases, enforcement, capability and settings changes,
  schedules) go to `device_audit_logs`, written only by server code through `pops/audit.py`. Each row stores
  `prev_hash` and `entry_hash` (SHA-256 over the previous hash and the row), inserted under a transaction-level
  advisory lock. `GET /api/system/audit-verify` walks the chain.
- **Consequences:** Tampering is detectable, not prevented: the application's database role can still update or
  delete rows (a separate owner role is the documented next step). Audit writes are serialized. Rows from before
  migration `0004` have no hash. Only metadata is recorded, never keystrokes.

## D-11 Notifications only for server-decided events; SSRF-guarded webhooks

**Since:** Unreleased (`bd622e5`, `32e67d4`, `e186fe3`).

- **Context:** Failed updates and takeover attempts must reach admins. But agents can write any `risk_level` into
  `/api/logs`, and before enforcement anyone can pose as an agent. A webhook URL typed into the panel makes the
  server send requests on the admin's behalf (SSRF towards internal services or cloud metadata).
- **Decision:** Only events the server decides on raise notifications; DNS alerts only from enrolled agents, one per
  device and category every 10 minutes. The same event is sent at most once per 10 minutes and at most 30 per 10
  minutes, in the background. Webhook hosts are resolved and every address must be public, both when saved and
  when sent; the connection is pinned to the checked address and redirects are not followed.
- **Consequences:** Severity an agent claims never notifies; dedupe and the send cap limit what goes out. Posting to an
  internal system needs `NOTIFY_WEBHOOK_ALLOW_PRIVATE=1` (the CI tests set it for their local receiver). SMTP
  credentials stay in `.env`. Dedupe and rate counters are per process (D-01).

## D-12 DNS policy is detection only, against exact domain lists

**Since:** 0.1.4-alpha (`8017560`, finding F8); lists kept when the policy page saves since `d4ad236`.

- **Context:** Detection parsed `ipconfig /displaydns` for the English label "Record Name", so it never worked on
  Turkish Windows, and substring matching would have flagged innocent names (`essex.ac.uk`) in a minor's record.
- **Decision:** The agent reads the DNS client cache through the Windows DNS API and flags a name only if it
  equals, or is a subdomain of, a domain in the policy's `dns_domains` list for an active category. The lists are
  empty by default, and an empty list flags nothing. Matches are reported and, if a school enables
  `auto_quarantine`, quarantine the PC after a threshold. Nothing is blocked, and the panel does not claim so.
- **Consequences:** No substring false positives, at the cost of lists each school maintains. Lookups that bypass
  the Windows DNS client cache, such as a browser's built-in DNS-over-HTTPS, are not seen. A blocking mode is
  planned separately ([`ROADMAP.md`](../ROADMAP.md)).

## D-13 Screen capture runs in the tray; the standalone POpsVision is not shipped

**Since:** release packages since 0.1.2-alpha (`59c0426`); MSI in 0.1.3-alpha (`a9fcd8f`); pipe check in
0.1.4-alpha (`8017560`).

- **Context:** The service runs as LocalSystem in session 0 and cannot capture a user's desktop. The original
  design ran a separate `POpsVision.exe` in the user session beside the tray, which already runs there and shows
  the consent dialogs and notices.
- **Decision:** `POpsTray` captures the primary screen and applies remote input, talking to the service over
  `POpsTrayPipe`; the service relays frames over `/ws/vision`. Packages and the MSI contain only POpsAgent,
  POpsTray, POpsWatchdog and POpsUpdater (with `POps.Shared.dll`).
- **Consequences:** One user-session process to verify, with consent and capture in the same process. Vision needs
  a signed-in user; the watchdog restarts a stopped tray. `Agent/POpsVision` remains as legacy source (still built
  by CI and watched by Dependabot), and the watchdog log text and the MSI and updater close lists still name it.
  Removing it is open cleanup.
- **0.1.14-alpha:** the source was removed as well (`Agent/POpsVision`, its CI build and Dependabot entry), and
  the watchdog no longer names it. `POpsVision.exe` stays only in the MSI close list (`Package.wxs`) and the updater's
  `UserProcesses`: an upgrade from a very old install may still find it running and holding files open.

## D-14 Native install is the primary path; Docker Compose is optional

**Since:** native installer in 0.1.3-alpha (`28dd181`); Docker Compose in Unreleased (`b9e7701`).

- **Context:** Schools run POps on one Linux server that often hosts other services. The reference target
  (AlmaLinux/RHEL 9) ships Python 3.9, and the panel self-update relies on systemd on the host.
- **Decision:** `Installer/server/install.sh` sets up PostgreSQL, a venv, `.env` with generated secrets, the
  migrations and a systemd unit on dnf and apt systems. `docker-compose.yml` (PostgreSQL, backend, panel behind
  Apache) is offered for hosts that already run containers.
- **Consequences:** CI tests the backend on Python 3.9 and migrations on PostgreSQL 13, while Compose uses
  `postgres:17`. In Docker there is no panel self-update, the server's Wake-on-LAN broadcast usually does not reach
  the LAN, TLS is not included, and the backend must stay one container (D-01). Two install paths to document.

## D-15 Offline-first: the server works without internet access

**Since:** 0.1.3-alpha (`658eb6e`, `76a98cb`, `2720d46`); GitHub download in Unreleased (`e062721`).

- **Context:** Many school servers have no or filtered internet access but still need updates and management.
- **Decision:** Nothing on the server depends on GitHub. Version and release-note checks use short timeouts, run
  off the event loop, are cached and never fatal. Agent releases can be uploaded by hand, and the one-click GitHub
  download runs the same checks: trust comes from the signature, not the source. Agents download updates only
  from their own server. Self-update redeploys the local checkout when `git fetch` fails. 2FA uses the standard
  library and a locally hosted QR script instead of a new dependency.
- **Consequences:** Offline servers only lose the update check, release notes and one-click download. The panel
  still loads Chart.js, SortableJS, Font Awesome and Google Fonts from public CDNs, so the administrator's browser
  needs internet for charts, icons and fonts. New external calls must be optional and fail soft.

## D-16 One version source

**Since:** 0.1.3-alpha (`33d870d`).

- **Context:** Components carried their own hard-coded versions (for example `2.0.1-GHOST-SERGEANT` in the
  watchdog). Signed updates compare versions, so a wrong number breaks the downgrade check.
- **Decision:** The repository-root `VERSION` file is the only version. `Agent/Directory.Build.props` feeds it to
  every agent project, components read it from their assembly, the server reads the `VERSION` deployed next to it,
  and the MSI uses its numeric `a.b.c` plus the CI run number. CI fails when `VERSION`, the top CHANGELOG release
  heading and the built `<Version>` differ; the release job also requires the tag to be `v<VERSION>`.
- **Consequences:** A release is one commit that changes `VERSION` and the CHANGELOG heading together. `VERSION`
  must start with a numeric `a.b.c`. The manifest version equals the agent version the "newer than me" check uses.

## D-17 Transparent by design

**Since:** the start of the project; stealth mode removed in 0.1.2-alpha (`704588a`).

- **Context:** POps runs on PCs used by students (often minors) and staff, under KVKK, and its remote features are
  powerful. Covert monitoring would destroy the trust the product depends on.
- **Decision:** Remote sessions ask for consent, or show a full-screen countdown with the recorded reason for
  mandatory ones. Previews are announced on the tray. The tray is always visible: the `--stealth` mode and the
  students' pause items were removed. There is no keystroke logging, and the tray log records message types only.
- **Consequences:** Silent workflows that other tools offer are deliberately impossible, and Vision needs a
  signed-in user. New features that observe users must follow the same rules. A KVKK notice template is in
  [`kvkk-aydinlatma.md`](kvkk-aydinlatma.md).

## D-18 Retention deletes logs, never the audit chain; the audit log is archived, not trimmed

**Since:** 0.1.14-alpha.

- **Context:** Agent event logs, task output and notifications grow without limit and hold personal data (user
  names, program names, command output). KVKK expects data to be kept only as long as needed. The security audit
  log (`device_audit_logs`, D-10) is a hash chain: deleting old rows breaks verification from the first row.
- **Decision:** A daily job deletes, in chunks of 5000, agent event logs and finished tasks older than 365 days
  and read notifications older than 90 (superadmin settings `retention_days_*`, `0` = keep). Pending, running and
  unknown tasks, the audit chain and the Vision session records are never deleted by it.
- **Audit archive (planned, not built):** export the oldest rows of `device_audit_logs` up to a cut-off id into a
  signed, compressed file kept off the server; store the cut-off id and the `entry_hash` of the last archived row
  as an anchor (a new table, written in the same transaction that deletes the rows); `audit-verify` then starts
  from the anchor instead of the first row and the archive can be verified on its own. Together with a separate
  database role for the audit table (R-07) and an external copy of the anchor this also makes truncation detectable.
- **Consequences:** Defaults keep a school year of history. Shorter periods are a per-school decision recorded in
  its KVKK notice. Until the archive exists the audit table keeps growing (it is small: metadata only).

## D-19 The agent ships its own .NET runtime (self-contained)

**Since:** 0.1.15-alpha.

- **Context:** .NET 8 support ends on 10 November 2026. Up to 0.1.14 the agent was framework-dependent and the MSI
  refused to install without the .NET 8 Desktop Runtime. The agent updates itself: a framework-dependent .NET 10 MSI
  cannot install on a PC without the .NET 10 Desktop Runtime, so the update would roll back and the fleet would stay
  on 0.1.14 until someone installed the runtime on every PC by hand.
- **Decision:** The service, tray, watchdog and updater target .NET 10 (LTS) and are published self-contained for
  win-x64 into one folder that shares the runtime files; the MSI collects the folder (`Files`) and has no runtime
  check. No trimming (Windows Forms does not support it) and no ReadyToRun (9 MB more, no measurable start-up
  gain). Only Turkish satellite resources are shipped. The tray is published last because it needs the
  WindowsDesktop builds of `System.Drawing.dll` and `Microsoft.VisualBasic.dll` (the others accept them, the
  reverse breaks the tray); the release workflow fails if that order is lost.
- **Consequences:** No prerequisite on the PC. The MSI grows from about 4 MB to about 41 MB (about 270 files,
  117 MB installed), and each self-update copies the updater with its runtime (about 80 MB) to
  `C:\POpsData\updater`. .NET security fixes reach PCs only through an agent release, not through Windows Update:
  a .NET 10 patch means rebuilding and shipping the agent. Rolling back to 0.1.14 or older runs on the .NET 8
  runtime the PC still has; the MSI never removes it.
