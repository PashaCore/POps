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
that partly changed (no decision taken), 1080 a change of the server's modules, 1090 a configuration that could not
be read and 1100 a clipboard shared in a Vision session (direction and length only). Failure to write an event does not stop the
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

**2. What the server does** (`Backend/pops/peer_cache.py`), so the agent knows what to expect:

- When the update setting "Sınıf içinde eşten dağıt" is on (the default), for every lab with at least two online
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
| `C:\Program Files\POps\` | Programs and `appsettings.json` (`ServerUrl`, `PersistDir`; SYSTEM and Administrators only). |
| `C:\POpsData\identity.key` | Hardware ID. |
| `C:\POpsData\secure\` | `agent.secret`, `enroll.token`, `bypass.secret`, `capabilities.json`, `isolation.json`, `lockdown.json`, `bypass-state.json`, `hw.bind`, `clone-<time>\` (SYSTEM and Administrators only). |
| `C:\POpsData\health.json`, `update.lock`, `update-result.json`, `update-progress.json` | Update state. |
| `C:\POpsData\session.json`, `patch-scan.json` | Last reported sign-in; time of the last Windows Update scan and a report not yet delivered (0.1.5-alpha on). |
| `C:\POpsData\software-inventory.json` | Last software inventory sent: SHA-256 of the sorted list, device ID and time (0.1.15-alpha on). |
| `C:\POpsData\packages\installed.msi`, `updates\`, `updater\` | Rollback package, downloaded update, updater copy. |
| `C:\POpsLogs\POps_<yyyyMMdd>.log`, `msi-*.log` | Service and updater log, and the updater's msiexec logs (SYSTEM and Administrators only). At start and once a day the service deletes these logs when they are older than 30 days, and the oldest ones while the folder holds more than 200 MB; today's log is never deleted. |
| `%LOCALAPPDATA%\POps\Logs\` | Per-user logs: `POpsWatchdog_<yyyyMMdd>.log` and the tray's `TrayLog.txt` (message types only, rotated at 1 MB). |

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
