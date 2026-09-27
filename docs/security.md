# Security

This page explains the security controls as an operator sees them and lists what to configure. The threat
model, the residual risks and the vulnerability reporting address are in [`../SECURITY.md`](../SECURITY.md).

The agent runs as SYSTEM on every managed PC, so whoever controls the POps server or an admin panel account can
run code on those PCs. Most of the controls below exist to narrow that, to make it visible, and to keep a
trustworthy record of it.

## Panel access

### Sign-in and sessions

- Passwords are stored as bcrypt hashes; login accepts nothing else.
- A successful login returns a JWT (HS256, `JWT_SECRET`, lifetime `JWT_EXPIRE_HOURS`, default 12 h). The panel keeps
  it in the `pops_jwt` cookie (`httpOnly`, `SameSite=Strict`, `Secure` over HTTPS); JavaScript cannot read it.
- State-changing requests authenticated by the cookie must carry `X-Requested-With: XMLHttpRequest` (CSRF check).
- Every request re-checks the user in the database. Deleting a user, or editing them (role, password, name),
  ends their existing sessions immediately. An open `/ws/panel` socket re-checks the session when it sends remote
  input and more than 10 seconds have passed since the last check, and is closed if the session was revoked.
- Login and the 2FA endpoints allow 10 attempts per minute per client address.

### Two-factor authentication

Opt-in TOTP (RFC 6238, any authenticator app), per account, off by default. A user enables it on the **Ayarlar**
page: scan the QR code (generated locally, no external service), then confirm a code; only then is it required.
Turning it off needs a valid code. If an authenticator is lost, a server administrator can reset it:

```sql
UPDATE users SET totp_enabled = false, totp_secret = NULL WHERE username = '<user>';
```

Viewers cannot open **Ayarlar**, and an admin needs the `settings` page permission to reach it; such accounts can
still use the `/api/admin/2fa/*` endpoints directly.

### Roles

| Role | Can |
| --- | --- |
| `viewer` | Read devices, labs, inventory, logs, tasks and packages. Cannot see screen previews or live frames, cannot send remote input, and cannot open the Deployment, Terminal or Settings pages. |
| `admin` | Everything operational: devices and labs, Wake-on-LAN, deployment and commands, remote-control sessions, remote input, previews, quarantine, offline bypass codes, policies. |
| `superadmin` | Additionally: panel users, agent releases and updates, enrollment tokens, agent-auth enforcement, re-enrollment, capability policy, server self-update, audit-chain verification. |

Things to keep in mind:

- **Admins can run commands as SYSTEM on managed PCs** through the task queue (Deployment and Terminal pages).
  Each command is written to the hash-chained audit log, with the user who queued it, when it is sent to the PC.
  On a PC where the terminal capability is disabled the agent refuses them.
- The per-user **page permissions** set on the Settings page only decide which dashboard pages a non-superadmin
  can open. The API authorizes by role alone, so an admin without the `deploy` page can still call the deployment
  endpoints. Use the `viewer` role for read-only accounts.
