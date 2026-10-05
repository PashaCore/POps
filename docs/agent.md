# Windows Agent

The POps agent runs on each managed Windows PC. It keeps a connection to the server, reports status and
inventory, runs the commands the server queues, and provides screen view and remote control through the tray.
It is written in C# for .NET 10 and published self-contained: the .NET runtime is installed with the agent.

This page is an overview. The detailed references are:

- [`Installer/README.md`](../Installer/README.md): the MSI, its properties, upgrades and migration from older installs,
- [`Agent/README.md`](../Agent/README.md): settings, secrets, server authentication, local hardening, the capability
  policy, the update and rollback procedure, and the unit tests.

PCs running Pardus or Debian use the [Linux agent](#linux-agent-pardus-and-debian) (first version: inventory and
remote commands).

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
- If `appsettings.json` cannot be read (broken JSON, access denied) or no valid `ServerUrl` is found, the agent
  logs an error, writes event 1090 and the tray shows "POps - yapılandırma okunamadı" with a warning icon, instead
  of looking healthy. Without a valid address it falls back to `http://127.0.0.1:8000`, which only reaches a server
  on the same PC.
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
- **Features.** The command connection also carries `X-Agent-Features: exam,files,winget,power,message`, the
  optional server actions this agent implements. The server sends `exam_mode`, file transfers, `winget_install`,
  `power` and `user_message` only to agents that list them; older agents never receive them and older servers ignore
  the header.
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
| `winget_install` | Installs a winget package as LocalSystem; see [below](#winget_install-contract). Sent only to agents that announce `winget` in `X-Agent-Features`. Agents without it never receive it (the server marks the task `Denied` instead). |
| `cancel_task` | Stops a running `execute` (exit code -2), a `power` countdown or a `user_message` that waits for its click. |
| `power` / `user_message` | Shut down, restart, sign out or lock with a tray countdown and note; show a message to the signed-in user. See [below](#power-and-user_message-contract). Sent only to agents that announce `power` / `message` in `X-Agent-Features`. |
| `get_hardware` | Posts the hardware inventory. |
| `start_vision_session` | Passes the session request to the tray (consent dialog or mandatory countdown). |
| `stop_stream` | Stops screen capture and closes the Vision connection. |
| `remote_input` | Screen preview (`get_thumbnail`), frame-rate change (`set_fps`) or mouse/keyboard input, subject to the Vision capability and, for input, an active session. Keyboard (from 0.1.14-alpha, `SendInput`): named keys (Enter, F1–F24, arrows, Home/End, …) become virtual keys, left/right modifiers from `code`; a single character is sent as Unicode (`KEYEVENTF_UNICODE`), so İ, ş, ğ, @ and € arrive as typed whatever the PC's layout; with Ctrl or Alt (not both, not AltGr) or Win the character becomes the key from `code` (Ctrl+C, Win+R). Keys still held when control ends or the service connection drops are released. |
| `lockdown` / `unlock` | Quarantine on / off (below). |
| `wake_peer` | Sends a Wake-on-LAN packet for another PC in the same lab. |
| `set_identity` | Replaces the stored hardware ID. |
| `set_secret` | Stores the device secret and deletes the enrollment token. |
| `server_info` | Sent by the server once the agent is registered; `features` containing `update_result_ack` / `result_ack` means the server confirms update results / task results (0.1.14-alpha); `winget` means the server may send `winget_install` and reads `X-Agent-Features`; `power` and `message` mean the same for `power` and `user_message`. Without `server_info` within 15 seconds of connecting the agent treats the server as older (same rule for both). |
| `result_ack` | The server stored the task result for `task_id`; the agent deletes it from `C:\POpsData\secure\pending-results.json`. |
| `update_result_ack` | The server stored the update result with this `result_id`; the agent sets `update-result.json` aside. |
| `set_bypass_secret` | Stores the per-device offline bypass key and acknowledges its fingerprint; accepted only on a device-secret command connection. |
| `set_capabilities` | Switches capabilities **off**: the server sends `terminal_enabled` / `vision_enabled`; the agent also applies `exam_enabled`, `files_enabled`, `power_enabled` and `message_enabled`. Requests to switch one on are ignored. |
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
that partly changed (no decision taken), 1080 a change of the server's modules, 1090 a configuration that could not
be read (or a `DataDirectory` / `LogDirectory` that was refused, the default folder is used), 1100 a
clipboard shared in a Vision session (direction and length only), 1110/1111/1112 exam mode
started, ended and an app closed during an exam, 1120/1121 a file pushed to or pulled from the PC, 1130 a
power action accepted (operation, delay, requester and task; not the note), 1140 a message shown to the user
(task, title and text length, style, whether a click is required, requester; never the title or text), 1141
the end of a message that waited for a click (`acknowledged`, `timeout`, `cancelled` or `service_stopping`) and
1150 a Vision session that started while the PC was locked (session, requester, mandatory; see
[`vision.md`](vision.md#a-session-that-starts-while-the-pc-is-locked)). Failure to write an event does not stop the
service.

### `winget_install` contract

Deploys a package from the winget community source (**Dağıtım** → winget paketi; server side added after
0.1.22-alpha). The agent part is not built yet; this is the contract it must follow. The message schema and test
vectors are in [`protocol/`](protocol/README.md) (`server-to-agent/winget_install.json`,
`examples/server-to-agent/winget_install*.json`, `examples/agent-to-server/result.winget*.json`). Why it is a separate action and
not an `execute` payload: [`decisions.md` D-23](decisions.md#d-23-winget-packages-as-their-own-agent-action).

**1. Announce the feature.** An agent that implements `winget_install` sends this header on the `/ws/agent`
connection, next to `X-Agent-Version`:

```
X-Agent-Features: winget
```

A comma-separated list of lowercase names (`[a-z0-9_]`, at most 32); unknown names are ignored. The server stores
it per connection (`agent_versions.features`), shows it on **Dağıtım** and sends `winget_install` only to an agent
whose current connection announced `winget`. An agent without the header never gets the message: the server marks
the task `Denied` with exit code -8 and does not send it. The server announces `winget` in `server_info.features`;
the agent does not need to wait for it.

**2. The message.**

```json
{"action": "winget_install", "task_id": 42, "id": "Mozilla.Firefox", "version": null, "requested_by": "admin"}
```

| Field | Type | Meaning |
| --- | --- | --- |
| `task_id` | integer | As in `execute`: one task, one `result`. |
| `id` | string | winget package identifier, matched exactly (`-e`). |
| `version` | string or `null` | A specific version, or `null` for the latest. |
| `requested_by` | string | Who queued it; for the local audit record, as in `execute`. |

**3. Checks before running** (refusals are a `result`, never a run):

- Capability and module: run only when the local terminal capability is on **and** the lab's `deploy` module is on
  (the policy's `modules.deploy`; `deploy` depends on `terminal` on the server). Otherwise answer `exit_code` -5,
  `output` starting with `[REDDEDİLDİ]` (for example `[REDDEDİLDİ] Bu cihazda uzaktan terminal kapalı (yetenek
  politikası); winget kurulumu yapılmadı.`), and send `capability_denied` with `"action": "winget_install"`,
  `"task_id"`, `"capability": "terminal"` (local capability off) or `"capability": "deploy"` with
  `"reason": "module_disabled"` (module off). Installing software as SYSTEM is software deployment, so it follows
  the same local lock as package deployment through `execute`.
- Validate again, whatever the server sent: `id` must match `^[A-Za-z0-9][A-Za-z0-9.+_-]{1,127}$` and `version`
  (when not `null`) `^[0-9A-Za-z.+_-]{1,40}$`, as a whole-string match (no trailing newline). Otherwise answer
  `exit_code` -5 and `[REDDEDİLDİ] Geçersiz winget paketi kimliği ya da sürümü; kurulmadı.`
- The same `task_id` already running: ignore the repeat, as for `execute`.

**4. Find winget.** `winget.exe` is an app execution alias in the signed-in user's profile and is not on the
SYSTEM account's `PATH`. Use the newest
`%ProgramFiles%\WindowsApps\Microsoft.DesktopAppInstaller_<version>_<arch>__8wekyb3d8bbwe\winget.exe` (x64 or
arm64 to match the OS), and only from that folder. When none exists, answer `exit_code` -7 and
`[REDDEDİLDİ] winget bu bilgisayarda yok` (the server marks the task `Denied`).

**5. Run.** Start `winget.exe` directly, **without a shell** (`UseShellExecute = false`, no `cmd.exe`, no
`.bat`, no string concatenation into a command line), passing each argument as its own entry of
`ProcessStartInfo.ArgumentList`, in this order:

```
install --id <id> -e --silent --scope machine --accept-package-agreements --accept-source-agreements --disable-interactivity [--version <version>]
```

Same limits as `execute`: 30-minute time limit (-1), `cancel_task` ends it and its child processes (-2), service
stop (-4), the same output cap. Capture stdout and stderr; drop the progress-bar and spinner lines winget draws
with carriage returns so the output stays readable. Write the start and finish to the local event log like a
command (the package id and version may be recorded in clear).

**6. Answer** with the normal result, kept until `result_ack` like any task result:

```json
{"type": "result", "pc_name": "HW-…", "task_id": 42, "output": "…", "exit_code": -1978335135}
```

`exit_code` is winget's own exit code as a signed 32-bit integer (winget returns HRESULTs, for example
`0x8A150061` = -1978335135). The server reads it like this:

| Exit code | Task status | Meaning |
| --- | --- | --- |
| `0` | Completed | Installed. |
| -1978335189 (`0x8A15002B`), -1978335135 (`0x8A150061`) | Completed | Already installed and up to date. |
| -1978334967 (`0x8A150109`), -1978334965 (`0x8A15010B`) | Completed | Installed; a restart finishes it. |
| -5 with `[REDDEDİLDİ] …` | Denied | Refused by the capability policy, the module or validation. |
| -7 with `[REDDEDİLDİ] winget bu bilgisayarda yok` | Denied | No winget on the PC. |
| -1 / -2 / -3 / -4 | Failed | Time limit, cancelled, agent error, service stopping (as for `execute`). |
| anything else | Failed | The panel names common winget codes (package not found, installer hash mismatch, app in use, another install running, disk full, blocked by policy …). |

-8 is set by the server only (agent without the feature).

### `power` and `user_message` contract

Power actions (**Kapat**, **Yeniden başlat**, **Oturumu kapat**, **Kilitle**) and **Mesaj gönder** used to be
`execute` commands (`shutdown /s /f /t 5`, `msg *`). From the server version after 0.1.22-alpha they are their own
messages. Schemas and test vectors: [`protocol/server-to-agent/power.json`](protocol/server-to-agent/power.json),
[`user_message.json`](protocol/server-to-agent/user_message.json), `examples/server-to-agent/power*.json`,
`examples/server-to-agent/user_message.json`, `examples/agent-to-server/result.power.json`,
`result.no_session.json`, `result.user_message.json`, `capability_denied.power.json`, `capability_denied.message.json`.

**1. Announce the features.** An agent that implements them sends, on the `/ws/agent` connection (together with
any other feature, comma-separated):

```
X-Agent-Features: power,message
```

The server stores the list per connection (`agent_versions.features`, `agent_features` in `/api/devices`) and sends
`power` only to an agent that announced `power`, `user_message` only to one that announced `message`. The server
lists `power` and `message` in `server_info.features`.

**2. `power`.**

```json
{"action": "power", "task_id": 61, "op": "shutdown", "delay": 300, "message": "Ders bitti; kaydedin.", "requested_by": "ogretmen"}
```

| Field | Type | Meaning |
| --- | --- | --- |
| `op` | `shutdown` \| `restart` \| `logoff` \| `lock` | Forced power off, forced restart, sign out the interactive user, lock the interactive session. |
| `delay` | integer 0–600 | Seconds before acting. |
| `message` | string ≤ 200 or null | Note for the user, one line; the server has removed control characters, line breaks and bidirectional formatting characters. |
| `requested_by` | string | Panel user or API token; write it to the local audit log as for `execute`. |

- Local capability `power` (on by default, can be switched off locally like terminal and Vision): when it is off,
  answer `result` with `exit_code` -5 and `[REDDEDİLDİ] …`, then `capability_denied` with `"capability": "power"`,
  `"action": "power"` and the `task_id`.
- `logoff` and `lock` with nobody signed in: `result` with `exit_code` -6 and `[REDDEDİLDİ] oturum açık kullanıcı yok`.
- Otherwise show a tray countdown of `delay` seconds with `message`, send `result` with `exit_code` 0 and an output
  that starts with `[TAMAM]` (for example `[TAMAM] 300 saniye sonra kapanıyor`) just before acting, then act. The
  result must leave before a shutdown or restart, so the task does not stay `Running`.

**3. `user_message`.**

```json
{"action": "user_message", "task_id": 63, "title": "Sınav başlıyor", "text": "Kaydedin.\nSınav 10 dakika sonra.", "style": "warning", "requires_ack": true, "requested_by": "ogretmen"}
```

| Field | Type | Meaning |
| --- | --- | --- |
| `title` | string 1–80 | One line. |
| `text` | string 1–1000 | May contain `\n` line breaks (never more than one empty line in a row); no other control characters. |
| `style` | `info` \| `warning` | Bilgi or Uyarı look. |
| `requires_ack` | boolean | Keep it on screen until the user clicks Tamam. |
| `requested_by` | string | Always a panel user (API tokens cannot send messages). |

- Local capability `message` (on by default): off → `result` -5 and `capability_denied` with
  `"capability": "message"`, `"action": "user_message"`.
- Nobody signed in: `result` -6 `[REDDEDİLDİ] oturum açık kullanıcı yok`.
- Shown: `result` 0 `[TAMAM] gösterildi` right away; with `requires_ack`, `[TAMAM] okundu` when the user clicks Tamam,
  or `[TAMAM] gösterildi, onaylanmadı` after 30 minutes without a click. The server does not hold the device's task
  queue while a message waits for its acknowledgement.
- Title and text are plain text: never HTML, never a shell argument.

**4. Results.**

| Exit code | Task status | Meaning |
| --- | --- | --- |
| `0` with `[TAMAM] …` | Completed | Done (or shown / read). |
| -5 with `[REDDEDİLDİ] …` | Denied | Local capability off. |
| -6 with `[REDDEDİLDİ] oturum açık kullanıcı yok` | Denied | Nobody signed in (logoff, lock, message). |
| -8 | Denied | Set by the server only: the agent did not announce the feature and there is no fallback; nothing was sent. |

On the wire -6 now means "nobody signed in"; the Windows agent's internal duplicate code -6 is never sent.

**5. Agents without the features.** The server sends `shutdown` and `restart` as the old `execute` command,
`shutdown /s|/r /f /t <max(delay, 5)>`, on Windows with `/c "<note>"` when the note has characters left after keeping
only letters, digits, spaces and `. , : ; ? ' ( ) -` (safe inside the quoted `.bat` line). Linux agents
(`clients.platform = 'linux'`) get exactly `shutdown /s|/r /f /t N`, which they map to `systemctl poweroff|reboot`.
`logoff`, `lock` and messages are not sent: the task becomes `Denied` with -8 ("Bu bilgisayardaki ajan bunu
desteklemiyor …"). The old command is an `execute`, so it still needs the terminal capability and the lab's
`terminal` module; `power` and `user_message` themselves do not depend on that module.

## Capability policy

Terminal (`execute`), Vision (streaming, previews, remote input), exam mode, file transfer, power actions
(`power`) and user messages (`user_message`) can be disabled per PC, so that even a compromised server cannot use
them there. The MSI sets them (`TERMINAL_ENABLED`, `VISION_ENABLED`, `EXAM_ENABLED`, `FILES_ENABLED`,
`POWER_ENABLED`, `MESSAGE_ENABLED`, `1` / `0`; all on by default);
the server can only switch them off (**Sistem** → "Cihaz yetenekleri"). A refused command is closed with
a `[REDDEDİLDİ]` result and reported as `capability_denied`. Re-enabling needs a local administrator: MSI repair or
reinstall with `…_ENABLED=1`. The state is in `C:\POpsData\secure\capabilities.json`; a capability missing from an
older file counts as on. The `capabilities` message reports all six (`terminal_enabled`, `vision_enabled`,
`files_enabled`, `exam_enabled`, `power_enabled`, `message_enabled`) with `server_ca`; the last three are optional
in the schema, so older agents that do not send them stay valid. See
[`Agent/README.md`](../Agent/README.md#capability-policy).

## File transfer

From 0.1.23-alpha a server that lists `file_transfer` in `server_info.features` can send files to a PC and fetch
files from it. The messages are the server's (`docs/protocol/server-to-agent/file_push.json`, `file_pull.json`,
`docs/protocol/agent-to-server/file_result.json`). The agent announces `files` in `X-Agent-Features` and reports
`files_enabled` in `capabilities` (the server sends file commands only to agents that report it).

Capability `files_enabled`: on by default, `FILES_ENABLED=0` switches it off locally, and the server can only
switch it off. With it off, a request is answered with one `capability_denied` (`capability: files`,
`action: file_push` or `file_pull`, `transfer_id`) and no `file_result`: the server marks the transfer `rejected`.
Each transfer is announced in the tray and written to the event log.

- **Transfer ID:** `transfer_id` must match `^[A-Za-z0-9_-]{8,64}$`. A request without a valid ID is only logged
  locally: no `file_result` (the server ignores unknown transfers). With the capability off, the
  `capability_denied` is still sent, without `transfer_id`.
- **Push** (admin → PC):

  ```json
  {"action": "file_push", "transfer_id", "name", "size", "sha256", "url", "dest": "public_desktop" | "inbox", "reason", "allow_exec"}
  ```

  - **Source:** The agent downloads only from its own server. `url` must have the server's form
    `/api/files/<id>/download?t=<token>`; it is appended to `ServerUrl` like every other agent request (the token
    is kept). The request goes without redirects and with the device key (`X-Agent-Id`, `X-Agent-Secret`).
  - **Checks:** The size (at most 1 GB; the server sends at most 200 MB) and the SHA-256 are verified. A reason
    of at least 3 characters is required.
  - **Destinations:** `public_desktop` (`C:\Users\Public\Desktop`) or `inbox` (`C:\POpsData\inbox\<yyyy-MM-dd>`,
    readable by Users, full control for SYSTEM and Administrators). No other path is possible.
  - **File name:** No path separators, no `:` (alternate data streams), no wildcards or control characters, no
    reserved names (`CON`, `COM1`, …). Trailing dots and spaces are removed, and a clash gets " (2)".
  - **Executable-like files:** `.lnk`, `.url` and `.scr` only with `allow_exec: true`.
  - **Download:** The file goes to a temporary file in the protected folder and is copied into place only when
    it matches, so it takes the destination's permissions.
  - **PC user:** The tray says "Yönetici bir dosya gönderdi: <ad>". Event 1120 records the ID, path, size, SHA-256
    and reason.
- **Pull** (PC → admin):

  ```json
  {"action": "file_pull", "transfer_id", "path", "max_size", "upload", "reason", "any_profile"}
  ```

  - **Request:** A reason of at least 3 characters is required. The path must start with a drive letter
    (`C:\…`), have at most 1024 characters and contain no wildcards and no `:` after the drive (no network,
    device or alternate-data-stream paths).
  - **Real path:** The checks use the file's real path, with symbolic links and junctions resolved
    (`GetFinalPathNameByHandle`).
  - **Refused:**
    - anything under `C:\POpsData\secure`;
    - another user's profile, unless `any_profile: true`. Allowed without it are the profile of the user signed
      in at the console and `Public`; with nobody signed in, every profile counts as another user's;
    - files larger than `max_size` (at most 1 GB).
  - **Upload:** `POST` to `upload` (form `/api/files/<id>/upload?t=<token>`, same rule as `url`), the raw file as
    `application/octet-stream`, with the device key. The optional `X-Content-SHA256` header is not sent.
  - **PC user:** The tray says "Yönetici bu dosyayı aldı: <yol>". Event 1121 records the ID, path, size and
    reason.
- **Result:** `{"type": "file_result", "transfer_id", "outcome": "done" | "rejected" | "failed", "path", "detail"}`.
  The field is `outcome`, not `status`: a server that does not know `file_result` would take a message with
  `status` for a heartbeat. `path` is left out when it is longer than 1024 characters; `detail` is cut at 300.
  A request that breaks a rule (type, path, profile, size, reason) is `rejected`; a download, checksum, disk or
  upload error is `failed`.

## Exam mode

From 0.1.23-alpha a server that lists `exam_mode` in `server_info.features` can put a lab into exam mode. The
messages are the server's (`docs/protocol/server-to-agent/exam_mode.json`,
`docs/protocol/agent-to-server/exam_state.json`):

```json
{"action": "exam_mode", "enabled": true, "allow": ["sinav.meb.gov.tr", "10.0.0.5", "10.1.0.0/24"],
 "until": 1791207689, "message": "Sınav modu: yalnızca sınav sitesi açık", "block_apps": ["cmd.exe", "powershell.exe"]}
```

`{"action": "exam_mode", "enabled": false}` ends it. The agent announces `exam` in `X-Agent-Features` when it
connects.

- **Network:** The PC can reach only the POps server, DNS/DHCP and the allow list (host names, IPv4/IPv6
  addresses, CIDR ranges from /8 for IPv4 and /16 for IPv6).
  - It uses the quarantine isolation engine with its own firewall rule group (`POps Exam`). Exam mode and
    quarantine are independent; if both run, quarantine (the stricter one) wins.
  - Firewall profiles that were off before are turned off again only when neither group is active.
  - Host names are resolved again every 2 minutes and whenever a network address changes.
- **Validation:** At most 50 allow entries and 50 apps (the server's limits); a message of at most 300 characters
  (control characters removed; the server sends at most 200); `until` must be in the future. Processes that would
  break the session or POps (explorer, svchost, winlogon, the POps programs, …) are never closed. A command that is
  refused or cannot be applied is answered with `exam_state` showing the unchanged state; the reason is only in
  the local log.
- **PC user:** The tray shows a red banner at the top of the primary screen with the message and the end time.
  It cannot be closed. The listed apps are closed in user sessions every 2 seconds, and the tray says so.
- **End:** Exam mode ends at `until` even when the server cannot be reached. It survives a restart (state in
  `C:\POpsData\secure\exam.json`).
- **Reporting:** `{"type": "exam_state", "enabled", "since", "until"}`, no other field.
  - `since` is when the current state began: the entry time while in exam mode; when not in exam mode, the time
    it was left if that happened since the service started, otherwise absent. `until` is the exam's end while in
    exam mode and `null` otherwise.
  - Sent as the answer to every `exam_mode`, after every change (end at `until`, capability switched off) and
    once on every connection after `server_info`, also when no exam runs.
  - Only to servers that list `exam_mode` in `server_info.features` (an answer to `exam_mode` always goes back),
    never as the first message of a connection. A change while the server is unknown or unreachable is reported
    after the next `server_info`.
- **Capability:** `exam_enabled` in `capabilities.json`, on by default; `EXAM_ENABLED=0` switches it off locally.
  The agent also applies `exam_enabled: false` from `set_capabilities` (and ignores `true`) and reports it as
  `exam_enabled` in the `capabilities` message. With it off, `exam_mode` with `enabled: true`
  is answered with one `capability_denied` (`capability: exam`, `action: exam_mode`) and nothing is applied. If it
  is switched off while exam mode runs (reinstall with `EXAM_ENABLED=0`, or `set_capabilities`), the agent leaves
  exam mode within 2 seconds and sends `exam_state` with `enabled: false`.
- **Events:** 1110 exam started (allow list, end time, apps), 1111 ended (`server`, `until` or `capability`),
  1112 app closed (once per app and exam).

## Power actions and user messages

How the Windows agent implements the [`power` and `user_message` contract](#power-and-user_message-contract). It
announces `power` and `message` in `X-Agent-Features`, and the server sends the two actions only to agents that do.
Each task gets exactly one `result`, kept until `result_ack` like the result of `execute`. The agent checks every
field again against the server's schemas ([`power.json`](protocol/server-to-agent/power.json),
[`user_message.json`](protocol/server-to-agent/user_message.json)).

**Text.** The server cleans the note, title and text before sending them; the agent repeats the same cleaning, so a
message that bypassed the server still reaches the screen as plain text:

- `\r\n` and `\r` become `\n`; U+2028 and U+2029 count as line breaks too;
- control characters other than `\n` (C0, DEL, C1) are removed, a tab becomes a space;
- text-direction controls (U+200E, U+200F, U+202A–U+202E, U+2066–U+2069), zero-width and invisible format characters
  (U+200B–U+200D, U+2060–U+2065) and U+FEFF are removed;
- the power note and the title are one line: every line break becomes a space;
- the message text keeps its line breaks, without spaces at the end of a line and with at most one empty line in a
  row;
- leading and trailing white space is removed.

Lengths are counted in Unicode characters, as JSON Schema and the server do (an emoji is one character), on the value
that arrived.

**`power`** (`op` `shutdown`, `restart`, `logoff` or `lock`; `delay` 0–600 seconds; `message` up to 200 characters or
`null`):

- **Checks, in this order.** No integer `task_id`: ignored (nothing to report to). The `power` capability is off:
  exit code -5, `[REDDEDİLDİ] Bu cihazda uzaktan güç işlemleri kapalı …` and `capability_denied`. A field is invalid
  (`op` missing or unknown, `delay` missing, `null` or not an integer from 0 to 600, `message` too long or neither
  text nor `null`): -5, `[REDDEDİLDİ] Geçersiz güç isteği: …`, without `capability_denied`. `logoff` or `lock` while
  nobody is signed in at the console: -6, `[REDDEDİLDİ] oturum açık kullanıcı yok`. Otherwise the request is accepted
  and event 1130 is written. A `message` that is missing, `null` or empty once cleaned means no note.
- **Countdown.** With a delay the tray shows a window on top of the others: "Bilgisayar 60 sn içinde yeniden
  başlatılacak" (… kapatılacak, "Oturumunuz … kapatılacak", … kilitlenecek), the note, and "Açık çalışmalarınızı
  şimdi kaydedin; kaydedilmemiş değişiklikler kaybolur." (for a lock: "Kilit açıldığında programlarınız açık
  kalır."). It does not take the keyboard focus, and the user may close it: the action happens anyway. The service
  owns the timer, so the action also happens without a tray; a tray that connects during the countdown shows the
  remaining seconds. With `delay` 0 the agent acts at once, without a window.
- **Acting.** When the time is up the agent sends the result first (0, `[TAMAM] Bilgisayar kapatılıyor.` /
  `… yeniden başlatılıyor.` / `[TAMAM] Kullanıcının oturumu kapatılıyor.` / `[TAMAM] Bilgisayar kilitleniyor.`) and
  then acts; after a shutdown or restart the server receives it on the next connection if it did not arrive before.
  A `logoff` or `lock` whose user signed out during the countdown ends with -6 instead. An action that fails after
  the result was sent is only logged (`[HATA] Güç işlemi uygulanamadı`).
  - Shutdown and restart: `%SystemRoot%\System32\shutdown.exe /s` or `/r` with `/t 0 /f /d p:0:0`. Running programs
    are closed without asking (lab PCs must not hang on a "save changes?" dialog); the countdown and the note are the
    warning.
  - Sign-out: `WTSLogoffSession` on the console session, from the service.
  - Lock: a service running as LocalSystem cannot lock the user's desktop, so the tray calls `LockWorkStation` when it
    runs in the console session; the session stays the console session and the user unlocks as usual. When the tray
    is not connected, runs in another session (Remote Desktop) or does not confirm within 5 seconds, the service
    disconnects the console session (`WTSDisconnectSession`): programs keep running and Windows shows the sign-in
    screen, but until the user signs in again the PC reports nobody at the console.
- **One countdown at a time.** The same `task_id` again is ignored, as for `execute`. A newer `power` replaces the
  running countdown; the older task ends with -2. `cancel_task` with the task's ID stops the countdown (-2,
  `[İPTAL EDİLDİ]: Güç işlemi panelden iptal edildi; bilgisayara dokunulmadı.`); the tray closes the window and shows
  "Güç işlemi iptal edildi". `set_capabilities` with `power_enabled` `false` stops it the same way. If the service
  stops during the countdown the result is -4.

**`user_message`** (`title` 1–80 characters, `text` 1–1000, `style` `info` or `warning`, `requires_ack`; all four are
required):

- **Checks.** The same order: integer `task_id`, the `message` capability (-5 and `capability_denied`), the fields
  (-5, `[REDDEDİLDİ] Geçersiz mesaj: …`; a title or text that is empty once cleaned is invalid too). Only the tray in
  the user's session can show a message:
  - no tray connected and nobody signed in at the console: -6, `[REDDEDİLDİ] oturum açık kullanıcı yok`;
  - no tray connected but someone signed in: -3, `[HATA]: POps tepsisi bu bilgisayarda çalışmıyor; mesaj
    gösterilmedi.` (the server marks the task `Failed`; -6 on the wire means only that nobody is signed in).

  A connected tray counts as a user who can read it, so a message also reaches a Remote Desktop user. Messages are
  not queued for later.
- **Window.** On top of the others, without taking the keyboard focus: the title, the text with its line breaks and
  an information or warning icon. It has no default button, so Enter typed in another program cannot acknowledge it.
- **Result.** Without `requires_ack` the window has a "Kapat" button and the result is sent at once: `[TAMAM]
  gösterildi`. With `requires_ack` it has only "Tamam" (with the hint "Okuduğunuzu bildirmek için Tamam'a basın.";
  Alt+F4 does not close it) and the result waits: `[TAMAM] okundu` when the user clicks, `[TAMAM] gösterildi,
  onaylanmadı` after 30 minutes or when the service stops. There is no earlier `[TAMAM] gösterildi` for such a
  message: the server keeps only the first result of a task. While it waits, a tray that reconnects shows it again,
  the same `task_id` is ignored and `cancel_task` closes the window (-2).
- **Privacy.** Event 1140 records the task, the lengths of title and text, style, `requires_ack` and the requester;
  1141 how a message that waited ended. Neither the event log nor the agent's logs contain the title or text.

Exit code -6 is also `CommandRunner.ExitDuplicate` (a repeated `execute`), which the agent never sends; for `power`
and `user_message` it is sent and means that nobody is signed in.

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
  | `deploy` | `winget_install` is not run (agents that implement it; see [below](#winget_install-contract)). |
  | `vision` | `start_stream`, `start_vision_session`, the Vision tunnel, previews and remote input are refused with `capability_denied` (`module_disabled`). A Vision session that is open when the module closes is ended. |
  | `helpdesk` | The tray hides "Sorun bildir" and "Taleplerim" (`HELPDESK_MENU:0` over the pipe) and shows them again when the module opens. Requests that still arrive get "Yardım masası … kapalı", and new replies are not polled. |
  | `software` | The software inventory is not collected or sent. When the module opens, the last-send record is forgotten, so the list goes out in the next 6-hour round even if it did not change (the server did not store it while the module was off). |
  | `patches` | The daily Windows Update scan and its report are skipped. `scan_updates` and `install_updates` are refused (`capability_denied`, `module_disabled`). A report finished while the module was off is dropped. |
  | `wol` | `wake_peer` (waking another PC in the lab) is refused (`capability_denied`, `module_disabled`). |
  | `dns_policy`, `quarantine` | Nothing extra on the agent. The server sends an empty DNS list and `auto_quarantine: false`. Unlock, offline bypass and the lock screen work as before. |

- **Server only.** `schedules`, `licenses` and `reports` are enforced by the server; `deploy` too, and by agents
  that implement `winget_install`.
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
restart, and the lock screen and the tray check the code format first so a typo does not use up an attempt. The
service keeps the tray pipe open while the server is unreachable, so a code typed on the lock screen reaches it; if
it cannot, the lock screen says so.

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
   agent downloads directly as before. See [`design/peer-cache.md`](design/peer-cache.md). With the lab-local peer
   cache (below) the server sends `update_agent` to one PC per lab first and to the rest with `peers`.
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

While an update runs, the agent reports each stage with `update_progress` to servers that list the feature:
received, downloaded, verified, updater started, waiting for a busy Windows Installer (up to 5 tries), installing,
or the reason it refused the update. The updater writes its stages to `C:\POpsData\update-progress.json` and the
service forwards them. **Sistem → Güncellemeler** shows them per PC
([`api.md`](api.md#update_progress-agent-update-stages)).

The outcomes and the rollback drill are described in [`Agent/README.md`](../Agent/README.md#updates). Agents
older than 0.1.3-alpha cannot apply signed updates and must be reinstalled once with the MSI.

### Peer cache contract

Lab-local peer cache for update packages ([`design/peer-cache.md`](design/peer-cache.md), option A). The server
side is built (after 0.1.22-alpha); the agent part is not built yet, and this is the contract it must follow. The
message schema and a test vector are in [`protocol/`](protocol/README.md) (`server-to-agent/update_agent.json`,
`examples/server-to-agent/update_agent.peers.json`).

**1. Announce the feature.** On the `/ws/agent` connection, next to `X-Agent-Version`:

```
X-Agent-Features: peer_cache
X-Agent-Peer-Cache: port=8817; ip=10.20.0.12; link=wired
```

- `peer_cache` in `X-Agent-Features` (comma-separated with the other features, for example `winget,peer_cache`)
  is what makes the server stage updates for this PC and send it `peers`. Without it the PC is updated as today.
- `X-Agent-Peer-Cache` is optional and every part of it is optional, in any order, separated by `;`:
  - `port`: the TCP port of the cache server, 1024–65535. Default **8817**.
  - `ip`: the IPv4 address other PCs in the lab should use, a private address (10/8, 172.16/12, 192.168/16).
    Send the address of the interface that routes to the POps server. Without it the server uses the address
    the agent reported in its hardware inventory (`ip_address`), and only as a last resort the address the
    connection came from (only when it is private and no other PC shares it, that is no NAT in between). A PC
    with no usable address is never a seed or a peer.
  - `link`: `wired` or `wireless`. Wired PCs are preferred as seeds.
- The server announces `peer_cache` in `server_info.features`. Against a server that does not, the agent need not
  keep a cache or listen.
- **Keep a cache and listen only when told.** The server adds `"peer_cache": true` to `update_agent` only while its
  setting "Sınıf içinde eşten dağıt" is on (it is **off by default**) and only for PCs that take part in a staged
  rollout. Without that field the agent keeps no package in `C:\POpsData\cache`, starts no cache server and adds
  no firewall rule, even if it announced the feature. This keeps "no inbound ports on PCs" true unless an admin turns
  the peer cache on.

**2. What the server does** (`Backend/pops/peer_cache.py`), so the agent knows what to expect:

- When the update setting "Sınıf içinde eşten dağıt" is on (it is off by default), for every lab with at least two online
  target PCs that announce the feature, the server picks one **seed** (online, feature, a usable address, not
  already on the target version; wired first, then the most recently seen). The seed gets a normal
  `update_agent` (no `peers`). The other feature PCs in that lab wait.
- The seed downloads from the server, verifies, installs and restarts as today. When it reports
  `update_result` with `status: "success"` for that version **on its new connection**, the server sends
  `update_agent` to the waiting PCs with `peers`. The server waits for the result and not for `verified` because
  the seed's service is stopped while `POpsUpdater` installs, so its cache server is down at exactly that time.
- Stages the server listens to: `update_progress` `verified` from the seed (shown in the panel; it restarts the
  seed's time limit for the install), `update_progress` `rejected` and any `update_result` other than `success`
  (the seed failed: the next candidate becomes the seed), and `update_result` `success` (the PC holds the package
  and becomes a peer). A seed that does not reach `verified` within 10 minutes, or a result within 10 minutes after
  `verified`, is replaced by the next candidate. After three seeds, or when no candidate is left, the waiting PCs get
  `update_agent` without `peers`.
- `peers` lists 1–3 PCs of the same lab that reported success for this package less than 110 minutes ago and are
  online with the feature: the seed first, the order rotated from PC to PC so that not every PC starts with the
  same peer. A later `update_agent` for the same version (a PC that was off comes online and is sent the update)
  gets `peers` at once, without a new seed.
- PCs without a lab, agents without the feature and labs with no candidate are updated as today, without `peers`.

**3. The message.** `update_agent` with an optional `peers` array:

```json
{"action": "update_agent", "manifest": "…", "manifest_sig": "…",
 "peers": [{"hw_id": "HW-LAB1-PC12", "url": "http://10.20.0.12:8817/pops-cache/4d638345…3beb"}]}
```

`url` is always `http://<IPv4>:<port>/pops-cache/<sha256>`, the SHA-256 of the agent MSI from the signed manifest in
lowercase hex. Agents without `peer_cache` ignore the field (the Windows agent reads `update_agent` by property name;
unknown fields are ignored).

**4. Downloading with peers.** Nothing about the signature check changes: verify the manifest first, exactly as
today; `peers` is read only after that.

- Ignore a peer whose `url` does not match the pattern above, whose path SHA-256 is not the manifest's MSI
  SHA-256, or whose host is not a private IPv4 address.
- Try the peers in the order given, one at a time: connect timeout 3 seconds; if no data arrives for 15 seconds, or
  the answer is anything but `200` with the expected `Content-Length`, go to the next peer. After the last peer,
  download from `<ServerUrl>/updates/<name>` as today (BITS or HttpClient).
- Check the size and SHA-256 against the signed manifest **whatever the source**. A mismatch from a peer deletes
  the file and moves on to the next source (log the peer's `hw_id`); it never fails the update and never installs.
- Report `update_progress` as today (`received`, `downloaded`, `verified`, …). Optionally put the source in
  `detail` of `downloaded` (for example `peer HW-LAB1-PC12` or `server`); the server stores it but does not depend
  on it.

**5. The seed's (and every peer's) cache.** After `verified`, keep the package for others:

- Store it as `C:\POpsData\cache\<sha256>` (the file name is the lowercase SHA-256, no extension; SYSTEM and
  Administrators only). Copy it there before `POpsUpdater` starts, so it survives the install.
- Keep it for **2 hours** after `verified`, or until the next update is verified, whichever comes first. Keep at
  most two packages; delete the oldest. Delete expired files at service start and every 10 minutes.
- **The cache server.** While at least one package is in the cache, the service listens on the port
  (`HttpListener`, `http://+:<port>/pops-cache/`) and answers only `GET` (and `HEAD`) for
  `/pops-cache/<sha256>` with a file it holds: `200`, `Content-Type: application/octet-stream`,
  `Content-Length`. Anything else is `404` (`405` for other methods); no directory listing, no other path, no
  query strings, no redirects. Read-only: it never writes, deletes or uploads anything because of a request.
- At most 4 transfers at a time. Further requests wait for a free slot for up to 60 seconds, then get `503`
  (a lab of 40 PCs must not fall back to the server because the seed was busy for a few seconds).
- It starts at service start when the cache holds a package (so that it is already listening when the new version
  sends its `update_result`) and stops when the cache becomes empty.
- Firewall: one inbound rule for the program, the TCP port, remote address `LocalSubnet` only, all profiles, added
  when the server starts and removed when it stops (and at service start if no package is cached).
- A PC that reports `success` for an update also keeps its package and serves it the same way: the server may
  list it as a peer for PCs updated later.
- Log start and stop of the cache server and each served transfer (peer address, SHA-256 prefix, bytes) to the
  local log; no event log entry per transfer.

## Files and logs

| Path | Contents |
| --- | --- |
| `C:\Program Files\POps\` | Programs and `appsettings.json` (`ServerUrl`, `PersistDir`, `DataDirectory`, `LogDirectory`; SYSTEM and Administrators only). |
| `C:\POpsData\identity.key` | Hardware ID. |
| `C:\POpsData\secure\` | `agent.secret`, `enroll.token`, `bypass.secret`, `capabilities.json`, `isolation.json`, `lockdown.json`, `bypass-state.json`, `hw.bind`, `clone-<time>\` (SYSTEM and Administrators only). |
| `C:\POpsData\health.json`, `update.lock`, `update-result.json`, `update-progress.json` | Update state. |
| `C:\POpsData\session.json`, `patch-scan.json` | Last reported sign-in; time of the last Windows Update scan and a report not yet delivered (0.1.5-alpha on). |
| `C:\POpsData\software-inventory.json` | Last software inventory sent: SHA-256 of the sorted list, device ID and time (0.1.15-alpha on). |
| `C:\POpsData\packages\installed.msi`, `updates\`, `updater\` | Rollback package, downloaded update, updater copy. |
| `C:\POpsLogs\POps_<yyyyMMdd>.log`, `msi-*.log` | Service and updater log, and the updater's msiexec logs (SYSTEM and Administrators only). At start and once a day the service deletes these logs when they are older than 30 days, and the oldest ones while the folder holds more than 200 MB; today's log is never deleted. |
| `%LOCALAPPDATA%\POps\Logs\` | Per-user logs: `POpsWatchdog_<yyyyMMdd>.log` and the tray's `TrayLog.txt` (message types only, rotated at 1 MB). |

`C:\POpsData` and `C:\POpsLogs` are the defaults. `DataDirectory` and `LogDirectory` in `appsettings.json` move them
(for example to `D:\POpsData`); the subfolders and files above keep their names and permissions inside the chosen
folder, the updater and the watchdog get the same folders from the service, and nothing is moved when the setting
changes. Rules and caveats: [`configuration.md`](configuration.md#log-and-data-folders).

Uninstalling removes the programs, the service, the Run entry and `appsettings.json`, but keeps `C:\POpsData`
(identity and secret) and `C:\POpsLogs`, so a reinstalled PC returns with the same identity.

## Building and testing

Build commands are in [`Installer/README.md`](../Installer/README.md#build-locally). Unit tests:

```
dotnet test Agent/POps.Tests/POps.Tests.csproj --configuration Release
```

The version of every component comes from the repository-root `VERSION` file (`Agent/Directory.Build.props`).

## Linux agent (Pardus and Debian)

A first version of the agent for Pardus 23 / Debian 12 and later (and Ubuntu 24.04) is in
[`Agent-Linux/`](../Agent-Linux/README.md). It is Python 3 on the distribution's own `python3-websockets` and
`python3-cryptography` packages ([D-22](decisions.md#d-22-the-linux-agent-is-python-3-on-the-distributions-own-packages)),
installed from a `.deb` that is part of every signed release. Install, configuration, files, limits, the update and
rollback steps and uninstalling are in [`Agent-Linux/README.md`](../Agent-Linux/README.md).

Same as the Windows agent:

- the `/ws/agent` protocol, enrollment token and per-device secret, `server_info` and the acknowledgements
  (`result_ack`, `update_result_ack`), backoff with jitter (at least 60 s after `4401`, 10 min after `4409`);
- TLS only (plain `http://` only to the same PC), optionally pinned to the school's CA (`SERVER_CA_CERT`, reported as
  `server_ca: custom`);
- the hardware DNA the server compares (`uuid`, `bios_sn`, `disk_sn`, `mac`, `ram_sn` from DMI, the root disk, the
  physical network card and SMBIOS memory records), clone detection after disk imaging, `pops-agent generalize`
  before taking an image;
- command limits (30 minutes, 512 K characters, exit code, cancel), results kept on disk until acknowledged, the
  capability policy with the `[REDDEDİLDİ]` result and exit code `-5`, lab modules;
- signed updates: the agent takes exactly `pops-agent_<version>_all.deb` from the signed manifest, downloads it from
  `/updates/`, and a transient systemd unit installs it and rolls back to the previous `.deb` when the new version
  does not report health within 90 seconds; the result is sent in the Windows `update_result` schema;
- a local audit log of admin actions (`/var/log/pops-agent/audit.log`, hash-chained and append-only).

Different on Linux:

| | Linux agent |
| --- | --- |
| Identification | Sends `X-Agent-Platform: linux`; the panel shows **Linux** (`clients.platform`, migration 0026). |
| Commands | `/bin/sh -c` as root in `/`, clean environment, no input. **Uzak komut** says so and does not offer the Windows-only quick commands. The panel's restart and shut-down commands (`shutdown /r|/s /f /t N`) run as `systemctl reboot|poweroff` after N seconds. |
| Inventory | Hardware from `/proc` and `/sys`; installed packages from `dpkg-query` (library and debug packages left out; `install_date` from the package's file list). No Windows Update data: the panel shows "—". |
| Signed-in user | From systemd-logind (active local session, graphical first). |
| Not in this version | Screen view and remote input, quarantine and the offline bypass (the device key is stored and acknowledged), tray, notices, help desk, DNS policy alerts. The server refuses to quarantine a Linux PC (`409`); the other requests are answered by the agent with `capability_denied` and reason `not_supported`, so the panel shows them as refused. |
| Files | `/etc/pops-agent/` (configuration, `0700`), `/var/lib/pops-agent/` (identity, secret, spool, update state, `0700`), `/var/log/pops-agent/` (log rotated daily and at 10 MB, 30 days; audit log; updater log). |

Tests: `python3 -m pytest Agent-Linux/tests` (no root needed); CI runs them on the distribution's packages, builds the
`.deb` twice to check it is reproducible, installs it with `dpkg`, and runs the agent against a backend over TLS.
