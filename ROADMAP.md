# POps Roadmap

POps is alpha software; the current version is on the [releases page](https://github.com/PashaCore/POps/releases/latest).
This page lists what is done, what is being built
and what is planned. Items under **Next** and **Later** are plans, not features: nothing there is available until
it appears in [`CHANGELOG.md`](CHANGELOG.md). There are no dates. The reasons behind existing designs are in
[`docs/decisions.md`](docs/decisions.md).

## Done

Details and upgrade notes: [`CHANGELOG.md`](CHANGELOG.md). "Unreleased" means merged on `main` but not yet in a
tagged release.

### Foundation (0.1.0-alpha)

- [x] Windows agent (.NET 10, self-contained): service, tray, watchdog, updater
- [x] Live state over WebSockets: heartbeats, CPU/RAM, hardware inventory
- [x] Web panel (PHP 8) and FastAPI backend with PostgreSQL
- [x] Vision: 1-5 FPS screen view and remote control
- [x] Software deployment (ZIP/MSI) through a task queue; remote PowerShell terminal
- [x] Quarantine lock screen; offline bypass codes followed in 0.1.1, working network isolation in 0.1.4 (F7)
- [x] Event log

### Security (0.1.1-alpha to 0.1.4-alpha)

- [x] Secrets only in `.env`; leaked passwords rotated by `setup_env.py` (0.1.1)
- [x] Panel JWT in an httpOnly cookie, CSRF header, escaped output (0.1.1)
- [x] bcrypt-only login, superadmin-only user management, pinned backend dependencies (0.1.2)
- [x] Agent enrollment tokens, per-device secrets, `enforce_agent_auth` (0.1.3)
- [x] Agent refuses non-TLS servers; secrets in a SYSTEM-only store (0.1.3)
- [x] Opt-in TOTP 2FA for panel accounts (0.1.3)
- [x] Hash-chained, agent-unwritable security audit log with a verify endpoint (0.1.3)
- [x] External penetration test: F1-F8, F10 and F12-F14 closed (0.1.4); F9 (diagnostic buttons) closed by the
  admin-only, session-bound command path (0.1.4), F11 (unauthenticated downloads) by signed deployment links (0.1.13);
  `/updates` serves only packages that the PC checks against the signed manifest
- [x] Immediate session revocation (`token_version`) and per-command accountability (0.1.4)
- [x] Capability policy: a PC can disable the terminal and Vision; the server can only switch them off (0.1.4)
- [x] Remote input only in an accepted or announced session; frames only to that admin (0.1.4)
- [x] Webhooks cannot target internal addresses (SSRF guard) (0.1.5)

### Updates and releases

- [x] Single version source (`VERSION`) checked in CI (0.1.3)
- [x] ed25519-signed release manifests, verified by the server and again by each agent (0.1.3)
- [x] Agent MSI (WiX) with health check and transactional rollback; rollback drill (0.1.3)
- [x] Signed agent updates dispatched from the panel; offline upload of releases (0.1.3)
- [x] Server self-update from the panel through a root systemd path unit (0.1.3)
- [x] One-click download and verification of the signed agent release from GitHub (0.1.5)
- [x] Release notes from the CHANGELOG on the **Sistem & Sürüm** page (0.1.5)
- [x] Rollback that really restores the previous agent, proven by drills on real PCs (0.1.7, 0.1.8); an update
  counts as successful only once the new agent is operational (0.1.12)
- [x] Server self-update follows release tags and can require SSH-signed tags; the release waits for the full test
  suite (0.1.12, 0.1.13). The project's tags are signed from 0.1.22.

### Maintainability

- [x] Database migrations (`Backend/migrate.py`), tested on an empty PostgreSQL in CI (0.1.3)
- [x] Backend split into the `Backend/pops/` package; flake8 gate at zero findings (0.1.4)
- [x] Shared agent library `POps.Shared` and xUnit tests in CI (0.1.4)
- [x] CI builds every agent; CodeQL and Dependabot (0.1.2)
- [x] Security invariants tested against a running server in CI (0.1.3)
- [x] Agent simulator and a first measured baseline ([`BENCHMARKS.md`](BENCHMARKS.md)) (0.1.3)
- [x] Operator documentation in `docs/` (0.1.5)
- [x] Structured logs with request IDs, Prometheus `/metrics`, diagnostics page (0.1.7); tested backups with
  restore check (0.1.7)

### Operations (0.1.5–0.1.6)

- [x] Notifications: panel bell, e-mail, webhook (Slack, Discord, Teams or any JSON endpoint)
- [x] Scheduled tasks (once, daily, chosen weekdays) through the normal task queue
- [x] Reports page with CSV export (formula-safe)
- [x] Software inventory and Windows Update status: server side and panel
- [x] Helpdesk (**Yardım Masası**): tickets from the panel, plus the server endpoint for tickets from the tray
- [x] Licence tracking against the software inventory (**Lisanslar** tab on **Raporlar**)
- [x] Auto-enrollment lab rule; reworked **Politikalar** page with per-category DNS domain lists
- [x] Optional Docker Compose packaging
- [x] Agent side of software inventory and Windows Update status, with on-request update installs (0.1.5)
- [x] **Sorun bildir** in the tray: students and staff open helpdesk tickets (0.1.6)

### Hardening after external reviews (0.1.8–0.1.13)

- [x] Reconnect with exponential backoff and jitter; 2000 agents back in ~4 s after a restart (0.1.8, 0.1.11)
- [x] The tray shows what administrators did on this PC in the last 30 days (0.1.9)
- [x] HTTPS set up by the installer (school CA or Let's Encrypt) and agents pinned to that CA (0.1.10)
- [x] Quarantine cannot be escaped through Task Manager, switch user or sign-out (0.1.11)
- [x] Security audit R-01..R-20 (0.1.12): Vision only with the device's own key, per-device bypass keys, local
  Windows event log of remote actions, agent health in the panel
- [x] Second review F01–F21 (0.1.13): no root writes into backend-writable paths, enrollment in one transaction
  with hashed tokens, identity bound to the device key, revoked panel sessions closed, honest task states with exit
  codes and cancel, bounded command output, once-per-day bypass codes, signed deployment links

### Reliability, modules and the new panel (0.1.14–0.1.21)

- [x] **Reliability:** atomic scheduled tasks, stuck-task timeout, duplicate-task guard, database time limits,
  batched heartbeats, per-panel send queues, retention, disk and certificate alerts, load figures (0.1.14)
- [x] **Security:** 2FA codes work once and are encrypted at rest; deploy settings from a root-owned file with venv
  rollback; actions pinned to commit SHAs; XSS check in CI (0.1.14)
- [x] **Agent:** Turkish keyboard in remote control, update results kept until the server confirms them,
  quarantine follows a server address change, leftover command files removed, standalone POpsVision removed (0.1.14)
- [x] Agent on .NET 10 with its own runtime, no prerequisite on the PC; modules per organisation and per lab with
  install profiles (0.1.15)
- [x] New panel: one layout on every page, one action bar, details on click, and who / when / from where / result /
  reason on every operation (0.1.16, 0.1.17)
- [x] Addresses without `.php`, event log in pages with filters, Sistem and Ayarlar in tabs, server charts on
  **Sistem → Genel bakış** (0.1.18–0.1.21)

### After the 0.1.21 reviews (0.1.22)

- [x] **Dependencies:** Python 3.10+ (3.12 on AlmaLinux 9) with the current FastAPI, Starlette and python-multipart;
  existing servers get a new virtual environment during the update (0.1.22)
- [x] **Offline panel:** fonts and icons bundled with the panel, no requests to third-party hosts (0.1.22)
- [x] **API input checks:** unknown fields and unknown target modes refused instead of ignored (0.1.22)
- [x] **Release rhythm:** at most one planned release a week (security fixes in between), with a short operator summary on top; signed release tags (0.1.22)
- [x] Organisation name and logo on the sign-in page; Docker images on GHCR; OpenSSF Scorecard (0.1.22)

### After the 0.1.21 reviews (0.1.23)

- [x] **API for automation:** `/api/v1`, REST names next to the old ones, API tokens, the OpenAPI file in the
  repository (0.1.23)
- [x] **Panel end-to-end tests** in CI with a real browser: every page at desktop and phone width, and the main flows
  (0.1.23)
- [x] **Agent protocol** as JSON Schema with shared test vectors; hash-locked backend dependencies (0.1.23)
- [x] **Public read-only demo** with a fake school fleet, reset every night (0.1.23)
- [x] **Agent:** update stages in the panel, BITS download that resumes, log retention, offline bypass while the
  server is unreachable, warnings as errors in the build (0.1.23)
- [x] **Vision v2, agent side:** DXGI capture, changed regions, several screens, adaptive quality, binary frames,
  clipboard in an accepted session (0.1.23)
- [x] **English interface:** every page in Turkish and English, chosen per browser (0.1.23)
- [x] **Vision v2 in the panel:** binary relay, canvas viewer, screen picker, quality, clipboard (0.1.23)
- [x] **Linux agent, first version** (Pardus/Debian): inventory, commands, signed updates with rollback (0.1.23)
- [x] **Exam mode, file transfer, winget, power actions and messages to the user** (server, panel and agent)
  (0.1.23)
- [x] **Sign-in with LDAP/AD and OpenID Connect; organisational units with scoped admins; real timestamps**
  (0.1.23)
- [x] **Modules screen, strict request bodies, GLPI export; optional several backend workers with Redis**
  (0.1.23)
- [x] **Supply chain:** hash-locked CI tools, pinned images, read-only workflow tokens, fuzzing (0.1.23)

## In progress

Nothing right now; the next items are below.

## Next

Short design notes. Where options are listed, the choice has not been made.

### Code signing (first)

*Status: next, first priority.*

The agent service and the updater run as SYSTEM, and the tray and watchdog run in the signed-in user's session,
but the executables and the MSI have no Authenticode signature, so SmartScreen and antivirus products may warn or
block them. Apply to SignPath Foundation (free Authenticode for open-source projects). The policy that will apply is in
[`docs/code-signing.md`](docs/code-signing.md). Once the binaries are signed, the tray pipe check also requires a
valid signature.

### Architecture round

*Status: planned, in this order of value.*

- **Task state machine:** created → dispatched → received → started → finished, with an attempt id per send, so
  a lost message is distinguished from a lost result.
- **Signed commands (R-01):** commands carry a signature the agent verifies, so a database or backend compromise
  cannot run code on PCs without the signing key.
- **mTLS for agents**, or pinning the server certificate through the enrollment token for self-signed setups.
- **Audit log hardening (R-07):** a separate database role that can only insert into the audit table, an anchor
  of the chain kept outside the server, and archiving old rows (decision D-18).
- **Immutable device id** with foreign keys instead of `pc_name` keys. (Proper timestamps instead of text dates are
  done: migration `0031`.)
- **Server-side owner filter for helpdesk tickets** (an admin sees the tickets assigned to them). Lab-scoped
  permissions are done as organisational units (district → school, scope per user and token; D-25).
- **Device list paging** for large fleets.
- **High availability:** done for the backend: several workers with Redis, off by default
  ([`docs/ha.md`](docs/ha.md)). PostgreSQL itself relies on standard replication.
- **RDP and multi-session PCs (F19):** today the tray assumes one console session.
- **Measure again:** 5,000 agents on 0.1.12+ over HTTPS with health telemetry, and a Vision load test.
- **End-to-end tests on Windows:** a Windows test machine for the real MSI update and rollback (the panel's
  browser tests already run in CI).
- **Update loop guard:** do not send the same update again to a PC that reported `pending_reboot`.

### Linux agent (Pardus first)

*Status: first version (in `Agent-Linux/`; decision [D-22](docs/decisions.md#d-22-the-linux-agent-is-python-3-on-the-distributions-own-packages)).*

Done in the first version: a systemd service with the same `/ws/agent` protocol, enrollment token and per-device
secret (root-only file), hardware ID and DNA from DMI data, hardware inventory and installed packages from `dpkg`,
commands run as root with `/bin/sh` and the Windows limits, results kept until the server acknowledges them, the same
capability policy (terminal on/off, the server can only switch off), signed updates (`pops-agent_<version>_all.deb`
in the signed manifest, installed from a transient systemd unit, rolled back to the previous `.deb` if the new one
does not come up), a local append-only audit log, and `clients.platform` so the panel shows each device's OS and
**Uzak komut** keeps Windows and shell commands apart. Supported: Debian 12 / Pardus 23 and later, Ubuntu 24.04.

Next for Linux:

- **Screen view:** X11 and Wayland need different capture paths (PipeWire portal on Wayland, with the user's
  consent dialog), and a user-session helper in place of the Windows tray.
- **Quarantine:** an nftables table that allows only the server, plus a lock screen in the user session; the offline
  bypass key is already stored and acknowledged.
- **Tray and notifications:** messages to the signed-in user (`notify-send` through the session bus), the fair-use
  notice and the help desk.
- **Power and message buttons:** the agent already maps the panel's restart and shut-down commands; a native
  message path needs the session helper above.

Options that were considered for the language:

Many Turkish public schools use Pardus, including Pardus ETAP on classroom interactive boards. TÜBİTAK ULAKBİM, which
develops Pardus, also develops Lider Ahenk, a central management system for Pardus machines and ETAP boards.
**Integrating with Lider Ahenk** instead of, or before, shipping a POps agent for Linux is an alternative path: POps
would then show and act on Pardus machines through a system the school may already use. It has not been evaluated
yet; the choice between the options above and this path is open.

| Option | For | Against |
| --- | --- | --- |
| .NET on Linux | Reuses the tested protocol code and logic (manifest verification, message reassembly, capability policy, DNS matching) and `POps.Tests`; one language for all agents. | Service, WMI inventory, firewall, pipe/tray and MSI updater are Windows-only and must be rewritten anyway. Needs the .NET runtime on the PC or a large self-contained build whose security fixes only come with a POps release. Not chosen now (D-22). |
| Small Go agent | One static binary, no runtime, simple `.deb` and systemd packaging, ed25519 in the standard library. | Adds another language to the project, which works against the bus-factor concern. Protocol and verification are reimplemented. Not chosen now; still the option for a compiled capture helper later (D-22). |
| **Python agent (chosen)** | Same language as the backend; Python 3 is present on Debian-based desktops such as Pardus; tested end to end on Linux CI; a 45 KB `Architecture: all` package. | Depends on the system Python and its `python3-websockets` / `python3-cryptography` (10+ / 38+); source is readable on the PC; weaker fit for screen capture later. |

### Integrations

*Status: first version implemented.*

- **GLPI export:** send POps inventory (computers, installed software) and helpdesk tickets to GLPI through its REST
  API, with POps as the source of truth for what agents report. Implemented: computers, software, tickets and public
  replies, on a schedule or on demand (**Sistem** → **Entegrasyonlar**). Next: hardware components, the operating
  system and network ports, ticket categories, a dry run and a test against a real GLPI. Mappings, sync direction,
  conflicts and authentication: [`docs/integrations/glpi.md`](docs/integrations/glpi.md).

### Vision beyond one school

*Status: several workers with Redis done ([`docs/ha.md`](docs/ha.md)); a separate relay and WebRTC open.*

By default one backend process holds every agent, panel and Vision socket and forwards frames only to the panel of
the admin who holds the session (D-01, D-09 in [`docs/decisions.md`](docs/decisions.md)). Options:

- **Several workers with Redis pub/sub (done, D-26).** Commands, events and frames go through channels keyed by
  device and by panel user; session grants, the online-agent registry, notification counters and rate limits are in
  Redis, pending updates in PostgreSQL. Frames are published only to the channels of the session holders and
  consumed by the worker that holds that admin's panel socket, never broadcast. Off unless `REDIS_URL` is set.
  PostgreSQL `LISTEN/NOTIFY` was not used: its payload limit (8000 bytes by default) rules it out for frames.
- **A separate Vision relay.** Agents' `/ws/vision` and the viewing panel connect to a relay; the backend issues a
  short-lived ticket (device, admin, expiry) that the relay checks. This keeps frame traffic off the API process
  and can run per school.
- **WebRTC, later.** Browser-native video and less server bandwidth, but it needs a WebRTC stack in the tray and a
  TURN server for labs behind NAT. Signalling stays on the backend so consent and audit remain server-side.

Whatever is chosen must keep: consent or notice, admin-only access, the recorded reason, input only in
tray-started sessions, frames only to the viewing admin, and the agent-side capability policy.

### DNS "block" mode

*Status: planned. Detection stays the default.*

- Block listed domains on the PC using the same exact domain/subdomain lists. Options to evaluate: `hosts` file
  entries (simple, no wildcards, so only listed names), Windows Firewall rules on resolved addresses (shared CDN
  addresses over-block and addresses change), or Name Resolution Policy Table rules that send a domain and its
  subdomains to a sinkhole.
- Turn off browsers' built-in DNS-over-HTTPS through policy (for example Chrome/Edge `DnsOverHttpsMode`, Firefox
  `DNSOverHTTPS`), since those lookups bypass the Windows resolver. This also makes detection mode see them.
- Per lab and reversible: the previous state is saved and restored when the mode is turned off or the agent is
  removed, the way quarantine saves and restores the firewall state. Changes go to the audit log, and the tray
  tells the user why a site is blocked.
- A school DNS resolver or filter at network level remains an alternative outside the agent.

### Load-testing profile

*Status: planned.*

[`tools/agent_simulator.py`](tools/agent_simulator.py) measures connect and heartbeat only. A profile for a whole
school should add:

- connect jitter and reconnects instead of a single burst, with agents enrolled and `enforce_agent_auth` on;
- inventory, software and Windows Update reports, and policy polling every 60 s;
- a command sent to every agent through the task queue at the configured concurrency limit, plus scheduled tasks;
- several panels on `/ws/panel` and concurrent Vision sessions at 2 and 5 FPS with frame sizes taken from a real
  tray capture.

Measure connect success, command round-trip (p50/p95), frame latency and dropped frames per viewer, backend CPU
and memory, database pool use and bandwidth. Target sizes (one lab, one school, several schools) are agreed with
pilot schools. Results go into [`BENCHMARKS.md`](BENCHMARKS.md) with the hardware used, measured on a throwaway
server, never on a production one.

### Support model

*Status: owner decision, not made. Options only.*

| Option | What it means | What it needs |
| --- | --- | --- |
| Community only | GitHub issues and discussions, best effort; security reports as in [`SECURITY.md`](SECURITY.md). | Maintainer time for triage. No response-time promise. |
| Paid support with an SLA | Pasha Core offers installation, upgrades and support contracts with defined response times. | More than one person able to answer, release and fix; an on-call rota; contract terms. |
| Partner model | Local IT firms or district IT teams install and give first-line support; the project gives second-line support. | Training material, a certification path, partner agreements. |
| Hybrid | Software and community support stay free; installation and SLA-backed support are paid. | The needs of both models above. |

Whatever the choice, a second maintainer with review and release rights would remove the single-person dependency
(`.github/CODEOWNERS` lists one account today).

## Other open items

- ~~Move the agent to .NET 10 before .NET 8 support ends (10 November 2026).~~ Done in 0.1.15-alpha (self-contained, D-19).
- ~~Move the backend to a newer Python.~~ Done in 0.1.22: Python 3.10+, target 3.12 (D-20).
- Per-action re-authentication for dangerous panel actions; enforce freshness of signed manifests.
- Freeze software (Deep Freeze and similar): thaw before an update and freeze again afterwards. Today: enroll
  before freezing or use `PersistDir` ([`Agent/README.md`](Agent/README.md#machines-with-freeze-software)).
- ~~A Turkish "Neden POps?" page for school management (terminal and screen off on staff PCs, on in labs).~~ Done:
  [`docs/tr/neden-pops.md`](docs/tr/neden-pops.md), with the other Turkish guides for schools in [`docs/tr/`](docs/tr).
- A "terminal/ekran kapalı" badge in the device list.
- Release signing key rotation with two trusted keys in the agent (procedure in
  [`keys/README.md`](keys/README.md#rotasyon)).

## Later (not scheduled)

- Policy synchronisation similar to Group Policy
- Plugin SDK for backend and agent modules
- macOS agent
- Scheduled PDF reports
- Anomaly detection and automated remediation
- Mobile console

## Proposing changes

Open an issue or a discussion on GitHub. A change to how POps is built also gets an entry in
[`docs/decisions.md`](docs/decisions.md). See [`CONTRIBUTING.md`](CONTRIBUTING.md).
