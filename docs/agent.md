# Windows Agent

The POps agent runs on each managed Windows PC. It keeps a connection to the server, reports status and
inventory, runs the commands the server queues, and provides screen view and remote control through the tray.
It is written in C# for .NET 10 and published self-contained: the .NET runtime is installed with the agent.

This page is an overview. The detailed references are:

- [`Installer/README.md`](../Installer/README.md): the MSI, its properties, upgrades and migration from older installs,
- [`Agent/README.md`](../Agent/README.md): settings, secrets, server authentication, local hardening, the capability
  policy, the update and rollback procedure, and the unit tests.

## Components

| Program | Runs as | Started by | Does |
| --- | --- | --- | --- |
| `POpsAgent.exe` | Windows service `POpsAgent`, LocalSystem | Windows (automatic start; restarted on failure) | Server connection, heartbeats, commands, inventory, quarantine, update download and verification. |
| `POpsTray.exe` | the signed-in user | `HKLM\…\Run` for every user; from 0.1.6-alpha also the service and the updater, in the active console session, when no tray runs | Tray icon and notices, consent dialog and countdown for remote sessions, fair-use notice, quarantine lock screen, screen capture, applying remote input, offline bypass code entry, help desk (**Sorun bildir**, **Taleplerim**). |
| `POpsWatchdog.exe` | the signed-in user | the service (at start and every 30 seconds) and the updater, in the signed-in user's session | Every 10 seconds: restarts the tray if it is not running and starts the `POpsAgent` service if it is stopped. Pauses while an update is in progress. Errors (for example a user who may not start services) are logged, the same message at most every 10 minutes. |
| `POpsUpdater.exe` | LocalSystem | the service, from a copy in `C:\POpsData\updater` | Installs a verified MSI, checks the new version's health and rolls back if needed. |
| `POps.Shared.dll` | – | – | Shared helpers: version, logging, settings lookup, hardware ID. |

The service and the tray talk over the local named pipe `POpsTrayPipe`. The service accepts only the installed
`POpsTray.exe` running in a user session on it (and, when the tray is Authenticode-signed, a valid signature).
From 0.1.6-alpha the tray also checks that the pipe's owner is SYSTEM or Administrators before it trusts it.

Screen capture is done by the tray ([`vision.md`](vision.md)). Older releases had a standalone `POpsVision.exe`; it
was not shipped since 0.1.2-alpha and its source was removed in 0.1.14-alpha.

Requirements: 64-bit Windows 10 or 11. Nothing else: from 0.1.15-alpha the service, tray, watchdog and updater are
published self-contained into one folder and share a .NET 10 runtime that the MSI installs with them (no .NET
prerequisite; up to 0.1.14 the .NET 8 Desktop Runtime was required). When the agent updates itself the updater is
copied to `C:\POpsData\updater` together with every runtime file its `POpsUpdater.deps.json` lists (about 80 MB).

## Installing

1. On **Sistem** → "Ajan kaydı ve kimlik", create an enrollment token (**Jeton üret**). Choose the lab the PCs should land
   in, the number of uses (one token can enroll a whole lab) and the lifetime (default 72 hours).
2. Install the MSI from the GitHub release on each PC:

   ```
   msiexec /i POps-Agent-<version>-win-x64.msi /qn /l*v C:\Windows\Temp\pops-msi-install.log SERVER_URL=https://pops.example.com ENROLL_TOKEN=<token>
   ```

3. The PC appears on **Cihazlar** within seconds, in the token's lab (or in `Atanmamis_Cihazlar` if the token
   has no lab).

