# Getting Started

POps (Pasha Operations Platform) manages the Windows PCs of computer labs and similar fleets from one web panel:
inventory, remote commands (also on a schedule) and software deployment, screen view and remote control with the
user's consent, quarantine, Wake-on-LAN, signed agent updates, reports, and notifications by e-mail or webhook.
Installed software and Windows Update status are reported by agents from 0.1.5-alpha on. It is designed to be transparent to the people using the PCs:
remote sessions ask for consent or show a notice with the reason, and security-relevant actions are recorded.

POps is alpha software. Read [`../SECURITY.md`](../SECURITY.md) before you run it on real machines.

## What you need

| Part | Requirement |
| --- | --- |
| Server | Linux with systemd (tested: AlmaLinux/RHEL/Rocky, Debian/Ubuntu), PostgreSQL, Python 3.10+ (3.12 recommended), PHP 8 with `curl`, nginx or Apache, a host name with a TLS certificate. |
| Managed PCs | Windows 10 or 11, 64-bit. No prerequisites: the .NET runtime comes with the agent. See *Supported systems* below. |
| Network | Each PC opens outbound connections to the server on port 443 (HTTPS) and keeps two WebSocket connections open (commands and Vision). Proxies and firewalls must allow the WebSocket upgrade (`Upgrade: websocket`) and long-lived connections; TLS inspection must either be off for the POps host or use a CA the agents trust. There is no HTTP polling fallback. Wake-on-LAN needs UDP broadcasts from the server to the lab subnet. |
| Administrators | A browser. From 0.1.22 the panel loads nothing from third parties; 0.1.21 and earlier load fonts and icons from public CDNs. |

### Supported systems

| Tested | Not tested (may work, no promise) |
| --- | --- |
| Windows 10 and 11, x64, one signed-in user at the console | Remote Desktop sessions and PCs with several users signed in at once (the tray assumes one console session) |
| Standard user and administrator accounts | The UAC prompt ("secure desktop"): remote control cannot see or click it |
| One monitor, 100 % scaling | Several monitors and display scaling other than 100 % (remote control coordinates) |
| Turkish and English keyboard layouts (0.1.14) | Other layouts and input methods |

Windows on ARM, 32-bit Windows, Windows Server as a managed PC, macOS and Linux PCs are not supported. Freeze
software (Deep Freeze, Shadow Defender) needs care: see [`Agent/README.md`](../Agent/README.md#machines-with-freeze-software).

## How it fits together

```
Browser ──https──► web server ──► backend (FastAPI) ──► PostgreSQL
                        ▲
Windows PC (POpsAgent) ─┘  wss: permanent outbound connection
```

Each PC runs the POps agent: a Windows service that keeps a connection to the server, a tray application the user
sees, a watchdog and an updater. The server has a FastAPI backend, a PostgreSQL database and a PHP panel. Details:
[`architecture.md`](architecture.md).

## Path to a working lab

To look around first, use the public read-only demo at [demo.pashacore.com.tr](https://demo.pashacore.com.tr)
(user `demo`, password `demo`): a made-up school with 50 PCs, reset every night.

1. **Install the server:** [`installation.md`](installation.md) (one script), or with Docker Compose:
   [`docker.md`](docker.md).
2. **Put the web server and TLS in front of it:** [`deployment.md`](deployment.md).
3. **Enroll the first PCs:** [`quick-start.md`](quick-start.md) walks through the whole sequence, from the first
   sign-in to running a command on a PC.
4. **Learn the panel:** [`dashboard.md`](dashboard.md).
5. **Harden it:** turn on agent-auth enforcement and 2FA, and review [`security.md`](security.md).

## Terms used in these docs

| Term | Meaning |
| --- | --- |
| Hardware ID (`hw_id`, `HW-…`) | The device's identity, stored on the PC in `C:\POpsData\identity.key` and used as its key on the server. Not the Windows host name. |
| Lab | A group of PCs (a classroom). New PCs land in `Atanmamis_Cihazlar` (unassigned) unless their enrollment token names a lab. |
| Enrollment token | A token created on **Sistem** and given to the MSI; it lets a PC obtain its device secret. Can be bound to a lab and used by several PCs. |
| Device secret | Per-PC credential issued at enrollment. The server stores only its hash. |
| Enforcement (`enforce_agent_auth`) | When on, PCs without a valid secret or token are rejected. Off by default so a fleet can be enrolled first. |
| Capability policy | Per-PC switch that disables remote commands and/or screen view on the PC itself. The server can switch them off, never on. |
| Remote-control session | A recorded, reasoned session that allows an admin to see a PC's screen live and control it. |
| Quarantine | Full-screen lock on the PC plus firewall isolation that leaves only the POps server, DNS and DHCP reachable. |
| Staged release | A signed agent release the server has verified and is ready to send to agents. |

## Documentation map

| Topic | Page |
| --- | --- |
| Install the server | [`installation.md`](installation.md) |
| Run the server with Docker (optional) | [`docker.md`](docker.md) |
| First steps end to end | [`quick-start.md`](quick-start.md) |
| Production setup, updates, releases | [`deployment.md`](deployment.md) |
| All settings | [`configuration.md`](configuration.md) |
| Components and data flows | [`architecture.md`](architecture.md) |
| Web panel | [`dashboard.md`](dashboard.md) |
| Windows agent | [`agent.md`](agent.md) |
| Screen view and remote control | [`vision.md`](vision.md) |
| Backend code layout | [`backend.md`](backend.md) |
| REST and WebSocket API | [`api.md`](api.md); OpenAPI file for `/api/v1`: [`openapi.json`](openapi.json) |
| Agent protocol (JSON Schema, test vectors) | [`protocol/README.md`](protocol/README.md) |
| Panel languages (Turkish, English) | [`i18n.md`](i18n.md) |
| Tests and how to run them | [`testing.md`](testing.md) |
| Public read-only demo (setup) | [`../deploy/demo/README.md`](../deploy/demo/README.md) |
| Database and migrations | [`database.md`](database.md) |
| Security controls | [`security.md`](security.md) |
| Server self-update from the panel | [`self-update.md`](self-update.md) |
| Problems and logs | [`troubleshooting.md`](troubleshooting.md) |
| Common questions | [`faq.md`](faq.md) |
| POps compared with classroom tools | [`positioning.md`](positioning.md) |
| KVKK notice template (Turkish) | [`kvkk-aydinlatma.md`](kvkk-aydinlatma.md) |
| Turkish guides for schools: why POps, POps with Veyon, pilot setup, case study template | [`tr/neden-pops.md`](tr/neden-pops.md), [`tr/veyon-ile-birlikte.md`](tr/veyon-ile-birlikte.md), [`tr/pilot-okul.md`](tr/pilot-okul.md), [`tr/vaka-calismasi-sablonu.md`](tr/vaka-calismasi-sablonu.md) |
| Code signing policy | [`code-signing.md`](code-signing.md) |
| GLPI export (design, not implemented) | [`integrations/glpi.md`](integrations/glpi.md) |
