# POps Agent

Windows endpoint agent (.NET 8). Includes the main agent, tray application, remote vision service, and watchdog.

## Layout

| Folder | Contents |
| --- | --- |
| `POps.Agent` | The Windows service (`POpsAgent.exe`, LocalSystem) |
| `POpsTray` | Tray application in the user's session |
| `POpsVision` | Screen streaming in the user's session |
| `POpsWatchdog` | Restarts the service and the tray |
| `POpsUpdater` | Applies signed MSI updates and rolls them back |
| `POps.Shared` | `POps.Shared.dll`, helpers used by the service, updater, watchdog and Vision: version, log, `appsettings.json` lookup, hardware ID. Each program sets `POpsHelpers.Component` at start; that name picks the log location. |
| `POps.Tests` | xUnit tests (see below) |

## Tests

```
dotnet test Agent/POps.Tests/POps.Tests.csproj --configuration Release
```

The project targets `net8.0-windows` (agent and `POps.Shared`) and `net472` (the MSI custom actions in `Installer/agent/CustomActions`), and CI runs it as the `test-agent` job. Tests cover logic only; firewall, pipe and service behaviour is not tested. Everything runs in a temporary folder with the current user standing in for SYSTEM, so the tests need no administrator rights and never touch `C:\POps`, `C:\POpsData` or `C:\POpsLogs`. `TestData/manifest.json` and its `.sig` are the signed v0.1.3-alpha release manifest, used to check the embedded public key.

Install it with the MSI from the release (`POps-Agent-<version>-win-x64.msi`); properties, upgrades and migration from older installs are described in [`Installer/README.md`](../Installer/README.md).

## Configuration

The agent reads its settings from `appsettings.json` in its install folder first (for example `C:\Program Files (x86)\POps`), then from `C:\POps\appsettings.json`; for each setting the first non-empty value wins. On every start the service restricts both files to SYSTEM and Administrators (a protected ACL, so nothing is inherited from `Program Files`), reads the result back and logs an error if any other account can still open the file. No server address or secret is compiled into the binaries; each setting below can also be supplied as a system environment variable, which takes precedence over the file.

| `appsettings.json` key | Environment variable | Description |
| ---------------------- | -------------------- | ----------- |
| `ServerUrl`            | `POPS_SERVER_URL`    | POps backend URL, e.g. `https://pops.example.com`. Must be `https://`: over plain `ws://` anyone on the network could read the device secret and send commands the agent runs as SYSTEM, so with an `http://` address the agent does not connect at all and logs why. Plain `http://` is accepted only for a server on the same machine (`127.0.0.1`, `localhost`); that is also the fallback when unset. |
| `PersistDir`           | `POPS_PERSIST_DIR`   | Optional. A local NTFS folder that freeze software (Deep Freeze ThawSpace, a thawed drive, …) does not roll back. The device secret is mirrored there so it survives a reboot on a frozen machine. |

### Secrets

Secrets are never kept in `appsettings.json` or in environment variables, which every user on the machine can read. They live in `C:\POpsData\secure`, a folder with a protected ACL that grants only SYSTEM and Administrators: students can neither read nor list it. (`C:\POpsData` itself stays readable so the tray and watchdog can read `identity.key`.)