The command line is visible to other signed-in users while `msiexec` runs, so install through a deployment tool
or while no student is signed in. All MSI properties are listed in [`configuration.md`](configuration.md#msi-properties).

### Machines with freeze software

Enroll before freezing, so the device secret is part of the frozen image, or set `PERSIST_DIR` to a local folder
that is not rolled back. Details in [`Agent/README.md`](../Agent/README.md#machines-with-freeze-software). A device
that lost its secret needs a new enrollment token and, because the server still holds a secret for it, a one-time
re-enrollment permission (`POST /api/system/allow-reenroll`); without that permission the server refuses the token
and closes the connection with `4401`.

## Connection

The wire format of both WebSockets (`/ws/agent`, `/ws/vision`) is specified in [`protocol/`](protocol/README.md).

- The agent reads `ServerUrl` and connects to `wss://<host>/ws/agent/<hw_id>`. A non-loopback `http://` address is
  refused: the agent logs `[GÜVENLİK] ServerUrl şifresiz http ve yerel değil …` every 10 minutes and does not
  connect (the tray and watchdog keep running).
- It sends a heartbeat every 5 seconds. After a disconnect it waits a random time between 0 and
  min(60 s, 2 s × 2^n), where n is the number of connections that failed in a row (full jitter; 0.1.7 and older
  waited a fixed 5 s, so all agents came back at once after a server restart). n goes back to 0 once a connection
  stays open for one heartbeat after the first messages. If the server rejected the credentials (close code
  `4401`) it waits 60 s plus that random time. If the server refused the ID because it is connected from another
  PC (`4409`, a copied installation; 0.1.15-alpha on) it waits 10 minutes plus that random time and logs it (event
  1071 once per service start). From 0.1.5-alpha the heartbeat carries `"quarantined"`
  (lock screen and/or isolation active), which the server uses to finish or resend a pending lock/unlock.
- The heartbeat also carries `agent_health`: service start time, last successful policy sync and inventory upload,
  tray connection, Vision channel (`off` / `idle` / `connected`), background-loop errors in the last hour and a
  sanitized last error of at most 200 characters. The in-memory window resets with the service; older servers
  ignore the unknown block.
- **Hardware ID.** The device ID (`HW-…`) is kept in `C:\POpsData\identity.key`. On first start it is derived from
  the machine UUID and the primary MAC address. The server compares a hardware fingerprint (UUID, BIOS serial,
  disk serial, MAC, RAM serial) on every connection. Before the device has a secret it may assign a different ID
  (`set_identity`), for example when a reinstalled PC is recognised. Once enrolled, the ID and secret never move to
  other hardware: a connection whose hardware does not match is logged (`dna_mismatch`, at most once an hour per
  device) and does not overwrite the stored fingerprint, and while the original device is connected the copy is
  refused (close code `4409`, notification `clone_rejected`).
- **Disk images.** An image captured after the agent enrolled carries that device's ID and secret. As the last
  step before capturing, run in an elevated prompt (0.1.15-alpha on):

  ```
  "C:\Program Files\POps\POpsAgent.exe" --generalize --enroll-token <token>
  ```

  It stops the service and the watchdog (which would start it again), deletes `identity.key`, the device secret
  (also its `PersistDir` copy), `bypass.device`, `hw.bind`, unconfirmed task and update results, the last
  software-inventory record and earlier `secure\clone-*` folders, and writes the token to `secure\enroll.token`.
  Every PC started from the image derives its ID from its own hardware and enrolls with the token. Use a
  **multi-use** token with enough uses for every PC and a lifetime that covers the rollout; the token's lab is
  where the PCs land. Shut down and capture right away: if the service starts again on the reference PC, it
  enrolls itself and the command has to be run again. Without `--enroll-token` an existing `enroll.token` is kept.
  Exit codes: `0` done; `1` a file could not be deleted or the token could not be written (see the output); `2` not
  run as administrator; `3` the service or the watchdog could not be stopped; `4` invalid arguments (unknown
  argument, missing or malformed token). With `2`, `3` and `4` nothing is changed. Installing the agent after
  imaging (GPO, deployment tool) also works.
- **Key bound to the hardware (0.1.15-alpha on).** When the secret arrives (`set_secret`) the agent writes
  `C:\POpsData\secure\hw.bind`: the SHA-256 of `uuid|bios_sn` (normalized as in `dna_payload`), separate digests
  of the values that are real, the ID the key was issued to and the time; with `PersistDir` also there. A value is
  not real when it is empty, all zeros or `F`, the UUID many boards share (`03000200-0400-0500-0006-000700080009`)
  or a placeholder such as "To be filled by O.E.M." or "Default string". An agent that has a secret but no
  `hw.bind` (0.1.14 and older) writes today's hardware on its first start (trust on first use). At every start,
  before the ID and the secret are read, the digest is computed again and the values that were real both then and
  now are compared:
  - **All of them changed** (a copied image on another PC changes both; if only one can be compared, it decides
    alone): the installation was copied. `identity.key`, `agent.secret` (and its `PersistDir` copy),
    `bypass.device`, `hw.bind` and unconfirmed task results (`pending-results.json`, so the original's results are
    not sent under the new ID) are moved, not deleted, to `C:\POpsData\secure\clone-<UTC time>\`. Event 1070 is
    written, the ID is derived from the hardware again and the agent continues as an unenrolled device (with
    `enroll.token` if there is one).
  - **One changed, the other did not** (motherboard service, a corrected BIOS serial, a virtual machine setting):
    nothing is touched; it is logged and event 1072 is written once per start. The server's `4409` still refuses
    a real copy while the original is connected.
  - **None changed** (only a value that is not real differs, for example a BIOS update filled in an empty serial):
    the same PC.
  - **Nothing can be compared:** nothing is touched and this is logged once.

  With `PersistDir` the newer `hw.bind` counts, and on the same hardware `identity.key` is set back
  to the ID in `hw.bind` (freeze software brings back the imaged ID at every boot). A copy made from an image
  without `hw.bind` (captured with 0.1.14 or older) trusts its own hardware on first start; the server's `4409`
  is then the only protection.
- **Authentication.** Before it has a device secret the agent sends the enrollment token (`X-Enroll-Token`); the
  server answers with `set_secret`. From then on it sends `X-Agent-Secret`. Secrets live in `C:\POpsData\secure`
  (SYSTEM and Administrators only) and are never written to a log. See [`security.md`](security.md#agent-identity).
- **Vision authentication.** The command socket may use the enrollment token for first registration, but the
  Vision socket never sends it: Vision requires the device's `X-Agent-Secret` and stays closed before enrollment.
  A Vision `4401` rejection clears the stream and local approval without an automatic retry.
- **Inventory.** When the server has no hardware inventory for the device, it asks for it (`get_hardware`) and the
  agent posts CPU, RAM, motherboard, GPU, OS, IP, MAC and disk information.
- **Policy.** The agent fetches `GET /api/agent_policies` every 60 seconds.

## Commands

What the service does with each server command:

| Command | Effect |
| --- | --- |
| `execute` | Runs the command line as a temporary `.bat` through `cmd.exe` as LocalSystem (UTF-8, 30-minute limit) and returns the output as a `result`. Refused when the terminal capability is off. Used by **Dağıtım**, **Uzak komut** and the PC actions on **Cihazlar** and **Sınıflar** through the task queue. The `.bat` (`pops_task_<32 hex>.bat` in the service's temp folder) is deleted when the task ends; from 0.1.14-alpha files left by a crash are deleted at service start, before the first task (only names matching exactly that pattern). Output is read in fixed 8192-character chunks, not by line, so even a single line of hundreds of megabytes stays within the 524 288-character (512 Ki) limit (the rest is read and dropped, the pipe never blocks). The same task ID is never run twice at once: a repeated `execute` for a running task is logged and ignored. Exit codes the agent sets itself: -1 time limit, -2 cancelled, -3 agent error, -4 service stopping, -5 refused (terminal capability off). |
| `get_hardware` | Posts the hardware inventory. |
| `start_vision_session` | Passes the session request to the tray (consent dialog or mandatory countdown). |
| `stop_stream` | Stops screen capture and closes the Vision connection. |
| `remote_input` | Screen preview (`get_thumbnail`), frame-rate change (`set_fps`) or mouse/keyboard input, subject to the Vision capability and, for input, an active session. Keyboard (from 0.1.14-alpha, `SendInput`): named keys (Enter, F1–F24, arrows, Home/End, …) become virtual keys, left/right modifiers from `code`; a single character is sent as Unicode (`KEYEVENTF_UNICODE`), so İ, ş, ğ, @ and € arrive as typed whatever the PC's layout; with Ctrl or Alt (not both, not AltGr) or Win the character becomes the key from `code` (Ctrl+C, Win+R). Keys still held when control ends or the service connection drops are released. |
| `lockdown` / `unlock` | Quarantine on / off (below). |
| `wake_peer` | Sends a Wake-on-LAN packet for another PC in the same lab. |
| `set_identity` | Replaces the stored hardware ID. |
| `set_secret` | Stores the device secret and deletes the enrollment token. |
| `server_info` | Sent by the server once the agent is registered; `features` containing `update_result_ack` / `result_ack` means the server confirms update results / task results (0.1.14-alpha). Without `server_info` within 15 seconds of connecting the agent treats the server as older (same rule for both). |
| `result_ack` | The server stored the task result for `task_id`; the agent deletes it from `C:\POpsData\secure\pending-results.json`. |
| `update_result_ack` | The server stored the update result with this `result_id`; the agent sets `update-result.json` aside. |
| `set_bypass_secret` | Stores the per-device offline bypass key and acknowledges its fingerprint; accepted only on a device-secret command connection. |
| `set_capabilities` | Switches terminal and/or Vision **off**; requests to switch them on are ignored. |
| `update_agent` | Starts a signed update (below). |

The server may also send `scan_updates` and `install_updates`
([below](#software-inventory-and-windows-updates)); agents up to 0.1.4-alpha ignore them.

The agent reports back `result`, `thumbnail`, `stream_frame` (on the Vision socket), `vision_rejected`,
`capabilities`, `capability_denied` and `update_result`. Every message, with a JSON Schema and test vectors, and
the protocol versioning rules are in [`protocol/`](protocol/README.md); [`protocol/AGENT_TESTS.md`](protocol/AGENT_TESTS.md)
describes how the agent tests use the vectors.

Task results (from 0.1.14-alpha): with a server that announces `result_ack`, every `result` is first written to
`C:\POpsData\secure\pending-results.json` (SYSTEM and Administrators only; temporary file + rename), sent, and deleted
only when `result_ack` for its `task_id` arrives. Unacknowledged results are sent again on the next connection, after
`server_info`, and survive a service restart. At most 20 are kept; when full, the oldest is dropped and logged. With
an older server the agent keeps the previous behaviour: results wait in memory while disconnected and are dropped
once sent; results left on disk are sent to such a server once and then deleted. A refused `execute` (terminal off)
is reported as a `result` with `exit_code` -5 followed by `capability_denied`.

High-impact actions also have a server-independent local record in the Windows **Application** event log under
the `POps Agent` source. IDs 1000/1001 cover command start/finish (only SHA-256 and length are recorded, never the
command text), 1010/1011 Vision sessions, 1020/1021 quarantine, 1022 quarantine allow list refreshed (old and new
server addresses), 1030 update results, 1040 capability changes, 1050 identity rejection, 1060 receipt of a
bypass-key fingerprint, 1070 a copied installation set aside at start, 1071 a `4409` rejection and 1072 hardware
that partly changed (no decision taken), 1080 a change of the server's modules and 1100 a clipboard shared in a
Vision session (direction and length only). Failure to write an event does not stop the
service.

## Capability policy

Terminal (`execute`) and Vision (streaming, previews, remote input) can be disabled per PC, so that even a
compromised server cannot use them there. The MSI sets them (`TERMINAL_ENABLED`, `VISION_ENABLED`, `1` / `0`);
the server can only switch them off (**Sistem** → "Cihaz yetenekleri"). A refused command is closed with
a `[REDDEDİLDİ]` result and reported as `capability_denied`. Re-enabling needs a local administrator: MSI repair or
reinstall with `…_ENABLED=1`. The state is in `C:\POpsData\secure\capabilities.json`; see
[`Agent/README.md`](../Agent/README.md#capability-policy).

## Modules

From 0.1.15-alpha a server can switch features (modules) on and off per lab. The agent adds its key
(`X-Agent-Id` + `X-Agent-Secret`) to `GET /api/agent_policies`; the server answers with the lab's DNS settings and
`"modules": {"<id>": true|false}` for `vision`, `terminal`, `deploy`, `schedules`, `patches`, `software`,
`licenses`, `helpdesk`, `dns_policy`, `quarantine`, `wol` and `reports`. The policy is fetched every minute, so a
change applies within a minute.

- **Missing field.** Without `modules` (older server, or an agent without a key, which sends no headers) every
  module counts as on. If a request fails, the last known state stays.
- **Memory only.** The state is kept in memory and never written to `capabilities.json`. A feature is available
  only when the module is on **and** the local capability allows it. The local lock stays separate and wins: a
  feature locked on the PC stays off when the server turns the module on. A module the server turns back on works
  again without a restart.
- **What the agent refuses** while a module is off:

  | Module | Behaviour |
  | --- | --- |
  | `terminal` | `execute` is not run. The result is `exit_code` -5 with `[REDDEDİLDİ] Uzak komut modülü …`, followed by `capability_denied` with `"reason": "module_disabled"`. |
  | `vision` | `start_stream`, `start_vision_session`, the Vision tunnel, previews and remote input are refused with `capability_denied` (`module_disabled`). A Vision session that is open when the module closes is ended. |
  | `helpdesk` | The tray hides "Sorun bildir" and "Taleplerim" (`HELPDESK_MENU:0` over the pipe) and shows them again when the module opens. Requests that still arrive get "Yardım masası … kapalı", and new replies are not polled. |
  | `software` | The software inventory is not collected or sent. When the module opens, the last-send record is forgotten, so the list goes out in the next 6-hour round even if it did not change (the server did not store it while the module was off). |
  | `patches` | The daily Windows Update scan and its report are skipped. `scan_updates` and `install_updates` are refused (`capability_denied`, `module_disabled`). A report finished while the module was off is dropped. |
  | `wol` | `wake_peer` (waking another PC in the lab) is refused (`capability_denied`, `module_disabled`). |
  | `dns_policy`, `quarantine` | Nothing extra on the agent. The server sends an empty DNS list and `auto_quarantine: false`. Unlock, offline bypass and the lock screen work as before. |

- **Server only.** `deploy`, `schedules`, `licenses` and `reports` are enforced by the server.
- **Logging.** A change is logged once and written to the event log as 1080 (closed and opened modules). On the
  first policy after the service starts, this happens only when a module is off.

## Server certificate

The agent talks to the server only over TLS and verifies the server certificate on every connection: the
command WebSocket, the HTTP endpoints, the Vision tunnel and the package download for updates all go through
one check, `POps.Shared.ServerTrust`.

- **Pinned to the school CA** (`server_ca = custom`): the MSI property `SERVER_CA_CERT=<path to pops-ca.pem>`
  stores the CA as `C:\POpsData\secure\server-ca.pem` (SYSTEM and Administrators only). While that file exists a
  server certificate is accepted **only** if it chains to that one root (custom root trust, no revocation check,
  so it works on a network without internet) **and** its name matches the host in `ServerUrl`. Nothing in the
  Windows trust store counts, so a certificate planted there, or a stolen public certificate, does not get a
  connection.
- **Windows trust store** (`server_ca = system`): no `server-ca.pem`. .NET's usual decision applies, as in every
  version before 0.1.10: public CAs such as Let's Encrypt, and the school CA if it was distributed by GPO.
- A `server-ca.pem` that cannot be read or is not a certificate fails closed: every connection is refused until
  the file is fixed or removed (the MSI refuses such a file at install time, so this needs a hand-edited file).

A refused certificate is logged as `[GÜVENLİK] Sunucu sertifikası kurum sertifikasına (server-ca.pem)
zincirlenmiyor` (or `… ana makine adıyla eşleşmiyor`), at most once a minute; the connection is not made and the
agent retries with its normal backoff. The `capabilities` message carries `server_ca` so the **Sistem** page shows
the mode of each device. `SERVER_CA_CERT=system` on an upgrade removes the pinned CA; not giving the property keeps
the current file. How to set up the server side, distribute `pops-ca.pem` and rotate the CA: [`docs/tls.md`](tls.md).

## Quarantine and offline bypass

`lockdown` (from **Uzak ekran** → "Karantinaya Al", from **Diğer** → "Karantinaya al" on **Cihazlar** or **Sınıflar**, or `POST /api/security/lockdown`):

- the tray shows a full-screen lock screen that blocks the Windows, Tab, Esc and F4 keys, stays on top and takes the
  focus back within half a second,
- from 0.1.11-alpha the Ctrl+Alt+Del screen offers no way out while the lock lasts: Task Manager (also
  Ctrl+Shift+Esc and `taskmgr`) and Switch user disappear at once (machine policy); Sign out, Change a password and
  Lock are hidden in every signed-in user's hive and take effect at that user's next sign-in (Windows reads them only
  then; in the open session they stay visible but do not end the lock). The previous values are kept in `C:\POpsData\secure\kiosk-policies.json` and put back
  when the lock ends (panel, bypass code, service start without a lock, MSI uninstall); only what POps changed is
  undone. Details and the manual procedure: `Agent/README.md` (*Ctrl+Alt+Del during a quarantine*, *Lifting a
  quarantine by hand*),
- the service adds Windows Firewall block rules (group `POps Isolation`) for every address except the POps server,
  the DNS and DHCP servers, loopback and IPv6 link-local/multicast, and switches on all firewall profiles. Their
  previous state is saved in `C:\POpsData\secure\isolation.json`,
- from 0.1.14-alpha, while the quarantine lasts, the service resolves the server name again every 5 minutes and
  after 3 failed connections in a row (DNS stays open). If the set of addresses changed and is not empty, the rules
  are rebuilt for the new addresses (new rules first, then the old ones are removed) and event 1022 is written; an
  empty or failed lookup leaves the rules as they are. The previous profile state in `isolation.json` is kept as it
  was at the first quarantine, so unlocking restores the right profiles. Before, the addresses were resolved only
  once, and a server that changed its IP cut the PC off until the bypass code was used.

`unlock` removes the lock screen and the rules and restores the firewall profiles.

From 0.1.5-alpha the lock survives the tray: the service keeps it in `C:\POpsData\secure\lockdown.json` and shows
the lock screen again whenever the tray connects (after closing it in Task Manager, signing out or restarting). If
the rules cannot be removed, the lock stays, the user is not told it was lifted, and the agent records
`agent.unlock_failed`; how a local administrator lifts a quarantine by hand is described in
[`Agent/README.md`](../Agent/README.md#lifting-a-quarantine-by-hand). The DNS threshold (`auto_quarantine`) takes the same path as `lockdown`: lock screen with the
reason "DNS kural ihlali eşiği" plus isolation, reported as `agent.auto_quarantine` so the panel shows the device as
quarantined.

**Offline bypass.** If a quarantined PC cannot reach the server, an admin can get the day's code for it with
**Çevrimdışı açma kodu** in the **Diğer** menu of the PC's detail panel (on **Cihazlar** or **Sınıflar**; shown for a quarantined PC; `POST /api/security/bypass_token/{pc}`; every request is logged). With the per-device key a code works once per day: if the PC was already unlocked with today's code, ask for the code again to get the next one. The user enters
it on the lock screen or in the tray menu **Yönetici Müdahalesi (Bypass)**. From 0.1.5-alpha a valid code does what
`unlock` does: it closes the lock screen and removes the network isolation, and when the server can be reached the
agent records the use as `agent.offline_bypass`. Older agents only remove the isolation. The server provisions a
separate 32-byte base64url key for each enrolled device with `set_bypass_secret`. The agent stores it as
`C:\POpsData\secure\bypass.device`; the code is the first six uppercase hex characters of HMAC-SHA256(key,
UTF-8(`hw_id|yyyy-MM-dd`), using the device's local date). If `bypass.device` exists, a malformed or unreadable file
fails closed and the legacy fleet secret is not tried. When the file is absent, older servers remain compatible
through the deprecated first-six-hex SHA-256(`hw_id` + `BYPASS_SECRET` + date) formula. After 5 wrong codes the bypass locks for 15
minutes, doubling up to 24 hours; from 0.1.5-alpha the counters are kept in `bypass-state.json` and survive a
restart, and the lock screen and the tray check the code format first so a typo does not use up an attempt.

## Policies

- **Fair-use notice.** When the policy has a `fair_use_text`, the tray shows it in a window titled
  "Kurumsal Adil Kullanım Politikası" that the user closes with "Okudum, Anladım ve Kabul Ediyorum".
- **DNS policy.** The agent contains DNS-cache matching against the policy's `dns_domains` (exact domain or
  subdomain, per active category), `policy_alert` reporting and the `auto_quarantine` threshold. In agents up to
  0.1.4-alpha the service does not start that monitoring loop, so they report no DNS violations and never
  quarantine a PC automatically. From 0.1.5-alpha it starts when the command channel first connects and checks
  the DNS cache every 15 seconds; without a `dns_domains` list nothing is flagged. A violation is one list entry
  (subdomains of the same entry count once), the threshold applies to the last hour, and the count starts again
  when the user signed in at the console changes. Details in
  [`Agent/README.md`](../Agent/README.md#local-hardening).

## Help desk

From 0.1.6-alpha the tray menu has **Sorun bildir** and **Taleplerim**. The tray sends the form to the service
over the pipe; the service adds the user of the tray's session as the reporter, limits the text (subject 200,
description 5000 characters) and the request rate (list every 6 s, new request every 10 s, one at a time), and
calls `POST /api/tickets/agent/{hw_id}`. **Taleplerim** reads `GET /api/tickets/agent/{hw_id}` and
shows only the signed-in user's requests, because a lab PC is shared. New replies are checked every 5 minutes and
shown as a balloon. Details in [`Agent/README.md`](../Agent/README.md#help-desk-sorun-bildir--taleplerim).

From 0.1.9-alpha the tray menu also has **Etkinlik geçmişim**: what IT administrators did on this PC in the last
30 days (remote sessions, commands, quarantine, updates, capability changes, Windows Update, enrolment). The tray
sends `ACTIVITY_LIST` over the pipe; the service calls `GET /api/activity/agent/{hw_id}` as the enrolled agent
(`X-Agent-Id` + `X-Agent-Secret`, as for **Taleplerim**), at most one request at a time and one every 6 s (the server
allows one per device every 5 s), and keeps a successful answer for 60 s. The server writes the Turkish title and
detail of each entry; the tray shows them as they are (newest first, at most 200; unknown kinds too), with only
control characters removed. The list holds only actions on this device, never other users' personal data. Without a
device secret, when the server is unreachable, or when it does not have the endpoint yet (404) the window shows a
Turkish message instead.

## Software inventory and Windows updates

The server side is in place; agents report this data from 0.1.5-alpha on. Older agents send nothing, show as
"bildirmedi" on **Raporlar**, and ignore the commands. The contract between agent and server
(`Backend/pops/routers/inventory.py`, `Backend/pops/models.py`):

| Direction | Message | Content |
| --- | --- | --- |
| agent → server | `POST /api/software/{hw_id}` | `{"items": [{"name", "version", "publisher", "install_date"}]}`: the complete list of installed programs, which replaces the stored list (at most 5000 items). |
| agent → server | `POST /api/patches/{hw_id}` | Windows Update state: pending, security and critical counts, `reboot_required`, `last_search`, `last_install` (ISO 8601), the pending updates (`kb`, `title`, `severity`, `categories`, `is_security`) and `last_result`. |
| server → agent | `{"action": "scan_updates", "scope": ...}` | Run a Windows Update scan and report the result with `POST /api/patches/{hw_id}`. |
| server → agent | `{"action": "install_updates", "scope": "security" \| "all"}` | Install the pending security/critical updates, or all of them. The PC is not restarted; the need for a restart is reported as `reboot_required`. |

Both endpoints require `X-Agent-Id` + `X-Agent-Secret` of an enrolled device, even while enforcement is off.
Admins send the commands from **Raporlar** → **Windows güncellemeleri**; only online devices receive them. When the
agent collects and sends this data, how it classifies updates and what an installation does are described in
[`Agent/README.md`](../Agent/README.md#what-the-agent-reports).

## Updates

Updates are signed MSI packages; the agent installs nothing unsigned.

1. A superadmin stages a release on **Sistem** (download from GitHub, or upload `manifest.json`,
   `manifest.json.sig` and the MSI) and sends it to all agents, a lab or selected PCs. Only online agents receive
   it; send it again for the others later. A PC that got the same version less than 15 minutes ago (or reported
   an update stage in that time) is not sent it again: its update lock would make the agent ignore it anyway.
2. The agent verifies the manifest's ed25519 signature with the public key compiled into it, refuses a version
   that is not newer than its own, downloads the MSI from `<ServerUrl>/updates/<name>` and checks its size and
   SHA-256. From 0.1.23-alpha the download uses BITS: it resumes after a network drop or a restart, and a download
   that is still running after 15 minutes continues with the next update command. When BITS cannot be used, the
   agent downloads directly as before. See [`design/peer-cache.md`](design/peer-cache.md).
3. `POpsUpdater` installs it, waits up to 90 seconds for the new version to report `phase: "operational"`
   in `C:\POpsData\health.json`, and otherwise rolls back to the previous MSI. Operational means the agent's
   identity, credentials, capabilities, quarantine/TLS state and tray pipe are ready and its first connection
   attempt has begun; the server need not be reachable and slow WMI inventory continues in the background.
   This avoids accepting a process that starts but fails during core initialization. Phase-less health files
   from 0.1.11 and older remain valid when an update rolls back to one of those versions.
4. The result (`success`, `pending_reboot`, `rolled_back`, `rollback_failed`, `install_failed`, `rejected`, …) is
   written to `C:\POpsData\update-result.json` and reported to the server, which records it in the audit log and
   shows it in the panel. From 0.1.14-alpha the message carries `result_id` (first 32 hex digits of the SHA-256 of
   the file). A server that announces `update_result_ack` in `server_info` confirms it with `update_result_ack`;
   until then the file stays and the result is sent again at most every 60 seconds while connected, so a server
   that fails before storing it does not lose it. Without `server_info` within 15 seconds of connecting the agent
   treats the server as older: it sends the result once and sets the file aside, as before.

The outcomes and the rollback drill are described in [`Agent/README.md`](../Agent/README.md#updates). Agents
older than 0.1.3-alpha cannot apply signed updates and must be reinstalled once with the MSI.

## Files and logs

| Path | Contents |
| --- | --- |
| `C:\Program Files\POps\` | Programs and `appsettings.json` (`ServerUrl`, `PersistDir`; SYSTEM and Administrators only). |
| `C:\POpsData\identity.key` | Hardware ID. |
| `C:\POpsData\secure\` | `agent.secret`, `enroll.token`, `bypass.secret`, `capabilities.json`, `isolation.json`, `lockdown.json`, `bypass-state.json`, `hw.bind`, `clone-<time>\` (SYSTEM and Administrators only). |
| `C:\POpsData\health.json`, `update.lock`, `update-result.json` | Update state. |
| `C:\POpsData\session.json`, `patch-scan.json` | Last reported sign-in; time of the last Windows Update scan and a report not yet delivered (0.1.5-alpha on). |
| `C:\POpsData\software-inventory.json` | Last software inventory sent: SHA-256 of the sorted list, device ID and time (0.1.15-alpha on). |
| `C:\POpsData\packages\installed.msi`, `updates\`, `updater\` | Rollback package, downloaded update, updater copy. |
| `C:\POpsLogs\POps_<yyyyMMdd>.log` | Service and updater log (SYSTEM and Administrators only). |
| `%LOCALAPPDATA%\POps\Logs\` | Per-user logs: `POpsWatchdog_<yyyyMMdd>.log` and the tray's `TrayLog.txt` (message types only, rotated at 1 MB). |

Uninstalling removes the programs, the service, the Run entry and `appsettings.json`, but keeps `C:\POpsData`
(identity and secret) and `C:\POpsLogs`, so a reinstalled PC returns with the same identity.

## Building and testing

Build commands are in [`Installer/README.md`](../Installer/README.md#build-locally). Unit tests:

```
dotnet test Agent/POps.Tests/POps.Tests.csproj --configuration Release
```

The version of every component comes from the repository-root `VERSION` file (`Agent/Directory.Build.props`).
