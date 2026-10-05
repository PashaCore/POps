# Security

This page explains the security controls as an operator sees them and lists what to configure. The threat
model, the residual risks and the vulnerability reporting address are in [`../SECURITY.md`](../SECURITY.md).

The agent runs as SYSTEM on every managed PC, so whoever controls the POps server or an admin panel account can
run code on those PCs. Most of the controls below exist to narrow that, to make it visible, and to keep a
trustworthy record of it.

## Panel access

### Sign-in and sessions

- Passwords are stored as bcrypt hashes; login accepts nothing else. Accounts can also come from a directory
  (LDAP / Active Directory) or an OpenID Connect provider; see
  [Directory and single sign-on](#directory-and-single-sign-on).
- A successful login returns a JWT (HS256, `JWT_SECRET`, lifetime `JWT_EXPIRE_HOURS`, default 12 h). The panel keeps
  it in the `pops_jwt` cookie (`httpOnly`, `SameSite=Strict`, `Secure` over HTTPS); JavaScript cannot read it.
- State-changing requests authenticated by the cookie must carry `X-Requested-With: XMLHttpRequest` (CSRF check).
- Every request re-checks the user in the database. Deleting a user, or editing them (role, password, name),
  ends their existing sessions immediately. An open `/ws/panel` socket re-checks the session every 10 seconds and
  is closed if the session was revoked, together with its screen and control grants.
- Login and the 2FA endpoints allow 10 attempts per minute per client address (`/api/v1/...` shares the same
  limit). The OpenID Connect start and callback allow 20 per minute, the ticket redemption 10.

### API tokens

Automation uses API tokens instead of a person's login ([`api.md`](api.md#api-tokens-automation), decision D-21):

- Only a superadmin creates, lists and revokes them (**Ayarlar → Güvenlik → API jetonları**); both are written to
  the hash-chained audit log. A token cannot manage tokens or users.
- `pops_` + 32 random bytes, shown once; the database keeps only its SHA-256 hash and an 8-character prefix.
- Role `viewer` (`GET` only) or `admin`; never `superadmin`. Tokens are refused on every superadmin endpoint, on the
  user list and 2FA, and on remote control and screen previews, which need a person's panel session.
- Accepted only in the `Authorization: Bearer` header (not as the cookie), so no CSRF header is needed; cookie
  sessions keep the `X-Requested-With` check.
- Expiry and revocation take effect on the next request. Everything a token does is recorded as `token:<name>`.
- An `admin` token can queue commands that run as SYSTEM on managed PCs: store it like an admin password, give it a
  validity, and revoke it when the script that uses it is retired.

### Two-factor authentication

Opt-in TOTP (RFC 6238, any authenticator app), per account, off by default and **recommended for every admin and
superadmin**. A user enables it on the **Ayarlar** page: scan the QR code (generated locally, no external service),
then confirm a code; only then is it required. Turning it off needs a valid code. It stays optional, but the
panel recommends it: an admin or superadmin whose own 2FA is off sees a notice on **Ayarlar** and **Sistem**
(read from `GET /api/admin/2fa/status`), which can be hidden for 7 days per browser.

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

### Directory and single sign-on

A superadmin can let people sign in with the school's directory accounts or a single sign-on provider
(**Ayarlar → Güvenlik → Kimlik sağlayıcıları**; settings in [`configuration.md`](configuration.md#identity-providers),
decision D-24). What protects what:

- **Local accounts stay in charge.** A user name that exists as a local account is only ever checked against its
  bcrypt hash; it never reaches the directory. The first local superadmin cannot be converted to a directory or OIDC
  account and the last local superadmin must stay local, so a directory or provider outage does not lock everyone
  out: directory accounts then get `503`, local accounts sign in as usual. A name that matches a local account even
  with different letter case is not sent to the directory either, so a local password never leaves the server. A
  directory or OIDC account whose name matches an existing local account is refused instead of being linked, so a
  directory entry called `admin` cannot take over the local `admin`. When a directory sign-in fails, the server
  spends the same bcrypt time as for a local account, so response times do not tell which local names exist.
- **No local password.** Directory and OIDC accounts are stored with `auth_source` `ldap` or `oidc`, an
  `external_id` (directory GUID/UUID or DN, OIDC `iss|sub`) and no usable password hash. OIDC accounts cannot use the
  password form; directory accounts use it, but their password is checked by the directory.
- **Roles come from groups.** Directory group DNs (or OIDC group claim values) map to `viewer`, `admin` or
  `superadmin` and to page permissions; the highest matching role wins. Role and pages are rewritten at every
  sign-in and a change ends the account's other sessions. An account in no mapped group cannot sign in. A default role
  for e-mail domains (OIDC) can be `viewer` or `admin`, never `superadmin`, and needs `email_verified`. When the OIDC
  user name is the e-mail address, it is accepted only with `email_verified: true`, so an unverified address cannot
  take another person's name or a superadmin's prepared account.
- **Disabled or removed accounts** (AD `userAccountControl` ACCOUNTDISABLE, `nsAccountLock`, OpenLDAP
  `pwdAccountLockedTime`, or no longer found) are refused, and their open panel sessions end at that attempt. Sessions
  are only ended after the directory confirmed the password (disabled, no mapped group) or confirmed that the linked
  account no longer exists (searched by its GUID/UUID), so typing someone's name cannot log them out. A failed
  directory search (permissions, referral, time limit) is reported as `503`, not as a wrong password. A session that
  is never used for a new sign-in lasts until it expires (`JWT_EXPIRE_HOURS`); delete the panel account to end it at
  once. Switching a provider off ends the sessions of all its accounts.
- **LDAP transport.** LDAPS or StartTLS only, TLS 1.2 or newer, with the certificate chain and the host name always
  verified against the system CA store or the pasted CA certificate. Plain LDAP is refused (an
  `allow_insecure_for_tests` switch exists for automated tests; it is not offered in the panel and is refused unless
  the backend itself runs with `POPS_SSO_ALLOW_INSECURE_FOR_TESTS=1`, so a stolen superadmin session cannot turn
  encryption off). The user name is
  escaped before it goes into the search filter, an empty password is refused before any bind (an empty simple bind
  is an anonymous bind), and referrals are not followed.
- **OIDC checks.** Authorization code flow with PKCE (S256), `state` and `nonce`. The ID token signature is verified
  against the provider's JWKS with RS/PS/ES/EdDSA algorithms only (`HS*` and `none` are refused); `iss` must equal the
  discovery document's issuer, `aud` the client ID (with several audiences, `azp` too), and `exp`, `iat` and `nonce`
  are checked. Provider endpoints must be https; redirects from the provider's endpoints are not followed and
  responses are capped at 1 MB.
- **No login CSRF, no open redirect.** The state is single-use, expires after 10 minutes and must match the
  `pops_oidc_state` cookie (`HttpOnly`, `SameSite=Lax`, path `/api/auth/oidc`, `Secure` with an https redirect URI).
  The callback does not set the session itself: it sends the browser back to the panel's `/login` with a one-time
  ticket valid for 60 seconds, which PHP redeems together with a random binding it stored in that browser's PHP
  session before the flow started. All redirect targets are derived from the configured redirect URI, never from
  the request; the optional return path (`next`) must be a local panel path and anything else is refused with `400`.
- **2FA still applies.** If the panel account has TOTP enabled, both a directory login and an OIDC login ask for the
  code before a session is issued.
- **Secrets.** The LDAP service-account password and the OIDC client secret are encrypted at rest with the same key
  as the 2FA secrets (`v1:` prefix; re-encrypted at startup like them), never returned by the API and never logged.
  When the server, port, connection type, service account, CA certificate, issuer or client ID changes, the stored
  secret is not reused until it is typed again, so a stolen superadmin session cannot send it to another server, to
  a server with another CA's certificate or over a weaker connection. **Bağlantıyı sına** follows the same
  rule.
- **Audit.** Every settings change (changed field names and values, a hash for the CA certificate, whether the secret
  changed, who), every account created by a first sign-in, every role change caused by a group change and every
  change of an account's identity source is written to the hash-chained audit log. Tokens, codes and passwords are
  never logged.
- **What to watch:** a directory group mapped to `superadmin` gives its members full control of every managed PC;
  map it to a small, dedicated group. Use a read-only service account. Wrong passwords typed on the panel count
  towards the directory's lockout policy like any other failed logon; the panel's limit of 10 attempts per minute
  per address slows guessing but does not stop someone from locking a known account out. "Bağlantıyı sına" can make the server open
  connections to the host a superadmin types (as the webhook can), so only superadmins can use it and it is
  rate-limited.

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
| `viewer` | Read devices, labs, inventory, software, Windows Update state, licences, logs, tasks, packages and reports (including CSV exports). Cannot see screen previews or live frames, cannot send remote input, and cannot open the **Dağıtım**, **Uzak komut** or **Ayarlar** pages. |
| `admin` | Everything operational: devices and labs, Wake-on-LAN, deployment and commands, scheduled tasks, Windows Update scan and install, licence definitions, helpdesk tickets, remote-control sessions, remote input, previews, quarantine, offline bypass codes, policies, the notification list (**Bildirimler**), sending files to PCs and fetching files from them (panel session only). |
| `superadmin` | Additionally: fetching files from other users' profiles on a PC, panel users, API tokens, agent releases and updates, enrollment tokens, agent-auth enforcement, re-enrollment, capability policy, server self-update, audit-chain verification, notification settings. |

Things to keep in mind:

- **Admins can run commands as SYSTEM on managed PCs** through the task queue (**Dağıtım** and **Uzak komut**,
  the **Uzak ekran** diagnostics dialog, the PC actions on **Cihazlar** and **Sınıflar**, and scheduled tasks). Each command is written to the hash-chained audit log, with
  the user who queued it, when it is sent to the PC. On a PC where the terminal capability is disabled the agent
  refuses them.
- **Scheduled tasks** use the same queue: when due, the server queues the command as normal tasks, so the
  concurrency limit, the capability policy and the per-command audit entry apply. Creating, pausing, resuming,
  running and deleting a schedule is also written to the audit log with the user, and each automatic run is
  recorded. A queued run keeps the schedule's creator (or the user who pressed **Şimdi**) as requester.
- The per-user **page permissions** set on the **Ayarlar** page only decide which dashboard pages a non-superadmin
  can open. The API authorizes by role alone, so an admin without the `deploy` page can still call the deployment
  endpoints. Use the `viewer` role for read-only accounts.
- The last active superadmin cannot be deleted or demoted, and nobody can delete their own account.

## Agent identity

- **Enrollment.** A superadmin creates an enrollment token on **Sistem** (lab-bound, expiring, one or
  more uses). The MSI stores it in the agent's protected store. On first connect the server consumes one use,
  issues a per-device secret and stores only its SHA-256; the agent keeps the secret in `C:\POpsData\secure`
  (SYSTEM and Administrators only) and presents it on every connection.
- **Enforcement.** Until `enforce_agent_auth` is turned on, agents without credentials are still accepted
  ("accept-both", for rollout). Once it is on, they are rejected (`4401` on WebSockets, `401` on agent HTTP
  endpoints) and the rejection is written to the audit log. Turn it on as soon as every PC is enrolled; the
  **Sistem** page shows how many are.
- **No takeover by re-enrollment.** A device that already has a secret cannot get a new one with an enrollment
  token (critical audit entry, `4401`) unless a superadmin allows it once (`POST /api/system/allow-reenroll`),
  for example after a reinstall behind freeze software.
- **Binding.** On the agent HTTP endpoints the authenticated device ID must match the device being written;
  otherwise `403`.
- **The key stays on its hardware (agent 0.1.15-alpha on).** The agent records the hardware it received its key
  on (`secure\hw.bind`). When every reliably read value (machine UUID, BIOS serial) differs at start, the
  installation is a copy: it moves the ID, key and unconfirmed task results aside (`secure\clone-<time>\`, event
  1070) and enrolls as a new device. When only one of them changed, nothing is touched (event 1072); the server's
  `4409` covers that case. `POpsAgent.exe --generalize` prepares a disk image. See [`agent.md`](agent.md#connection).
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

### Power actions and messages

Power actions (shut down, restart, sign out, lock) and messages to the user are their own agent messages
(`power`, `user_message`), not commands: the note, title and text travel as JSON fields, never through a shell, and
the server removes control characters, line breaks (except in a message's text) and bidirectional formatting
characters first. Only admins can send them; a message also needs a panel session (API tokens get `403`), so a
person is behind every text a user sees. For older agents shutdown and restart fall back to the fixed `shutdown`
command, with a note reduced to letters, digits, spaces and `. , : ; ? ' ( ) -`; everything else is refused without
being sent. Audit records keep op or style, delay, target count and the requesting user, and only the length and
the first 60 characters of a note or message. The agent has local `power` and `message` capabilities (on by default)
that a site can switch off like terminal and Vision.

## File transfer

An admin can send a file to PCs and fetch a file from a PC (**Dosya gönder** / **Dosya al**; protocol in
[`api.md`](api.md#file-transfer)). What protects it:

- **Who.** Sending, fetching and downloading a fetched file need an admin in a **panel session**; API tokens are
  refused (`403`), so files are never moved by automation without a person. Viewers can only see the list (names,
  sizes, reasons, who and when). Fetching from **other users' profiles** (`any_profile`) needs a superadmin. A reason
  is required for both directions (3–300 characters).
- **Audit.** Every send (per PC), every request, every received file, every download of a fetched file and every
  result from the agent goes into the hash-chained audit log with metadata only: transfer ID, name, size,
  SHA-256, destination or path, size limit, `any_profile`, reason, who. The file content is never logged or stored in
  the database. The tray's **Etkinlik geçmişim** shows the PC's users that a file was sent or requested (who, when,
  reason) but not the path or name of a fetched file, which may belong to another user.
- **One-time tokens bound to one PC.** Each PC gets its own transfer ID and a random token (32 bytes, stored as
  SHA-256 only) valid for one use and 1 hour. The agent endpoints always need the device secret (`X-Agent-Id` +
  `X-Agent-Secret`), also while `enforce_agent_auth` is off. A wrong token, another PC's secret, a used or expired
  token and an unknown ID all return the same `404`, and failed attempts do not use the token up. The token travels
  only on the PC's own authenticated WebSocket. The agent downloads and uploads only from the server it is enrolled
  with (the URLs are relative).
- **Destinations and file types.** The server sends only `public_desktop` or `inbox`, never a path; the agent writes
  only to its allowlisted folders and refuses `.lnk`, `.url` and `.scr` unless the admin ticked "çalıştırılabilir
  dosyaya izin ver" (the server refuses them too without it). File names are cleaned on the server (no path parts,
  no characters Windows does not allow, no control or direction characters such as U+202E that could make an `.exe`
  look like a `.png`, no reserved names). The agent checks size and SHA-256 before it writes.
- **Paths.** A fetch request must be a full local path with a drive letter. Network paths
  (`\\server\share`, which would make the agent connect to another host with the PC's machine account), device
  paths (`\\?\`), `..`, wildcards and alternate data streams are refused by the server; the agent applies its own
  profile rules on top.
- **Size and storage.** At most 200 MB each way. An oversized request is refused before its body is read, and an
  agent upload is cut off at the requested `max_size`. Files are stored under server-made names in
  `Backend/transfers` (not served, mode `0750`/`0640`); a fetched file is downloaded only through
  `GET /api/files/{id}/content`, always as an attachment with `nosniff` and a sandboxing CSP, so a fetched HTML or SVG
  file cannot run in the panel's origin. Fetched files are deleted after 7 days, sent files once all tokens are used
  or expired; transfers are not in backups.
- **Off switches.** The `files` module turns the feature off for the organisation or a lab (open transfers are
  rejected and their tokens stop working). On the PC, file transfer can be switched off locally; the agent reports
  `files_enabled: false`, and the server sends it nothing. An agent that does not report the capability gets no
  file commands.

## Exam mode

Exam mode (**Sınıflar → Sınıf işlemleri → Sınav modu…**, `POST /api/labs/{lab}/exam`) restricts a lab's PCs for the
length of an exam. What it does:

- **Network isolation with an allow list.** The agent uses the quarantine isolation: outbound traffic is blocked
  except to the POps server, DNS, DHCP and the allowed domain names, IP addresses and networks (domain names are
  resolved by the agent). The server validates the list (no wildcards, no network wider than `/8`, at most 50
  entries).
- **A notice.** The tray shows the admin's message for the whole exam.
- **Optional program block.** Listed programs (`cmd.exe`, …) are blocked while the exam runs. POps's own
  processes and the Windows session processes cannot be listed.
- **A fixed end.** Every exam has an end time at most 8 hours ahead. The agent leaves exam mode at that time on its
  own, also when it is offline; the server ends the exam too and tells connected agents.
- **Accountability.** Starting, ending and the automatic end are written to the hash-chained audit log
  (`exam_start`, `exam_end`, `exam_auto_end`) with who, the lab, the reason and the lists. A PC that reports it left
  exam mode while the exam runs gets an audit entry (`exam_left`) and a notification; a PC whose agent refuses
  (`capability_denied`, capability `exam`) is logged and notified, and the panel shows both.

What it does **not** guarantee:

- **A local administrator can switch it off.** The `exam` capability can be switched off on the PC, and an
  administrator of the PC can stop the agent or change the firewall. Exam mode is meant for students with standard
  accounts. The panel shows such a PC as **Ayrıldı** or **Reddetti** and raises a notification, but only after the
  agent reports it, and only if it still can.
- **It is not proctoring.** POps does not watch, record or analyse the screen, the camera, keystrokes or the
  student during an exam. Remote screen (Vision) keeps its own rules (consent or a recorded mandatory session).
- **It does not stop other devices.** Phones, a second network card the agent does not manage, or a USB modem are
  outside its reach. It also does not change what the allowed sites themselves permit.
- **Older agents ignore it.** An agent without exam mode drops the command; the panel shows it as
  **Desteklemiyor (eski ajan)**, and that PC is not restricted.
- **Offline PCs** get the exam when they connect. A PC that never connects during the exam is not restricted.

## Remote control and transparency

- Remote mouse/keyboard input and `execute` sent over the remote-input path need an admin **and** an open
  remote-control session for that device, opened with a recorded reason. A session grant expires after 30
  minutes without activity.
- Live frames go only to the admin who opened the session, never to viewers or other panels. The server files every
  frame (JSON or Vision v2 binary), monitor list and clipboard text under the device whose secret opened the Vision
  tunnel; a device ID inside the message is ignored, so one enrolled agent cannot show its screen in another
  device's view. Binary frames that break the header rules or exceed 2 MB are dropped and counted in `/metrics`.
- **Clipboard (Vision v2).** Text only, at most 64 KB, both directions, and only in a session the PC user
  accepted: an "ask the user" session (never a mandatory session, which only shows a notice) that was the only open
  session for the PC when its Vision tunnel opened. With several sessions at that moment, or for a session started
  while the tunnel was open, the server cannot tell which one the user accepted and refuses for all of them. Only
  that session's admin can send text to the PC or receive text copied there; at most 30 texts per minute per device
  and direction. The server writes the direction, length, admin and time to the hash-chained audit log before passing
  the text on (no audit entry, no transfer), never the text itself, and keeps no copy; the agent refuses clipboard
  outside an accepted session as well and its event log also records only direction and length. Screen selection
  and quality changes (`select_monitor`, `set_quality`) are forwarded only from the session holder too.
- On the PC, the user is asked for consent, or, for a mandatory session, sees a full-screen countdown. The agent
  applies remote input only during a session the tray started after that step, even if the server is
  compromised. A mandatory session that starts while the PC is locked is not silent: the user sees a notice as soon
  as their desktop returns, until the session ends, and the PC records event 1150.
- Screen previews update the tray tooltip and show a notice at most every five minutes.
- The tray icon is always visible; there is no hidden mode and no keystroke logging.

See [`vision.md`](vision.md).

## Notifications

Notifications appear under **Bildirimler** in the panel (admins and superadmins) and can also be sent out by e-mail
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
| GLPI export failed three runs in a row (`glpi_failed`) | high |
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
At most 30 notifications are sent out per 10 minutes; the rest are still shown under **Bildirimler**. Sending runs in
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

## GLPI export

- Off by default; only a superadmin configures it, tests the connection or starts a run, and these actions are
  audit-logged (the names of changed settings, never their values).
- The GLPI app token and user token are stored encrypted (`v1:`, the key of the 2FA secrets), are never returned by
  the API, and never appear in a log, an error message or the audit log. When the address changes, the saved tokens
  are not used until they are entered again, and a connection test against another address uses only the tokens
  typed in the form, so a saved token is never sent to a new host by itself.
- The GLPI host gets the same guard as the webhook: every resolved address must be public unless
  `GLPI_ALLOW_PRIVATE=1`; the connection is pinned to the checked address, TLS is verified against the host name
  (`GLPI_CA_FILE` for a school CA) and redirects are not followed. Plain `http://` is refused unless the opt-in is set
  and the host is on a private address.
- Data minimisation: no signed-in user, screen images, foreground program or event log content is sent; the reporter
  of a ticket and internal notes are not sent unless the school turns the reporter on (internal notes never).
  Nothing is read back from GLPI into POps.

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
| `agent_logs_v2` | server and agents | Operational event log shown on the **Kayıtlar** page. |
| `enterprise_audit_logs` | server | Remote-control sessions (who, target, reason, mandatory, start/end). |

`GET /api/system/audit-verify` (superadmin) walks the hash chain and reports the first altered or deleted entry.
The chain makes tampering **detectable**; the database role used by the application can still change rows, so
restrict database access.

## What is reachable without a panel login

| Path | Why |
| --- | --- |
| `GET /api/health` | Health check (database state and version only). |
| `POST /api/admin/login`, `/api/admin/login/totp` | Sign-in (rate-limited). |
| `GET /api/auth/sso` | Which sign-in methods are on and the provider's button name (no other settings). |
| `GET /api/auth/oidc/start`, `/api/auth/oidc/callback`, `POST /api/auth/sso/redeem` | OpenID Connect flow, only when it is enabled (otherwise `404`); single-use state, ticket bound to the starting browser, rate-limited. |
| `GET /api/agent_policies` | Agents read the policy; it holds no secrets. |
| `GET /api/branding`, `GET /api/branding/logo` | The organisation name and logo for the sign-in page, set by a superadmin; nothing else. |
| `/download/<file>?sig=…` | Deployment packages for agents, only with the signed link returned at upload (wrong or missing signature: 404). Anyone who has a package's link can still download it, so do not upload anything confidential on the **Dağıtım** page. |
| `/updates/<file>` | The agent MSI being distributed (verified by agents against the signed manifest). |
| `/api/v1/...` | The same endpoints as `/api/...` with the same authentication; nothing extra is open. |
| `/ws/agent/…`, `/ws/vision/…`, agent HTTP endpoints | Agent channels; they require agent credentials once enforcement is on. The software, Windows Update, helpdesk and file transfer endpoints always require them. |

## Operator checklist

- [ ] Serve POps only over HTTPS; keep the backend on `127.0.0.1` and port 8000 closed.
- [ ] Keep `.env` at mode `600`; never commit it. Use a random `JWT_SECRET` and `BYPASS_SECRET` (`install.sh` and
      `setup_env.py` generate them).
- [ ] Change the initial admin password; enable 2FA on every admin and superadmin account (recommended; the panel
      reminds accounts that have not).
- [ ] Give people the lowest role they need; use `viewer` for read-only access.
- [ ] With directory or OIDC sign-in: keep a local superadmin with a strong password and 2FA, map `superadmin` only to
      a small dedicated group, and use a read-only service account.
- [ ] Enroll every PC, then turn on agent-auth enforcement.
- [ ] Revoke enrollment tokens you no longer need; prefer short lifetimes.
- [ ] Install the MSI with `TERMINAL_ENABLED=0` and/or `VISION_ENABLED=0` on PCs that do not need those features.
- [ ] Restrict who can reach the database; back it up.
- [ ] Check the audit chain from time to time (`/api/system/audit-verify`).
- [ ] Send notifications out (e-mail or an `https://` webhook) so takeover attempts and failed updates reach you.
- [ ] Prepare the KVKK notice for the people whose PCs are managed: [`kvkk-aydinlatma.md`](kvkk-aydinlatma.md).

## Reporting a vulnerability

Do not open a public issue. Email **security@pashacore.com.tr** (see [`../SECURITY.md`](../SECURITY.md)).