| File in `C:\POpsData\secure` | Written by | Description |
| ---------------------------- | ---------- | ----------- |
| `enroll.token`  | installer (`ENROLL_TOKEN`) | Enrollment token created in the panel (it can be valid for several devices, see `Installer/README.md`). Deleted once the server has issued the device secret. |
| `agent.secret`  | the agent, from the server's `set_secret` | Per-device secret, sent as `X-Agent-Secret` on every connection. |
| `bypass.secret` | installer or administrator | Shared secret that verifies offline quarantine bypass codes (the panel's daily code, `offline_bypass_code` in the backend). Offline bypass is disabled when it is missing. A valid code does what the panel's unlock does: it closes the lock screen and lifts the network isolation. The lock screen covers the taskbar, so it has its own code field; the tray menu has one too. Both check the format first (6–64 characters, 0-9 and A-F), so a typo such as the letter O for zero does not use up an attempt. When the server can be reached, the use is recorded in its audit log (`agent.offline_bypass`). Any logged-on user can reach the tray pipe, so after 5 wrong codes the bypass locks for 15 minutes, doubling with each further lockout up to 24 hours. The counters are kept in `bypass-state.json` in this folder, so restarting the service or the PC does not reset them. After 24 hours without a wrong code (counted from the end of the last lock), old failures and lockouts are forgotten and the next lock starts at 15 minutes again. |

To set one by hand, put `EnrollToken` or `BypassSecret` into `appsettings.json` (or the legacy `POPS_ENROLL_TOKEN` / `POPS_BYPASS_SECRET` system variables) and restart the service. On start the agent moves the value into `C:\POpsData\secure`, replacing the stored one, and removes it from the file (or deletes the variable).

## Server authentication

1. **Enrollment.** While the agent has no device secret it sends the enrollment token as `X-Enroll-Token` on `/ws/agent/{hw_id}`. When the server accepts it, it replies after the first heartbeat with `{"action":"set_secret","secret":"…"}`; the agent stores the secret and deletes the token.
2. **Later connections** send `X-Agent-Secret`. When both a secret and a token are present both are sent; the server checks the secret first, so a device whose secret was lost can re-enroll with a new token without reinstalling.
3. **Rejection.** When the server enforces authentication and rejects the agent, it closes the socket with code 4401. The agent logs this and retries every 60 seconds instead of every 5, because each rejected attempt writes an audit record on the server.

The Vision WebSocket (`/ws/vision/{hw_id}`) sends the same headers. The agent's HTTP calls (`POST /api/inventory/{hw_id}`, `/api/policy_alert` and the reports under *What the agent reports*) send `X-Agent-Id` + `X-Agent-Secret`. None of these credentials is ever sent to a non-loopback `http://` address, and HTTP redirects are not followed: .NET would carry the `X-Agent-Secret` header to the new host.

The secret is bound to the device identity the server resolves; when the server renames the device (`set_identity`) it moves the secret with it, so the agent keeps using the same secret. Neither value is ever written to the log.

### Machines with freeze software

Enroll before freezing: install with the machine thawed, wait until the device appears in the panel (the secret is now on disk), then freeze, so the secret is part of the frozen image. A machine that enrolls while frozen loses the secret at the next reboot, and a single-use token is already used up. To avoid that, set `PersistDir` to a folder that is not rolled back. With Windows Unified Write Filter, add a file exclusion for `C:\POpsData\secure` instead. Once the server enforces authentication, a device that has lost its secret needs a new enrollment token.

## Local hardening

- **Tray pipe.** Every logged-on user can open `POpsTrayPipe`, so the service checks each client before trusting it. The client process must be the installed `POpsTray.exe` (resolved from its PID; `Program Files` is admin-only), run in a user session, and, when the installed tray is Authenticode-signed, carry a valid signature. Any other program is logged and disconnected, so a student cannot feed fake screen frames or request a Vision tunnel. The tray in turn checks that the pipe belongs to the service: its owner must be SYSTEM or Administrators (the service sets SYSTEM explicitly). If the owner cannot be read, the tray does not connect. A program started while the service is down, or in another session, therefore cannot pose as the service and send the tray fake lock screens, capture requests or remote input.
- **Tray and watchdog start.** When neither runs in any session, the service starts the watchdog and the tray in the active console session, as the signed-in user (`WTSQueryUserToken` + `CreateProcessAsUser`). It checks at start and then every 30 seconds, and the updater does the same when it finishes. After an install or an update the tray therefore appears within about 30 seconds without signing out; in quarantine this also brings back the lock screen. The tray waits up to a minute after sign-in for the shell (`explorer`) so its icon is not lost. Nothing is started while an update is running, whether the agent started it (`update.lock`) or it is a manual or GPO MSI install (Windows Installer's `_MSIExecute` mutex, counted only when it is owned by SYSTEM or Administrators, since any user can create a mutex with that name). "Running" means a process from the install folder, checked by its image path: a program copied elsewhere and named `POpsTray.exe` does not count and cannot stop the real tray (and in quarantine the lock screen) from starting. The same applies to the tray's single-instance lock. If another program holds it, or created it with permissions that lock the tray out, while no real tray is running, the tray starts anyway. If the user's environment block cannot be built, nothing is started, so the program never runs with the service's environment. A program that closes right after being started is logged once, not every 30 seconds. The tray and the watchdog are built without startup-hook support, so `DOTNET_STARTUP_HOOKS` in the user's environment cannot load code into them. The service still treats everything the tray sends as untrusted input. Up to 0.1.5 a scheduled task for `BUILTIN\Users` was used instead, which did not start them in the user's session; the tray came back only at the next sign-in (`HKLM\...\Run`).
- **Remote input needs local consent.** Remote mouse/keyboard events are applied only while a Vision session is open that the tray started after the user accepted it (or after showing the mandatory-session notice). A compromised server alone cannot drive the PC. Screen previews are not affected.
- **Logs.** `C:\POpsLogs` (service and updater logs) is restricted to SYSTEM and Administrators. The tray, watchdog and Vision run in the user's session and log to `%LOCALAPPDATA%\POps\Logs`. The tray's log rotates at 1 MB and records only message types, never message contents or remote keystrokes.
- **Network quarantine** (`lockdown`, or the DNS threshold when `auto_quarantine` is on) adds two Windows Firewall block rules (group `POps Isolation`). They block every IPv4/IPv6 address except the POps server (resolved to IPs), the DNS and DHCP servers, loopback and IPv6 link-local/multicast. Block rules override every allow rule, so no other program's rule can bypass them. All firewall profiles are switched on for the duration; their previous state is saved in `C:\POpsData\secure\isolation.json` and restored by `unlock` or a valid offline bypass code. The lock screen is part of the quarantine: its state is kept in `C:\POpsData\secure\lockdown.json` (with the reason), and the service shows it again whenever the tray connects, so closing the tray in Task Manager, signing out or restarting does not remove it. If the isolation cannot be removed, the lock stays, the user is told so instead of "removed", and the agent records `agent.unlock_failed`. Every heartbeat reports the lock state as `"quarantined": true|false`; the server uses it to finish or resend a pending lock or unlock (every 5 minutes), so an unlock given while the PC was off is not lost. Both commands are idempotent: a repeated `lockdown` while isolated does not rebuild the firewall rules (the reason is kept if the repeat has none), and a repeated `unlock` while unlocked starts no PowerShell and shows no notice.
- **DNS policy detection** reads the Windows DNS client cache through the DNS API, independent of the Windows display language. It flags a name only if it equals, or is a subdomain of, a domain the school listed for an active category in the policy's `dns_domains` (`{"<category>": ["example.com", …]}`). Without such a list, nothing is flagged. Substring guesses such as "sex" in `essex.ac.uk` are gone. The check runs every 15 seconds from the first time the command channel connects; the policy is refreshed every minute. The lists are indexed once per policy change, and each cached name is looked up by its suffixes (`a.b.c`, `b.c`, `c`), so large lists do not cost CPU on every check. Names in other scripts are compared in their punycode form, so a list entry such as `örnek.com` matches the cache entry `xn--rnek-4qa.com`. A violation is one **list entry**: `www.`, `cdn.` and `static.` of the same site count once and are reported once (`POST /api/policy_alert`, with the first name seen). With `auto_quarantine` the PC is quarantined when the violations of the **last hour** reach `quarantine_threshold`, even if the server cannot be reached. This goes through the same path as the panel's lockdown (lock screen with the reason "DNS kural ihlali eşiği" and network isolation) and is reported as `agent.auto_quarantine`, so the panel shows the device as quarantined. When the user signed in at the console changes, the count and the reported entries are reset. Names already in the DNS cache at that moment belong to the previous user and are ignored until they drop out of the cache, so a student is never quarantined for someone else's visits, and the next student visiting the same site is reported again. `unlock` or a valid bypass code also resets the count.
- **Server messages** are read until the end of the WebSocket message, with a limit of 8 MB on the command socket and 1 MB on the Vision socket, so long deployment scripts arrive whole. Oversized or malformed messages are logged instead of dropped silently.

## What the agent reports

Besides the heartbeat, the agent sends the data below. The software inventory, the Windows Update status and sign-in events are accepted only from an enrolled agent (`X-Agent-Id` + `X-Agent-Secret`, even when the server does not enforce authentication), so an agent without a device secret sends none of them.

| Data | Endpoint | When |
| --- | --- | --- |
| Installed programs: name, version, publisher, install date | `POST /api/software/{hw_id}`, the whole list (at most 5000 entries); the server replaces the previous one | About a minute after start, then every 6 hours when the list has changed, and at least once a day |
| Windows Update: pending updates (KB, title, MSRC severity, categories, security flag), their counts, whether a restart is needed, time of the scan and of the last successful install | `POST /api/patches/{hw_id}` (at most 500 updates, critical and security ones first) | Once a day, and when the panel asks |
| User signed in at the console | `POST /api/auth/login`, `POST /api/auth/logout` | When it changes (checked every 15 seconds) |
| Name of the program in the foreground, for example `chrome` or `WINWORD` | `active_window` in the heartbeat | While the tray runs |

- **Installed programs** are read from the `Uninstall` registry keys (64-bit, `WOW6432Node`, and the hives of signed-in users for programs installed per user). Windows components and update entries are left out: `SystemComponent=1`, a `ParentKeyName`, a `ReleaseType` of `Update`, `Hotfix` or `Security Update`, or an empty `DisplayName`. Name, version, publisher and install date are cut to the server's column sizes (300, 100, 200, 20 characters). If the server does not have the endpoint (404/405), the agent waits a day before it tries again.
- **Windows Update** status comes from the Windows Update Agent API. Security and critical updates are recognised by their classification ID, because category names depend on the Windows language. An update counts as a security update when it is in *Security Updates* or has an MSRC severity, and as critical when its severity is *Critical* or it is in *Critical Updates*. Optional and preview updates (`BrowseOnly`) and Windows version upgrades (*Upgrades*) are neither counted as pending nor installed. The daily scan runs 10–70 minutes after the service starts, with a fixed delay per device so that a lab switched on together does not scan at once. After that it runs 24 hours after the last scan. A scan that takes longer than 15 minutes is cancelled. If the report cannot be sent, the agent keeps it and retries **only the send** every 30 minutes; it does not scan again. If the server does not have the endpoint (404/405), the report is dropped until the next daily scan. `C:\POpsData\patch-scan.json` keeps the scan time and an unsent report across restarts.
- **Installing updates** (`{"action":"install_updates","scope":"security"|"all"}` from the panel): the agent scans and accepts license terms where needed. It then downloads and installs either the security and critical updates (`security`) or all pending ones (`all`), scans again and reports a short summary as `last_result`, for example `3 güncelleme kuruldu, 1 başarısız (KB5043080)`. **The agent never restarts the PC**; `reboot_required` tells the panel when a restart is needed. Updates that may ask the user for input are skipped. Downloading and installing together may take at most 3 hours; after that the agent asks Windows Update to stop and reports it in the summary. Only one scan or installation runs at a time; a request that arrives meanwhile is logged and ignored. All of this runs on a background thread and never holds up the command channel.
- **Sign-in events** keep the panel's logged-on user current. The last reported sign-in is kept in `C:\POpsData\session.json`, so a service restart does not report the same sign-in again, and a sign-out missed at shutdown is reported at the next start. If a report cannot be sent, the next attempt is 5 minutes later.
- **Foreground program:** the tray sends only the process name. Window titles can contain personal data (document names, websites, chat partners), so the tray does not read them, and the service ignores the titles that older trays sent.

## Help desk (Sorun bildir / Taleplerim)

The tray menu has **Sorun bildir** (report a problem) and **Taleplerim** (my requests).

- **Sorun bildir** asks for a subject (at least 3 characters), a category (hardware, software, network/internet, printer, account/password, other) and a description. The tray passes it to the service over the pipe. The service adds the user of the **tray's own session** as the reporter, found from the pipe client's process. It is not taken from the form, and it is not the console user, so fast user switching and RDP give the right name. It then limits the subject to 200 and the description to 5000 characters and sends the request to `POST /api/tickets/agent/{hw_id}`. The user sees the server's answer in Turkish, for example that the subject is too short or that the PC already has too many open requests (the server allows 5 open and 10 new per hour).
- **Taleplerim** shows the requests and the IT team's replies (`GET /api/tickets/agent/{hw_id}`; internal notes never leave the server). It lists **only the signed-in user's** requests: the server returns every request of the PC, and on a shared lab PC one student must not read another's.
- The service limits what the tray may ask for: one request of each kind at a time, a list at most every 6 seconds (the server's own limit is 5) and a new request at most every 10 seconds. Anything more gets a short "too often" answer and never reaches the server. The server also answers 429 to more than one request per 5 seconds per device; the tray keeps the list it shows and just displays the note.
- The service checks for new replies every 5 minutes (for the same user) while the tray is connected and shows a balloon; clicking it opens **Taleplerim**. The reply counts already shown are kept in `C:\POpsData\tickets-seen.json`, so a restart does not repeat old notices.
- Like the other reports, nothing is sent without a device secret.

## Lifting a quarantine by hand

Use this only when neither the panel's unlock nor the offline bypass code helps. For example: the server is gone for good, or PowerShell or Windows Firewall on the PC is broken, so the agent cannot remove its own rules (the panel then reports that the quarantine could not be lifted). You need a local administrator account.

1. From 0.1.11-alpha the quarantine also hides **Switch user**, **Sign out**, **Lock**, **Change a password** and **Task Manager** on the Ctrl+Alt+Del screen (see *Ctrl+Alt+Del during a quarantine* below). Start the PC in Safe Mode instead: press Ctrl+Alt+Del, click the power icon, hold **Shift** and choose **Restart**, then **Troubleshoot → Advanced options → Startup Settings → Restart → 4 (Safe Mode)**. Sign in as a local administrator. Neither the POps service nor the tray runs in Safe Mode. (Agents older than 0.1.11: Ctrl+Alt+Del → **Switch user** still works.)
2. In an elevated command prompt, remove the firewall rules:
   ```
   netsh advfirewall firewall delete rule name="POps Isolation - Outbound"
   netsh advfirewall firewall delete rule name="POps Isolation - Inbound"
   ```
   The PowerShell equivalent is `Get-NetFirewallRule -Group "POps Isolation" | Remove-NetFirewallRule`. Agents older than 0.1.4-alpha named their rules `POps_Isolation_*`.
3. The quarantine switched every firewall profile on. `type C:\POpsData\secure\isolation.json` shows which profiles were off before (`previous_profiles`); switch only those off again, for example `netsh advfirewall set privateprofile state off`. If the file is missing, leave the profiles on.
4. Delete the lock state:
   ```
   del C:\POpsData\secure\lockdown.json
   del C:\POpsData\secure\isolation.json
   ```
   Do **not** delete `C:\POpsData\secure\kiosk-policies.json`: it holds the values the Ctrl+Alt+Del options had before the quarantine.
5. Restart the PC normally. The service finds no lock, puts the Ctrl+Alt+Del options back from `kiosk-policies.json` (the machine settings at once, each user's own settings when that user signs in) and deletes the file; when the tray reconnects it closes the lock screen, and the heartbeat reports `"quarantined": false`. (Without a reboot: `sc stop POpsAgent`, then `sc start POpsAgent`.)
6. If the panel still shows a pending lock for the device, lift it there as well; otherwise the server sends the lock again within 5 minutes.
7. Only if the agent cannot run any more (for example it was removed while users who were signed in during the quarantine had signed out): remove the values by hand. Machine: `reg delete "HKLM\Software\Microsoft\Windows\CurrentVersion\Policies\System" /v <name> /f` for `HideFastUserSwitching` and `DisableTaskMgr`. Per user, signed in as that user: `reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Policies\System" /v <name> /f` for `DisableTaskMgr`, `DisableLockWorkstation`, `DisableChangePassword`, and `reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer" /v NoLogoff /f`. Check `kiosk-policies.json` first: an entry with `"HadValue": true` had its own value before (for example your GPO), put that `PreviousValue` back instead of deleting it.

### Ctrl+Alt+Del during a quarantine

Windows always handles Ctrl+Alt+Del itself; no program can block it. From 0.1.11-alpha the quarantine removes what that screen offers, so a student can no longer leave or kill the lock screen:

| Option on the Ctrl+Alt+Del screen | Policy value (1 = hidden) | Where | Takes effect |
| --- | --- | --- | --- |
| Task Manager (also Ctrl+Shift+Esc and running `taskmgr`) | `DisableTaskMgr` | machine and every signed-in user | at once |
| Switch user | `HideFastUserSwitching` | machine | at once |
| Sign out | `NoLogoff` (`…\Policies\Explorer`) | every signed-in user | at that user's next sign-in |
| Change a password | `DisableChangePassword` | every signed-in user | at that user's next sign-in |
| Lock | `DisableLockWorkstation` | every signed-in user | at that user's next sign-in |

- Measured on Windows 11: Task Manager and Switch user disappear at once from the machine values. Sign out, Change a password and Lock are user policies that Windows reads only when the user signs in (a `gpupdate` does not refresh them), and their machine copies have no effect. In the session that was already open when the lock started they therefore stay visible, but none of them ends the quarantine: after signing out and in again the student gets the lock screen back (and now without those options), and the network stays isolated throughout. A quarantine that survives a restart hides all five from the first sign-in.
- The values go under `…\CurrentVersion\Policies\System` (except `NoLogoff`) in `HKLM` and in each signed-in user's hive (`HKU\<SID>`); a user who signs in during the quarantine is covered when their tray connects.
- Before writing, the service records every previous value in `C:\POpsData\secure\kiosk-policies.json` (SYSTEM and Administrators only). When the lock ends (panel, bypass code, or at service start when there is no lock) it puts back only what it changed: a value that was already 1 (your own policy) stays 1, a value someone else changed during the quarantine is left alone, and a value that did not exist is deleted. Settings of a user who signed out during the quarantine are restored when they sign in again. Uninstalling the MSI restores them too.
- A quarantine that survives a restart applies them again when the service starts.
- The lock screen stays on top and takes the focus back within half a second after Ctrl+Shift+Esc, the Windows key, Alt+Tab or another window.

## Capability policy

A school can switch off the two server features that matter most if the server or a panel account is compromised (threat #4 in `SECURITY.md`):

| Capability | Covers |
| --- | --- |
| `terminal_enabled` | `execute`: commands the agent runs as SYSTEM |
| `vision_enabled` | screen streaming, screen previews (`get_thumbnail`) and remote mouse/keyboard |

The state lives in `C:\POpsData\secure\capabilities.json` (SYSTEM and Administrators only):

- **Installer.** `TERMINAL_ENABLED` / `VISION_ENABLED` (`1` or `0`) set either direction. A flag that is not given keeps its current value, so an update never turns a disabled capability back on. A first install without them enables both.
- **Server.** It may only switch a capability **off**: `{"action":"set_capabilities","terminal_enabled":false}`. A request to switch one on is ignored and logged, so turning it back on takes a local administrator (MSI repair or reinstall with `…_ENABLED=1`). A compromised server can therefore not re-enable what the school disabled.
- **Missing or unreadable file.** An install from before this feature (no file) keeps both enabled. A file that exists but cannot be read counts as both disabled.

A disabled capability is refused on the agent, not merely hidden in the panel. `execute` does not run; the task is closed with a `[REDDEDİLDİ]` result, and the server receives `{"type":"capability_denied","capability":"terminal","action":"execute","task_id":…}`. Vision requests are refused in the same way, and a stream that is already running is stopped when Vision is switched off. On every connection, and after every change, the agent reports its state as `{"type":"capabilities","terminal_enabled":…,"vision_enabled":…}`.

## Updates

Agent updates are signed MSI installs; the agent applies nothing unsigned.

1. The server sends `{"action":"update_agent","manifest":"<base64 of manifest.json>","manifest_sig":"<contents of manifest.json.sig>"}` — the release manifest produced by `tools/sign_release.py`.
2. The agent verifies the ed25519 signature with the public key compiled into it (`ReleaseVerifier.PublicKeyBase64`, the raw bytes of `keys/pops_release_ed25519.pub.pem`), requires the manifest version to be newer than its own (no downgrade, no re-install of the same version) and takes the MSI's name, size and SHA-256 from the signed manifest only.
3. It downloads that file from its own server only (`<ServerUrl>/updates/<name>`), stops reading at the signed size and deletes the file unless size and SHA-256 match. Only then does anything change on the machine.
4. The verified MSI is kept in `C:\POpsData\updates`, and `POpsUpdater` is copied to `C:\POpsData\updater` together with every file its `POpsUpdater.deps.json` lists, and started from there, so that `msiexec` never has to replace a running updater. If any of those files is missing the update is not started.
5. `POpsUpdater` holds `C:\POpsData\update.lock` and prepares the rollback package: every install keeps its own MSI as `C:\POpsData\packages\installed.msi`, which is copied to `previous.msi` and accepted only if the copy's SHA-256 matches, it is a POps Agent package that supports rollback, and it belongs to the product installed right now. It then backs up the install folder file by file, closes the tray and watchdog and runs `msiexec /i … /qn /norestart` (1618 is retried).
6. It waits up to 90 seconds for the new version to write `C:\POpsData\health.json` (`{"version":"<version>","ts":<epoch>,"pid":…}`). The service writes this file first thing on every start, before the slower WMI inventory; a `health.json` older than the install is ignored.
7. **Nothing installed is removed before its replacement is in place.** A failed `msiexec` needs no extra step: Windows Installer restores the old version itself. A 3010 exit (files in use) means the install completes at the next restart, so the updater reports `pending_reboot` and does not roll back. If the new version does not report healthy, the updater installs `previous.msi` with `POPS_ROLLBACK=1 REINSTALLMODE=amus`, which removes the newer version inside the same Windows Installer transaction. If that fails (retried once), Windows Installer leaves the newer version installed. When msiexec returns 0 the updater checks right away, before waiting for `health.json`, that the `POpsAgent` service exists and its `POpsAgent.exe` has the previous version; if not, it repairs with the same package (`msiexec /fvamus`) at once. If there is no usable previous MSI, the updater restores the file backup instead.
   - Up to 0.1.6 the rollback install returned 0 but left the machine without the service and the POps executables (both drills, 0.1.4 → 0.1.5 and 0.1.5 → 0.1.6; reproduced by hand). Windows Installer costs the install while the newer version is still present, refuses the components whose files have a higher version ("Disallowing installation of component … since the same component with higher versioned keyfile exists"), and `RemoveExistingProducts` then removes the newer version with its service. Only the last-resort repair about 90 seconds later brought the agent back. `REINSTALLMODE=amus` writes every file whatever its version; the package also sets it itself when `POPS_ROLLBACK=1` is given without `REINSTALLMODE`, so a manual rollback install works too.
8. After every outcome it checks that the `POpsAgent` service exists and is running and starts it if needed. If the service is missing, it repairs or installs from the package it still has as a last resort and logs `[KRİTİK]`.
9. The outcome goes to `C:\POpsData\update-result.json`, together with `rollback`, `running_version` (the version actually running afterwards), `agent_state` (`running`, `not_running`, `reinstalled`, `unmanaged`) and the msiexec exit code. The log path is under `C:\POpsLogs`. The outcome is one of:

   | Outcome | Meaning |
   | --- | --- |
   | `success` | The new version is running. |
   | `pending_reboot` | 3010: completes at the next restart; no rollback. |
   | `rolled_back` | The update was not applied and the machine runs the previous version. `rollback` says how: `msi_transaction` (msiexec failed and Windows Installer restored it), `msi` (unhealthy, previous MSI reinstalled), `msi_repair` (as `msi`, but the reinstall left the service or executable missing and a repair with the same package fixed it; `detail` says what was missing) or `files` (file backup restored). |
   | `rollback_pending_reboot` | Like `rolled_back`, completing at the next restart. |
   | `rollback_failed` | The previous version could not be restored; check `agent_state` and `running_version`. |
   | `install_failed` | msiexec refused to start (another install in progress, policy, unreadable package, …); nothing on the machine changed. |
   | `rejected` | The package did not match its SHA-256. |
   | `error` | Unexpected failure; see `detail`. |

 The agent sends it to the server once over the command WebSocket as `{"type":"update_result","status":"<outcome>","from_version",…}`, then renames the file to `update-result.reported.json`.

While `update.lock` is younger than 15 minutes the watchdog neither restarts the service nor relaunches the tray; once the lock is gone it starts the tray again in the user's session.

### Rollback drill

To prove the rollback path on a real machine without publishing a broken build:

1. Have someone present who can recover the machine.
2. In an elevated command prompt, run `type nul > C:\POpsData\secure\rollback-drill`. Only SYSTEM and Administrators can write to that folder.
3. Dispatch a newer signed release to that machine from the panel.

The new version's agent **consumes** the marker the first time it starts during that update. It renames `rollback-drill` to `rollback-drill.consumed` and writes its own version and the update run (`started_at` from `update.lock`) into it, and it does not write `health.json`. The updater treats the new version as unhealthy after 90 seconds. It deletes both marker files **before** it reinstalls the previous MSI with `POPS_ROLLBACK=1`, and again at the end, then reports `rolled_back` / `msi` with `running_version` set to the previous version. Dispatching the same release again then succeeds normally.

- The previous version never sees the marker, even if its agent does not know about the drill, so it reports healthy. Before 0.1.6 only the updater deleted the marker, at the very end: the reinstalled version saw it too, did not report healthy, and the drill ended in a false `rollback_failed` (seen on 0.1.4 → 0.1.5).
- `health.json` is skipped only while `.consumed` holds the agent's own version **and** the same update run. If Windows restarts the new service during the drill, the drill still holds. If the same version is sent again later (a new run), the old record is deleted and the version installs normally, even when an older updater left `.consumed` behind.
- The marker is consumed only while an update to that agent's version is running. If the service restarts for another reason after you placed the marker, it stays in place for the next update.
- The fix is in the agent of the version being **installed**. The first drill that ends in `rolled_back` is 0.1.5 → 0.1.6 or later; a drill into 0.1.5 or older still ends in a false `rollback_failed` (the last-resort repair still brings the machine back).
- Up to 0.1.6 the rollback install itself left the machine without the agent (see step 7 above), so those drills end with `agent_state` `reinstalled` after about 90 seconds without an agent. The fix is in the updater and the package of the version installed **before** the drill: the first drill that should end in `rolled_back` with `agent_state` `running` is 0.1.7 → 0.1.8, rolling back to 0.1.7.

The updater that runs is always the one from the version **installed before** the update: the running service copies `POpsUpdater` out of its own install folder, using its own `POpsUpdater.deps.json` to decide which files to copy. A drill therefore tests the installed version's updater and copy step, not the new version's. To test a change to the updater or to what it depends on (such as `POps.Shared.dll` in 0.1.4), first ship that version normally without the marker and check that the service, watchdog and tray run. Then do the drill with the next release (0.1.4 → 0.1.5, rolling back to 0.1.4).
