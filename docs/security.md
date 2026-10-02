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
  ends their existing sessions immediately. An open `/ws/panel` socket re-checks the session every 10 seconds and
  is closed if the session was revoked, together with its screen and control grants.
- Login and the 2FA endpoints allow 10 attempts per minute per client address.

### Two-factor authentication

Opt-in TOTP (RFC 6238, any authenticator app), per account, off by default and **recommended for every admin and
superadmin**. A user enables it on the **Ayarlar** page: scan the QR code (generated locally, no external service),
then confirm a code; only then is it required. Turning it off needs a valid code. It stays optional, but the
panel recommends it: an admin or superadmin whose own 2FA is off sees a notice on **Ayarlar** and **Sistem &
Sürüm** (read from `GET /api/admin/2fa/status`), which can be hidden for 7 days per browser.

- **A code works once.** The server stores the time step of the last accepted code per account; that code, and
  any older one, is refused even while it is still within its ±30-second window.
- **The second step belongs to the session.** The short-lived challenge issued after the password carries the
  account's `token_version`; if sessions are revoked, the password is changed or the role edited before the code is
  entered, the challenge is refused.
- **Secrets are encrypted in the database** (Fernet, `v1:` prefix). The key is `TOTP_ENCRYPTION_KEY` from `.env`
  (generate with `python3 -c "from cryptography.fernet import Fernet; print(Fernet.generate_key().decode())"`) or,
  if it is not set, a key derived from `JWT_SECRET`. Decryption tries both, and at startup every secret is
  re-encrypted with the current key (plain-text secrets from older versions included). **Before changing
  `JWT_SECRET`, set `TOTP_ENCRYPTION_KEY` and restart once**; otherwise the existing 2FA secrets can no longer be
  read and those users must have 2FA reset. The per-device offline bypass keys are encrypted the same way (from
  0.1.14); if one can no longer be read, the panel says so instead of showing a code, and the device is sent a new
  key the next time it connects.
- **What the encryption covers:** a leaked database dump or SQL access alone. A full server backup holds both the
  database and `.env` (with the key), so it must be protected like the server itself: root-only, encrypted when it
  leaves the machine.

If an authenticator is lost, a server administrator can reset it:

```sql
UPDATE users SET totp_enabled = false, totp_secret = NULL WHERE username = '<user>';
```

Viewers cannot open **Ayarlar**, and an admin needs the `settings` page permission to reach it; such accounts can
still use the `/api/admin/2fa/*` endpoints directly.

### Panel output (XSS)

Everything the panel shows that came from a PC or a person (hostnames, lab names, window titles, software,
command output, tickets, logs, error messages) is written with `textContent` or escaped first: `escapeHtml()` for
text and quoted attributes, `jsArg()` for arguments of inline `onclick` handlers (HTML escaping alone is not
enough there), `encodeURIComponent()` for URL parts. PHP output uses `htmlspecialchars`, or `json_encode` with
`JSON_HEX_TAG` inside scripts.

The `Dashboard checks` CI job runs `tools/html_sinks/check_html_sinks.py` (and its self-tests), which fails on any
`innerHTML`/`outerHTML`/`insertAdjacentHTML`/`document.write` or PHP `echo` that writes an unescaped value, and on
any HTML template or string concatenation built from one. To run it locally:
`python3 tools/html_sinks/check_html_sinks.py`. When it reports a value that is safe (a number counted on the
page, a class name from a fixed table), add a line to `tools/html_sinks/html_sinks_allowlist.txt`: the file, the
key the checker printed for that line, and a one-line reason. Keys follow the line's text, not its number, so
unrelated edits do not break them; changing the line itself requires a new review. Prefer escaping over adding
an entry.

### Roles

| Role | Can |
| --- | --- |
| `viewer` | Read devices, labs, inventory, software, Windows Update state, licences, logs, tasks, packages and reports (including CSV exports). Cannot see screen previews or live frames, cannot send remote input, and cannot open the Deployment, Terminal or Settings pages. |
| `admin` | Everything operational: devices and labs, Wake-on-LAN, deployment and commands, scheduled tasks, Windows Update scan and install, licence definitions, helpdesk tickets, remote-control sessions, remote input, previews, quarantine, offline bypass codes, policies, the notification bell. |
| `superadmin` | Additionally: panel users, agent releases and updates, enrollment tokens, agent-auth enforcement, re-enrollment, capability policy, server self-update, audit-chain verification, notification settings. |

