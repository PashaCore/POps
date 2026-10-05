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
- [x] External penetration test: F1-F8, F10 and F12-F14 closed (0.1.4); F9 and F11 remain open
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

## In progress

- [ ] **Dependencies:** Python 3.10+ (3.12 on AlmaLinux 9) so the backend can take the current FastAPI, Starlette
  and python-multipart with their security fixes; existing servers get a new virtual environment during the update.
- [ ] **Offline panel:** fonts and icons bundled with the panel, no requests to third-party hosts.
- [ ] **API input checks:** unknown fields and unknown target modes refused instead of ignored.
- [ ] **Release rhythm:** fewer, larger releases with a short operator summary at the top of each.

## Next

Short design notes. Where options are listed, the choice has not been made.

### Architecture round

*Status: planned, in this order of value.*

- **Task state machine:** created → dispatched → received → started → finished, with an attempt id per send, so
  a lost message is distinguished from a lost result.
- **Signed commands (R-01):** commands carry a signature the agent verifies, so a database or backend compromise
  cannot run code on PCs without the signing key.
- **mTLS for agents**, or pinning the server certificate through the enrollment token for self-signed setups.
- **Audit log hardening (R-07):** a separate database role that can only insert into the audit table, an anchor
  of the chain kept outside the server, and archiving old rows (decision D-18).
- **Immutable device id** with foreign keys and proper timestamps instead of text dates and `pc_name` keys.
- **Lab-scoped permissions** (an admin limited to some labs) and a server-side owner filter for helpdesk tickets.
- **Device list paging** for large fleets.
- **High availability:** several backend processes with Redis (see Vision below).
- **RDP and multi-session PCs (F19):** today the tray assumes one console session.
- **Measure again:** 5,000 agents on 0.1.12+ over HTTPS with health telemetry, and a Vision load test.
- **End-to-end tests:** Playwright for the panel in CI and a Windows test machine for the real MSI update and
  rollback.
- **Code signing:** apply to SignPath Foundation (free Authenticode for open-source projects).
- **Update loop guard:** do not send the same update again to a PC that reported `pending_reboot`.

### Linux agent (Pardus first)

*Status: planned. Implementation language not decided.*

Scope of a first version: inventory and commands only. A systemd service with the same `/ws/agent` protocol,
enrollment token and per-device secret (root-only file), hardware ID from DMI data, installed packages from
`dpkg`, commands run by the service, the same capability policy (terminal on/off), and signed updates
(`.deb` listed in the signed manifest). Screen view comes later, and X11 and Wayland need different capture paths.
The panel must show each device's OS and keep PowerShell and shell commands apart.

| Option | For | Against |
| --- | --- | --- |
| .NET on Linux | Reuses the tested protocol code and logic (manifest verification, message reassembly, capability policy, DNS matching) and `POps.Tests`; one language for all agents. | Service, WMI inventory, firewall, pipe/tray and MSI updater are Windows-only and must be rewritten anyway. Needs the .NET runtime on the PC or a large self-contained build. .NET 8 support ends on 10 November 2026, so it would start on .NET 10. |
| Small Go agent | One static binary, no runtime, simple `.deb` and systemd packaging, ed25519 in the standard library. | Adds another language to the project, which works against the bus-factor concern. Protocol and verification are reimplemented; the signed test manifest in `Agent/POps.Tests/TestData` can cross-check them. |
| Python agent | Same language as the backend; Python 3 is present on Debian-based desktops such as Pardus. | Depends on the system Python and its packages (or ships a venv); source is readable on the PC; weaker fit for screen capture later. |

### Vision beyond one school

*Status: planned.*

Today one backend process holds every agent, panel and Vision socket and forwards frames only to the panel of the
admin who holds the session (D-01, D-09 in [`docs/decisions.md`](docs/decisions.md)). Options:

- **Several workers with Redis pub/sub.** Route commands, events and frames through channels keyed by device and
  by panel user; move session grants, pending updates and notification counters to Redis or PostgreSQL. Frames
  are published per session and consumed only by the worker that holds that admin's panel socket, never
  broadcast. PostgreSQL `LISTEN/NOTIFY` could carry control events without a new service, but its payload limit
  (8000 bytes by default) rules it out for frames.
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
- Authenticode-sign the agent binaries; the tray pipe check then also requires a valid signature.
- Close pentest findings F9 and F11.
- Freeze software (Deep Freeze and similar): thaw before an update and freeze again afterwards. Today: enroll
  before freezing or use `PersistDir` ([`Agent/README.md`](Agent/README.md#machines-with-freeze-software)).
- A Turkish "Neden POps?" page for school management (terminal and screen off on staff PCs, on in labs) and a
  "terminal/ekran kapalı" badge in the device list.
- Release signing key rotation with two trusted keys in the agent (procedure in
  [`keys/README.md`](keys/README.md#rotasyon)).

## Later (not scheduled)

- Active Directory / LDAP sign-in for the panel
- Policy synchronisation similar to Group Policy
- Plugin SDK for backend and agent modules
- macOS agent
- Scheduled PDF reports
- Anomaly detection and automated remediation
- Mobile console

## Proposing changes

Open an issue or a discussion on GitHub. A change to how POps is built also gets an entry in
[`docs/decisions.md`](docs/decisions.md). See [`CONTRIBUTING.md`](CONTRIBUTING.md).
