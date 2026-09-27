# POps Agent

Windows endpoint agent (.NET 8). Includes the main agent, tray application, remote vision service, and watchdog.

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
| `enroll.token`  | installer (`ENROLL_TOKEN`) | One-time enrollment token created in the panel. Deleted once the server has issued the device secret. |
| `agent.secret`  | the agent, from the server's `set_secret` | Per-device secret, sent as `X-Agent-Secret` on every connection. |
| `bypass.secret` | installer or administrator | Shared secret that verifies offline quarantine bypass codes (the panel's daily code, `offline_bypass_code` in the backend). Offline bypass is disabled when it is missing. Any logged-on user can reach the tray pipe, so after 5 wrong codes the bypass locks for 15 minutes, doubling with each further lockout up to 24 hours. |

To set one by hand, put `EnrollToken` or `BypassSecret` into `appsettings.json` (or the legacy `POPS_ENROLL_TOKEN` / `POPS_BYPASS_SECRET` system variables) and restart the service. On start the agent moves the value into `C:\POpsData\secure`, replacing the stored one, and removes it from the file (or deletes the variable).

## Server authentication

1. **Enrollment.** While the agent has no device secret it sends the enrollment token as `X-Enroll-Token` on `/ws/agent/{hw_id}`. When the server accepts it, it replies after the first heartbeat with `{"action":"set_secret","secret":"…"}`; the agent stores the secret and deletes the token.
2. **Later connections** send `X-Agent-Secret`. When both a secret and a token are present both are sent; the server checks the secret first, so a device whose secret was lost can re-enroll with a new token without reinstalling.
3. **Rejection.** When the server enforces authentication and rejects the agent, it closes the socket with code 4401. The agent logs this and retries every 60 seconds instead of every 5, because each rejected attempt writes an audit record on the server.

The Vision WebSocket (`/ws/vision/{hw_id}`) sends the same headers. The agent's HTTP calls (`POST /api/inventory/{hw_id}`, `POST /api/policy_alert`) send `X-Agent-Id` + `X-Agent-Secret`. None of these credentials is ever sent to a non-loopback `http://` address.

The secret is bound to the device identity the server resolves; when the server renames the device (`set_identity`) it moves the secret with it, so the agent keeps using the same secret. Neither value is ever written to the log.

### Machines with freeze software

Enroll before freezing: install with the machine thawed, wait until the device appears in the panel (the secret is now on disk), then freeze, so the secret is part of the frozen image. A machine that enrolls while frozen loses the secret at the next reboot, and its one-time token is already used. To avoid that, set `PersistDir` to a folder that is not rolled back. With Windows Unified Write Filter, add a file exclusion for `C:\POpsData\secure` instead. Once the server enforces authentication, a device that has lost its secret needs a new enrollment token.

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
7. **Nothing installed is removed before its replacement is in place.** A failed `msiexec` needs no extra step: Windows Installer restores the old version itself. A 3010 exit (files in use) means the install completes at the next restart, so the updater reports `pending_reboot` and does not roll back. If the new version does not report healthy, the updater installs `previous.msi` with `POPS_ROLLBACK=1`, which removes the newer version inside the same Windows Installer transaction. If that fails (retried once), Windows Installer leaves the newer version installed. If there is no usable previous MSI, the updater restores the file backup instead.
8. After every outcome it checks that the `POpsAgent` service exists and is running and starts it if needed. If the service is missing, it repairs or installs from the package it still has as a last resort and logs `[KRİTİK]`.
9. The outcome goes to `C:\POpsData\update-result.json`, together with `rollback`, `running_version` (the version actually running afterwards), `agent_state` (`running`, `not_running`, `reinstalled`, `unmanaged`) and the msiexec exit code. The log path is under `C:\POpsLogs`. The outcome is one of:

   | Outcome | Meaning |
   | --- | --- |
   | `success` | The new version is running. |
   | `pending_reboot` | 3010: completes at the next restart; no rollback. |
   | `rolled_back` | The update was not applied and the machine runs the previous version. `rollback` says how: `msi_transaction` (msiexec failed and Windows Installer restored it), `msi` (unhealthy, previous MSI reinstalled) or `files` (file backup restored). |
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

The new version starts but, seeing the marker, does not write `health.json`. The updater treats it as unhealthy after 90 seconds and reinstalls the previous MSI with `POPS_ROLLBACK=1`. It then deletes the marker (one-shot) and reports `rolled_back` / `msi` with `running_version` set to the previous version. Dispatching the same release again then succeeds normally.