Things to keep in mind:

- **Admins can run commands as SYSTEM on managed PCs** through the task queue (Deployment and Terminal pages,
  the Vision diagnostics dialog and scheduled tasks). Each command is written to the hash-chained audit log, with
  the user who queued it, when it is sent to the PC. On a PC where the terminal capability is disabled the agent
  refuses them.
- **Scheduled tasks** use the same queue: when due, the server queues the command as normal tasks, so the
  concurrency limit, the capability policy and the per-command audit entry apply. Creating, pausing, resuming,
  running and deleting a schedule is also written to the audit log with the user, and each automatic run is
  recorded. A queued run keeps the schedule's creator (or the user who pressed **Şimdi**) as requester.
- The per-user **page permissions** set on the Settings page only decide which dashboard pages a non-superadmin
  can open. The API authorizes by role alone, so an admin without the `deploy` page can still call the deployment
  endpoints. Use the `viewer` role for read-only accounts.
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
- **The key stays on its hardware (agent 0.1.15-alpha on).** The agent records the hardware it received its key
  on (`secure\hw.bind`). A copy of the installation on other hardware moves the ID and key aside at start
  (`secure\clone-<time>\`, event 1070) and enrolls as a new device; `POpsAgent.exe --generalize` prepares a disk
  image. See [`agent.md`](agent.md#connection).
- **Newer agent endpoints are enrolled-only.** The software inventory, Windows Update and helpdesk endpoints
  (`POST /api/software/{hw_id}`, `POST /api/patches/{hw_id}`, `/api/tickets/agent/{hw_id}`) require a valid device
  secret even while enforcement is off (`401` otherwise, `403` for another device). The accept-both exception for
  older agents does not apply there.
- **State changes need an enrolled agent.** Events on the older, accept-both endpoints can change something
  beyond the event log only when they come from an enrolled agent: `agent.auto_quarantine` and
  `agent.offline_bypass` on `POST /api/logs/{hw_id}` set or clear the device's quarantine flag (and are audited and
  notified), and `/api/policy_alert` raises a notification. From a client without a valid secret they are only
  logged.
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

Both are on by default so that a fresh install works for a lab. **Turn them off where they are not needed**: on
teachers' and administration PCs a remote terminal and screen view are rarely necessary, and a PC with both off
offers nothing to someone who takes over the server or an admin account.

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

## Notifications

Notifications appear under the bell in the panel (admins and superadmins) and can also be sent out by e-mail
and/or a webhook.

**Which events notify.** Only events the server itself decides on:

| Event | Severity |
| --- | --- |
| Attempt to take over an enrolled device with an enrollment token (`enroll_denied`) | critical |
| Agent update failed (`rollback_failed`, `error`, `rejected`, …, or the agent is no longer managed) | critical |
| Agent update rolled back | high |
| Update sent, but no result from the agent after 20 minutes | high |
| DNS policy violation reported by an enrolled agent | high |
| Device quarantined by an admin | high |
| Enrolled agent quarantined itself at the DNS violation threshold | high |
| Scheduled task could not be queued | high |
| Licence over its seats, or expired (checked once a day) | high |
| Update did not start (machine unchanged), or waits for a restart | medium |
| Agent refused a disabled capability | medium |
| Quarantine lifted on the PC with an offline bypass code (enrolled agent) | medium |
| New helpdesk ticket from an agent | medium |
| Licence ends within 30 days (checked once a day) | medium |
| Agent updated successfully | info |

The `risk_level` an agent writes with `POST /api/logs` never creates a notification, so a device that is not
enrolled cannot flood administrators with fake "critical" alerts. DNS policy alerts notify only when they come
from an enrolled agent with a valid secret: while enforcement is off `/api/policy_alert` still records alerts from
agents without credentials in the event log, but those never notify. The notification title is per category (the
domain is in the detail), so one device raises at most one notification per category every 10 minutes.

**Limits.** An identical notification (same event, device and title) is recorded at most once per 10 minutes.
At most 30 notifications are sent out per 10 minutes; the rest are still shown under the bell. Sending runs in
the background with a 10-second timeout and never blocks or breaks the event that caused it.

**Settings.** Only a superadmin can change where notifications go. The webhook address must be `http://` or
`https://` (use `https://`; the body contains device names and event details). SMTP host, user and password are
read only from the backend `.env` and are never stored in the database or shown in the panel. Changes to the
notification settings are written to the audit log.

**Webhook target guard (SSRF).** The webhook host is resolved, and **every** address it resolves to must be a
public internet address; loopback, private ranges, link-local (including the cloud metadata address
`169.254.169.254`), CGNAT, reserved, multicast and unspecified addresses are refused. The check runs when the
settings are saved or tested and again before each delivery. The connection is then made to the checked address
(so a DNS answer that changes in between does not help), TLS is still verified against the host name, and
redirects are not followed. A school that wants to post to a system inside its own network opts in with
`NOTIFY_WEBHOOK_ALLOW_PRIVATE=1` in `.env`; multicast and unspecified addresses stay refused even then.

## Helpdesk

- Agents can open tickets and read their own device's tickets only with a valid device secret (see above).
- Per device at most 5 tickets may be open (open, in progress or waiting) and at most 10 may be opened per hour;
  beyond that the agent gets `429`.
- Ticket text is stored as plain text and escaped in the panel. Internal notes (and the automatic notes that record
  status, priority and assignee changes) are shown only in the panel; the agent endpoint never returns them.
- The panel endpoints require the `admin` role.

## Licences

Licence patterns are plain text matched with a case-insensitive "contains" against installed program names (and
optionally the publisher). `%`, `_` and `\` are rejected in the pattern and the publisher filter, so a definition
cannot use SQL wildcards to match everything. Creating, changing and deleting licences is written to the audit
log; reading them is open to every signed-in role.

## Reports and CSV export

The report and export endpoints are readable by every signed-in role (the **Raporlar** page itself needs the
`reports` page permission). CSV exports contain values reported by agents (program names, event
texts), so cells that start with `=`, `+`, `-`, `@`, a tab or a carriage return are prefixed with `'`; spreadsheet
programs then show them as text instead of running them as formulas.

## Audit logs

| Log | Written by | Use |
| --- | --- | --- |
| `device_audit_logs` | server only (agents cannot write it) | Security record: enrollment and rejections, remote-control session starts, lockdown/unlock, bypass codes, SYSTEM commands with their requester, scheduled-task changes and runs, Windows Update scan/install requests, licence changes, agent self-quarantine and offline-bypass events, update results, releases, enforcement and capability changes, auto-enrollment and notification settings. Hash-chained. |
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
| `/download/<file>?sig=…` | Deployment packages for agents, only with the signed link returned at upload (wrong or missing signature: 404). Anyone who has a package's link can still download it, so do not upload anything confidential on the Deployment page. |
| `/updates/<file>` | The agent MSI being distributed (verified by agents against the signed manifest). |
| `/ws/agent/…`, `/ws/vision/…`, agent HTTP endpoints | Agent channels; they require agent credentials once enforcement is on. The software, Windows Update and helpdesk endpoints always require them. |

## Operator checklist

- [ ] Serve POps only over HTTPS; keep the backend on `127.0.0.1` and port 8000 closed.
- [ ] Keep `.env` at mode `600`; never commit it. Use a random `JWT_SECRET` and `BYPASS_SECRET` (`install.sh` and
      `setup_env.py` generate them).
- [ ] Change the initial admin password; enable 2FA on every admin and superadmin account (recommended; the panel
      reminds accounts that have not).
- [ ] Give people the lowest role they need; use `viewer` for read-only access.
- [ ] Enroll every PC, then turn on agent-auth enforcement.
- [ ] Revoke enrollment tokens you no longer need; prefer short lifetimes.
- [ ] Install the MSI with `TERMINAL_ENABLED=0` and/or `VISION_ENABLED=0` on PCs that do not need those features.
- [ ] Restrict who can reach the database; back it up.
- [ ] Check the audit chain from time to time (`/api/system/audit-verify`).
- [ ] Send notifications out (e-mail or an `https://` webhook) so takeover attempts and failed updates reach you.
- [ ] Prepare the KVKK notice for the people whose PCs are managed: [`kvkk-aydinlatma.md`](kvkk-aydinlatma.md).

## Reporting a vulnerability

Do not open a public issue. Email **security@pashacore.com.tr** (see [`../SECURITY.md`](../SECURITY.md)).
