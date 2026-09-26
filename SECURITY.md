# Security Policy

## Supported Versions

Currently, POps is in active development. Security updates are applied to the `main` branch and the latest tagged release.

| Version | Supported          |
| ------- | ------------------ |
| 0.1.x   | :white_check_mark: |
| < 0.1   | :x:                |

## Security Architecture

POps runs as SYSTEM/Administrator on endpoints, so it is designed defensively. The current guarantees:

1. **Agent identity & authentication.** A device enrolls with a one-time, lab-bound, expiring **enrollment token** (delivered by the installer). On first connect the server issues a **per-device secret**; the agent stores it in a SYSTEM/Administrators-only store (`C:\POpsData\secure`, inheritance disabled) and presents it as `X-Agent-Secret` on every later connection. The secret is bound to the server-resolved hardware identity, not the URL, and can be revoked from the panel. The server stores **only the SHA-256 of the secret**, so a leaked database cannot be replayed to impersonate an agent — this is the deliberate reason a shared secret (rather than a client keypair / mTLS) is accepted for v1; mTLS is a roadmap hardening, not a correctness fix. For reboot-to-restore (Deep Freeze) machines an optional freeze-excluded mirror keeps the secret across reverts. Authentication runs in **accept-both** mode during rollout and can be **enforced** (`enforce_agent_auth`), after which unauthenticated agent connections — on `/ws/agent`, `/ws/vision`, and the agent HTTP endpoints — are rejected (WebSocket close `4401` / HTTP `401`) and the rejection is written to a log agents cannot forge.
2. **Transport.** The agent refuses a non-TLS (`http://`/`ws://`) server address unless it is loopback; production must be `https://`/`wss://`. Credentials are never sent over an insecure transport. The agent validates the server's TLS certificate with the platform's default chain and hostname checks (there is **no** accept-all bypass), so a same-LAN attacker cannot present a rogue certificate for the server's hostname. For self-hosted sites that use a **self-signed** certificate, certificate **pinning via the enrollment token** (embedding the server certificate fingerprint the agent then pins) is a planned hardening.
3. **Signed updates (supply chain).** Releases are signed with **ed25519**. The private key lives only in CI / GitHub Secrets — never on the server or on agents. Each agent has the **public key compiled in** and, before stopping any process or touching a file, verifies the signed `manifest.json` and the package's SHA-256, refuses any version not strictly newer than its own (downgrade protection), and downloads the package only from its own server. Rollback is itself a signed Windows Installer transaction. An admin-uploaded package is verified against its signed manifest server-side before it can be dispatched.
4. **Dashboard access.** JWT in an httpOnly cookie, CSRF protection on state-changing requests, and Role-Based Access Control (`superadmin` / `admin` / `viewer`). User administration is restricted to `superadmin`.
5. **Endpoint hardening.** Secrets are readable only by SYSTEM/Administrators (no `Users:Read`, so students cannot read `BypassSecret`); the identity file's ACL is reset on every start; the offline unlock code has attempt lockout with exponential backoff and constant-time comparison; the tray IPC bounds message sizes.
6. **Transparency.** A `POpsVision` (remote screen) session asks for consent, or, for a mandatory admin session with a written reason, shows a countdown and a notice. Screen previews update the tray tooltip and notify at most every five minutes. The tray is always visible; there is no hidden/stealth mode; there is no keystroke logging.

## Threat Model

| # | Threat | Attack | Mitigation | Residual risk |
|---|--------|--------|------------|---------------|
| 1 | Rogue device impersonation | Connect to `/ws/agent/{pc}` claiming to be another PC to receive its commands / send fake data | Enrollment token → per-device secret; `enforce_agent_auth` rejects unauthenticated with `4401` | While enforcement is off (rollout default), unauthenticated agents are accepted **and logged**; enable enforcement once the fleet is enrolled |
| 2 | Network MITM | ARP-spoof a flat lab LAN to capture the secret or inject commands | Agent refuses non-loopback `http://`; TLS (`wss://`) required end-to-end | Operator must front the server with TLS (the agent enforces this) |
| 3 | Malicious / forged update | Push a trojaned agent build | ed25519-signed manifest, verify-before-execute against a compiled-in key, downgrade guard, signed rollback | Compromise of the release key itself; mitigated by keeping it offline / CI-only, never on the server |
| 4 | **Server / panel compromise** | Attacker controls the server and sends commands to agents | Updates **cannot** be forged (signature is verified independently of the server). | **Biggest residual.** Non-update commands (remote terminal `execute`, Vision, lockdown) are trusted from the server, so a compromised server ≈ **SYSTEM code execution on every managed PC**. Planned mitigation: an agent-side **capability policy** (terminal/Vision disabled at install, re-enabled only by an offline-signed policy) so a compromised server cannot run code where it is disabled. Until then, the panel is the crown jewel — protect it hardest. |
| 5 | Malicious local user (student) | Read the bypass secret, brute-force the unlock code, tamper with identity, or crash the agent | Secrets SYSTEM-only; unlock-code lockout + constant-time compare; identity ACL reset on start; bounded IPC | — |
| 6 | Stolen panel credentials | Reuse an admin's password | JWT + RBAC + CSRF | No 2FA and no re-authentication on dangerous actions **yet** (planned) |
| 7 | Audit tampering | Alter or delete logs to hide activity | Auth/update rejections are written to a table agents cannot write | Audit is **not yet tamper-evident** (no hash chain) and the app's DB user can still `UPDATE`/`DELETE` it (planned: append-only `audit_events` with restricted grants) |
| 8 | Replay of an old signed update | Re-send a genuine but outdated signed manifest | Agent rejects any version ≤ installed | Freshness/`released_at` is not enforced; the authenticated TLS dispatch channel must prevent replay |

### If the server is fully compromised, what can an attacker do?

Honest answer: they **cannot** silently push a malicious agent update — every update is verified against a key the server does not hold, and downgrades are refused. They **can**, however, issue any command the agent currently trusts from the server: run PowerShell as SYSTEM on any PC (remote terminal), start a Vision session, lock a machine, or read inventory. In other words, a compromised panel is close to SYSTEM-level remote code execution across the fleet. The planned defense is the agent-side capability policy above (an offline-signed switch that lets a site hard-disable terminal/Vision so even a compromised server cannot use them). Treat the server and panel accounts as the highest-value target; this is the top item on the security roadmap.

## Reporting a Vulnerability

If you discover a security vulnerability within POps, please **DO NOT** open a public issue. 

Instead, please send an email to **security@pashacore.com.tr**. We will triage your report within 48 hours and work with you to understand and mitigate the issue before public disclosure.

When reporting, please include:
* A description of the vulnerability.
* Steps to reproduce the issue.
* Potential impact.

Thank you for helping keep the POps platform secure!
