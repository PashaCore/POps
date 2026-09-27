# Windows Agent

The POps agent runs on each managed Windows PC. It keeps a connection to the server, reports status and
inventory, runs the commands the server queues, and provides screen view and remote control through the tray.
It is written in C# for .NET 8.

This page is an overview. The detailed references are:

- [`Installer/README.md`](../Installer/README.md): the MSI, its properties, upgrades and migration from older installs,
- [`Agent/README.md`](../Agent/README.md): settings, secrets, server authentication, local hardening, the capability
  policy, the update and rollback procedure, and the unit tests.

## Components

| Program | Runs as | Started by | Does |
| --- | --- | --- | --- |
| `POpsAgent.exe` | Windows service `POpsAgent`, LocalSystem | Windows (automatic start; restarted on failure) | Server connection, heartbeats, commands, inventory, quarantine, update download and verification. |
| `POpsTray.exe` | the signed-in user | `HKLM\…\Run` for every user | Tray icon and notices, consent dialog and countdown for remote sessions, fair-use notice, quarantine lock screen, screen capture, applying remote input, offline bypass code entry. |
| `POpsWatchdog.exe` | the signed-in user | the service, in the signed-in user's session | Every 10 seconds: restarts the tray if it is not running and starts the `POpsAgent` service if it is stopped. Pauses while an update is in progress. |
| `POpsUpdater.exe` | LocalSystem | the service, from a copy in `C:\POpsData\updater` | Installs a verified MSI, checks the new version's health and rolls back if needed. |
| `POps.Shared.dll` | – | – | Shared helpers: version, logging, settings lookup, hardware ID. |

The service and the tray talk over the local named pipe `POpsTrayPipe`. The service accepts only the installed
`POpsTray.exe` running in a user session on it (and, when the tray is Authenticode-signed, a valid signature).

`Agent/POpsVision` is the source of an older standalone screen-streaming program. It is not part of releases or
the MSI; screen capture is done by the tray ([`vision.md`](vision.md)).

Requirements: 64-bit Windows and the .NET 8 Desktop Runtime (x64). The MSI checks for the runtime and refuses to
install without it.

## Installing

1. On **Sistem & Sürüm** → "Ajan kaydı ve kimlik", create an enrollment token. Choose the lab the PCs should land
   in, the number of uses (one token can enroll a whole lab) and the lifetime (default 72 hours).
2. Install the MSI from the GitHub release on each PC:

   ```
   msiexec /i POps-Agent-<version>-win-x64.msi /qn /l*v C:\POpsLogs\msi-install.log SERVER_URL=https://pops.example.com ENROLL_TOKEN=<token>
   ```

3. The PC appears on **Cihaz Yönetimi** within seconds, in the token's lab (or in `Atanmamis_Cihazlar` if the token
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

- The agent reads `ServerUrl` and connects to `wss://<host>/ws/agent/<hw_id>`. A non-loopback `http://` address is
  refused: the agent logs `[GÜVENLİK] ServerUrl şifresiz http ve yerel değil …` every 10 minutes and does not
  connect (the tray and watchdog keep running).
- It sends a heartbeat every 5 seconds and reconnects 5 seconds after a disconnect, or 60 seconds after the
  server rejected its credentials (close code `4401`).
- **Hardware ID.** The device ID (`HW-…`) is kept in `C:\POpsData\identity.key`. On first start it is derived from
  the machine UUID and the primary MAC address. The server compares a hardware fingerprint (UUID, BIOS serial,
  disk serial, MAC, RAM serial) on every connection and may assign a different ID (`set_identity`), for example
  when a disk image was cloned to another PC or when a reinstalled PC is recognised.
