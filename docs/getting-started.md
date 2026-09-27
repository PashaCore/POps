# Getting Started

POps (Pasha Operations Platform) manages the Windows PCs of computer labs and similar fleets from one web panel:
inventory, remote commands and software deployment, screen view and remote control with the user's consent,
quarantine, Wake-on-LAN, and signed agent updates. It is designed to be transparent to the people using the PCs:
remote sessions ask for consent or show a notice with the reason, and security-relevant actions are recorded.

POps is alpha software. Read [`../SECURITY.md`](../SECURITY.md) before you run it on real machines.

## What you need

| Part | Requirement |
| --- | --- |
| Server | Linux with systemd (tested: AlmaLinux/RHEL/Rocky, Debian/Ubuntu), PostgreSQL, Python 3.9+, PHP 8 with `curl`, nginx or Apache, a host name with a TLS certificate. |
| Managed PCs | 64-bit Windows with the .NET 8 Desktop Runtime (x64), and HTTPS access to the server. |
| Administrators | A browser. The panel loads its charts, icons and fonts from public CDNs. |

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

1. **Install the server:** [`installation.md`](installation.md) (one script).
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
| Enrollment token | A token created on **Sistem & Sürüm** and given to the MSI; it lets a PC obtain its device secret. Can be bound to a lab and used by several PCs. |
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
| First steps end to end | [`quick-start.md`](quick-start.md) |
| Production setup, updates, releases | [`deployment.md`](deployment.md) |
| All settings | [`configuration.md`](configuration.md) |
| Components and data flows | [`architecture.md`](architecture.md) |
| Web panel | [`dashboard.md`](dashboard.md) |
| Windows agent | [`agent.md`](agent.md) |
| Screen view and remote control | [`vision.md`](vision.md) |
| Backend code layout | [`backend.md`](backend.md) |
| REST and WebSocket API | [`api.md`](api.md) |
| Database and migrations | [`database.md`](database.md) |
| Security controls | [`security.md`](security.md) |
| Server self-update from the panel | [`self-update.md`](self-update.md) |
| Problems and logs | [`troubleshooting.md`](troubleshooting.md) |
| Common questions | [`faq.md`](faq.md) |
| POps compared with classroom tools | [`positioning.md`](positioning.md) |
| KVKK notice template (Turkish) | [`kvkk-aydinlatma.md`](kvkk-aydinlatma.md) |
