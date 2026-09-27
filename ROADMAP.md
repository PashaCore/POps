# POps Roadmap

POps is alpha software; the latest release is **0.1.4-alpha**. This page lists what is done, what is being built
and what is planned. Items under **Next** and **Later** are plans, not features: nothing there is available until
it appears in [`CHANGELOG.md`](CHANGELOG.md). There are no dates. The reasons behind existing designs are in
[`docs/decisions.md`](docs/decisions.md).

## Done

Details and upgrade notes: [`CHANGELOG.md`](CHANGELOG.md). "Unreleased" means merged on `main` but not yet in a
tagged release.

### Foundation (0.1.0-alpha)

- [x] Windows agent (.NET 8): service, tray, watchdog, updater
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
- [x] Webhooks cannot target internal addresses (SSRF guard) (Unreleased)

### Updates and releases

- [x] Single version source (`VERSION`) checked in CI (0.1.3)
- [x] ed25519-signed release manifests, verified by the server and again by each agent (0.1.3)
- [x] Agent MSI (WiX) with health check and transactional rollback; rollback drill (0.1.3)
- [x] Signed agent updates dispatched from the panel; offline upload of releases (0.1.3)
- [x] Server self-update from the panel through a root systemd path unit (0.1.3)
- [x] One-click download and verification of the signed agent release from GitHub (Unreleased)
- [x] Release notes from the CHANGELOG on the **Sistem & Sürüm** page (Unreleased)

### Maintainability

- [x] Database migrations (`Backend/migrate.py`), tested on an empty PostgreSQL in CI (0.1.3)
- [x] Backend split into the `Backend/pops/` package; flake8 gate at zero findings (0.1.4)
- [x] Shared agent library `POps.Shared` and xUnit tests in CI (0.1.4)
- [x] CI builds every agent; CodeQL and Dependabot (0.1.2)
- [x] Security invariants tested against a running server in CI (0.1.3)
- [x] Agent simulator and a first measured baseline ([`BENCHMARKS.md`](BENCHMARKS.md)) (0.1.3)
- [x] Operator documentation in `docs/` (Unreleased)

### Operations (Unreleased)

- [x] Notifications: panel bell, e-mail, webhook (Slack, Discord, Teams or any JSON endpoint)
- [x] Scheduled tasks (once, daily, chosen weekdays) through the normal task queue
- [x] Reports page with CSV export (formula-safe)
- [x] Software inventory and Windows Update status: server side and panel
- [x] Helpdesk (**Yardım Masası**): tickets from the panel, plus the server endpoint for tickets from the tray
- [x] Licence tracking against the software inventory (**Lisanslar** tab on **Raporlar**)
- [x] Auto-enrollment lab rule; reworked **Politikalar** page with per-category DNS domain lists
- [x] Optional Docker Compose packaging

## In progress

- [ ] **Agent side of software inventory and Windows Update status** (planned for 0.1.5-alpha). The server
  already accepts the reports from enrolled agents.
- [ ] **Rollback drill of the 0.1.4-alpha updater** during the 0.1.5-alpha rollout
  ([`Agent/README.md`](Agent/README.md#rollback-drill)).
- [ ] **"Sorun bildir" (report a problem) in the tray**: the agent half of the helpdesk; see below.

## Next

Short design notes. Where options are listed, the choice has not been made.

### Helpdesk and licences

*Status: server side and panel done (Unreleased); the tray part is next.*

- **Tray "Sorun bildir".** A form in the tray (category, subject, description) that opens a ticket for that PC
  and the signed-in user through `POST /api/tickets/agent/{hw_id}` and shows the replies. The server already
  accepts only enrolled agents there and limits each device (5 open tickets, 10 new per hour), so an unenrolled
  or misbehaving client cannot flood the queue. Internal notes never reach the agent.
- **Transparency.** The tray shows what is sent (user name, PC, text) and never attaches a screenshot or logs
  unless the user chooses to.
- **Licences need software data.** Counting installations only works once agents report installed programs
  (0.1.5-alpha). Matching is by text contained in the program name, optionally filtered by publisher, so the
  matched programs the panel shows while a licence is defined should be checked; exact product identifiers can
  replace text matching later if it proves too loose.

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

- Move the agent to .NET 10 before .NET 8 support ends (10 November 2026).
- mTLS for agents, or pinning the server certificate through the enrollment token for self-signed setups.
- Make the audit log write-protected, not only tamper-evident: a separate database owner role.
- Per-action re-authentication for dangerous panel actions; enforce freshness of signed manifests.
- Authenticode-sign the agent binaries; the tray pipe check then also requires a valid signature.
- Close pentest findings F9 and F11.
- Freeze software (Deep Freeze and similar): thaw before an update and freeze again afterwards. Today: enroll
  before freezing or use `PersistDir` ([`Agent/README.md`](Agent/README.md#machines-with-freeze-software)).
- Remove the legacy `Agent/POpsVision` project and the leftovers that still name it.

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