- **Authentication.** Before it has a device secret the agent sends the enrollment token (`X-Enroll-Token`); the
  server answers with `set_secret`. From then on it sends `X-Agent-Secret`. Secrets live in `C:\POpsData\secure`
  (SYSTEM and Administrators only) and are never written to a log. See [`security.md`](security.md#agent-identity).
- **Inventory.** When the server has no hardware inventory for the device, it asks for it (`get_hardware`) and the
  agent posts CPU, RAM, motherboard, GPU, OS, IP, MAC and disk information.
- **Policy.** The agent fetches `GET /api/agent_policies` every 60 seconds.

## Commands

What the service does with each server command:

| Command | Effect |
| --- | --- |
| `execute` | Runs the command line as a temporary `.bat` through `cmd.exe` as LocalSystem (UTF-8, 30-minute limit) and returns the output as a `result`. Refused when the terminal capability is off. Used by the Deployment and Terminal pages through the task queue. |
| `get_hardware` | Posts the hardware inventory. |
| `start_vision_session` | Passes the session request to the tray (consent dialog or mandatory countdown). |
| `stop_stream` | Stops screen capture and closes the Vision connection. |
| `remote_input` | Screen preview (`get_thumbnail`), frame-rate change (`set_fps`) or mouse/keyboard input, subject to the Vision capability and, for input, an active session. |
| `lockdown` / `unlock` | Quarantine on / off (below). |
| `wake_peer` | Sends a Wake-on-LAN packet for another PC in the same lab. |
| `set_identity` | Replaces the stored hardware ID. |
| `set_secret` | Stores the device secret and deletes the enrollment token. |
| `set_capabilities` | Switches terminal and/or Vision **off**; requests to switch them on are ignored. |
| `update_agent` | Starts a signed update (below). |

The agent reports back `result`, `thumbnail`, `stream_frame` (on the Vision socket), `vision_rejected`,
`capabilities`, `capability_denied` and `update_result`. The full message list is in [`api.md`](api.md#websockets).

## Capability policy

Terminal (`execute`) and Vision (streaming, previews, remote input) can be disabled per PC, so that even a
compromised server cannot use them there. The MSI sets them (`TERMINAL_ENABLED`, `VISION_ENABLED`, `1` / `0`);
the server can only switch them off (**Sistem & Sürüm** → "Cihaz yetenekleri"). A refused command is closed with
a `[REDDEDİLDİ]` result and reported as `capability_denied`. Re-enabling needs a local administrator: MSI repair or
reinstall with `…_ENABLED=1`. The state is in `C:\POpsData\secure\capabilities.json`; see
[`Agent/README.md`](../Agent/README.md#capability-policy).

## Quarantine and offline bypass

`lockdown` (from **POpsVision** → "Karantinaya Al", or `POST /api/security/lockdown`):

- the tray shows a full-screen lock screen that blocks the Windows, Tab, Esc and F4 keys,
- the service adds Windows Firewall block rules (group `POps Isolation`) for every address except the POps server,
  the DNS and DHCP servers, loopback and IPv6 link-local/multicast, and switches on all firewall profiles. Their
  previous state is saved in `C:\POpsData\secure\isolation.json`.

`unlock` removes the lock screen and the rules and restores the firewall profiles.

**Offline bypass.** If a quarantined PC cannot reach the server, an admin can get the day's code for it with the
key button on **Cihaz Yönetimi** (`GET /api/security/bypass_token/{pc}`; every request is logged). The user enters
it in the tray menu **Yönetici Müdahalesi (Bypass)**. A valid code removes the network isolation. The code is the
first 6 hex characters of SHA-256(`hw_id` + `BYPASS_SECRET` + date), so the agent's `BypassSecret` must equal the
server's `BYPASS_SECRET` and both must use the same local date. After 5 wrong codes the bypass locks for 15
minutes, doubling up to 24 hours.

## Policies

- **Fair-use notice.** When the policy has a `fair_use_text`, the tray shows it in a window titled
  "Kurumsal Adil Kullanım Politikası" that the user closes with "Okudum, Anladım ve Kabul Ediyorum".
- **DNS policy.** The agent contains DNS-cache matching against the policy's `dns_domains` (exact domain or
  subdomain, per active category), `policy_alert` reporting and the `auto_quarantine` threshold. In this version
  the service does not start that monitoring loop, so no DNS violations are reported and no automatic quarantine
  happens.

## Updates

Updates are signed MSI packages; the agent installs nothing unsigned.

1. A superadmin stages a release on **Sistem & Sürüm** (download from GitHub, or upload `manifest.json`,
   `manifest.json.sig` and the MSI) and sends it to all agents, a lab or selected PCs. Only online agents receive
   it; send it again for the others later.
2. The agent verifies the manifest's ed25519 signature with the public key compiled into it, refuses a version
   that is not newer than its own, downloads the MSI from `<ServerUrl>/updates/<name>` and checks its size and
   SHA-256.
3. `POpsUpdater` installs it, waits up to 90 seconds for the new version to report healthy
   (`C:\POpsData\health.json`), and otherwise rolls back to the previous MSI.
4. The result (`success`, `pending_reboot`, `rolled_back`, `rollback_failed`, `install_failed`, `rejected`, …) is
   written to `C:\POpsData\update-result.json` and reported to the server, which records it in the audit log and
   shows it in the panel.

The outcomes and the rollback drill are described in [`Agent/README.md`](../Agent/README.md#updates). Agents
older than 0.1.3-alpha cannot apply signed updates and must be reinstalled once with the MSI.

## Files and logs

| Path | Contents |
| --- | --- |
| `C:\Program Files\POps\` | Programs and `appsettings.json` (`ServerUrl`, `PersistDir`; SYSTEM and Administrators only). |
| `C:\POpsData\identity.key` | Hardware ID. |
| `C:\POpsData\secure\` | `agent.secret`, `enroll.token`, `bypass.secret`, `capabilities.json`, `isolation.json` (SYSTEM and Administrators only). |
| `C:\POpsData\health.json`, `update.lock`, `update-result.json` | Update state. |
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