- The Settings page offers only `admin` and `superadmin` when creating users; create `viewer` accounts through
  `POST /api/admin/users` ([`api.md`](api.md#panel-login-2fa-and-users)).
- The last active superadmin cannot be deleted or demoted, and nobody can delete their own account.

## Agent identity

- **Enrollment.** A superadmin creates an enrollment token on **Sistem & Sürüm** (lab-bound, expiring, one or
  more uses). The MSI stores it in the agent's protected store. On first connect the server consumes one use,
  issues a per-device secret and stores only its SHA-256; the agent keeps the secret in `C:\POpsData\secure`
  (SYSTEM and Administrators only) and presents it on every connection.
- **Enforcement.** Until `enforce_agent_auth` is turned on, agents without credentials are still accepted
  ("accept-both", for rollout). Once it is on, they are rejected (`4401` on WebSockets, `401` on agent HTTP
  endpoints) and the rejection is written to the audit log. Turn it on as soon as every PC is enrolled; the
  Sistem & Sürüm page shows how many are.
- **No takeover by re-enrollment.** A device that already has a secret cannot get a new one with an enrollment
  token (critical audit entry, `4401`) unless a superadmin allows it once (`POST /api/system/allow-reenroll`),
  for example after a reinstall behind freeze software.
- **Binding.** On the agent HTTP endpoints the authenticated device ID must match the device being written;
  otherwise `403`.
- **Transport.** The agent refuses a non-loopback `http://` server and validates the server certificate with the
  Windows defaults, so credentials never travel in clear text. A self-signed certificate must be trusted by the
  PCs.

## Signed agent updates

Releases are signed in CI with an ed25519 key that exists only as a GitHub secret. The server verifies a release
before staging it, and each agent verifies it again with the public key compiled into it, refuses anything not
newer than itself, and downloads the MSI only from its own server, checking size and SHA-256 before anything
changes. A compromised server can therefore not push a modified agent. Details: [`agent.md`](agent.md#updates),
[`keys/README.md`](../keys/README.md).

## Capability policy

A site can switch off remote commands (`execute`) and/or Vision (streaming, previews, remote input) per PC. The
setting lives on the PC (`C:\POpsData\secure\capabilities.json`, set by the MSI with `TERMINAL_ENABLED=0` /
`VISION_ENABLED=0`). The server can only switch a capability **off**
(`POST /api/system/set-capabilities`); a request to switch it on is ignored by the agent. On PCs where these are
off, even a compromised server cannot use them. It takes effect on agents from 0.1.4-alpha.

## Remote control and transparency

- Remote mouse/keyboard input and `execute` sent over the remote-input path need an admin **and** an open
  remote-control session for that device, opened with a recorded reason. A session grant expires after 30
  minutes without activity.
- Live frames go only to the admin who opened the session, never to viewers or other panels.
- On the PC, the user is asked for consent, or, for a mandatory session, sees a full-screen countdown. The agent
  applies remote input only during a session the tray started after that step, even if the server is
  compromised.
- Screen previews update the tray tooltip and show a notice at most every five minutes.
- The tray icon is always visible; there is no hidden mode and no keystroke logging.

See [`vision.md`](vision.md).

## Audit logs

| Log | Written by | Use |
| --- | --- | --- |
| `device_audit_logs` | server only (agents cannot write it) | Security record: enrollment and rejections, remote-control session starts, lockdown/unlock, bypass codes, SYSTEM commands with their requester, update results, releases, enforcement and capability changes. Hash-chained. |
| `agent_logs_v2` | server and agents | Operational event log shown on the Log pages. |
| `enterprise_audit_logs` | server | Remote-control sessions (who, target, reason, mandatory, start/end). |

`GET /api/system/audit-verify` (superadmin) walks the hash chain and reports the first altered or deleted entry.
The chain makes tampering **detectable**; the database role used by the application can still change rows, so
restrict database access.

## What is reachable without a panel login

| Path | Why |
| --- | --- |
| `GET /api/health` | Health check (database state and version only). |
| `POST /api/admin/login`, `/api/admin/login/totp` | Sign-in (rate-limited). |
| `GET /api/agent_policies` | Agents read the policy; it holds no secrets. |
| `/download/<file>` | Deployment packages for agents. Do not upload anything confidential on the Deployment page. |
| `/updates/<file>` | The agent MSI being distributed (verified by agents against the signed manifest). |
| `/ws/agent/…`, `/ws/vision/…`, agent HTTP endpoints | Agent channels; they require agent credentials once enforcement is on. |

## Operator checklist

- [ ] Serve POps only over HTTPS; keep the backend on `127.0.0.1` and port 8000 closed.
- [ ] Keep `.env` at mode `600`; never commit it. Use a random `JWT_SECRET` and `BYPASS_SECRET` (`install.sh` and
      `setup_env.py` generate them).
- [ ] Change the initial admin password; enable 2FA on every admin and superadmin account.
- [ ] Give people the lowest role they need; use `viewer` for read-only access.
- [ ] Enroll every PC, then turn on agent-auth enforcement.
- [ ] Revoke enrollment tokens you no longer need; prefer short lifetimes.
- [ ] Install the MSI with `TERMINAL_ENABLED=0` and/or `VISION_ENABLED=0` on PCs that do not need those features.
- [ ] Restrict who can reach the database; back it up.
- [ ] Check the audit chain from time to time (`/api/system/audit-verify`).
- [ ] Prepare the KVKK notice for the people whose PCs are managed: [`kvkk-aydinlatma.md`](kvkk-aydinlatma.md).

## Reporting a vulnerability

Do not open a public issue. Email **security@pashacore.com.tr** (see [`../SECURITY.md`](../SECURITY.md)).
