# POps Agent

Windows endpoint agent (.NET 8). Includes the main agent, tray application, remote vision service, and watchdog.

Install it with the MSI from the release (`POps-Agent-<version>-win-x64.msi`); properties, upgrades and migration from older installs are described in [`Installer/README.md`](../Installer/README.md).

## Configuration

The agent reads its settings from `appsettings.json` in its install folder first (for example `C:\Program Files (x86)\POps`), then from `C:\POps\appsettings.json`; for each setting the first non-empty value wins. On every start the service restricts both files to SYSTEM and Administrators (a protected ACL, so nothing is inherited from `Program Files`), reads the result back and logs an error if any other account can still open the file. No server address or secret is compiled into the binaries; each setting below can also be supplied as a system environment variable, which takes precedence over the file.

| `appsettings.json` key | Environment variable | Description |
| ---------------------- | -------------------- | ----------- |
| `ServerUrl`            | `POPS_SERVER_URL`    | POps backend URL, e.g. `http://pops.example.com:8000`. Falls back to `http://127.0.0.1:8000` when unset. |
| `PersistDir`           | `POPS_PERSIST_DIR`   | Optional. A local NTFS folder that freeze software (Deep Freeze ThawSpace, a thawed drive, …) does not roll back. The device secret is mirrored there so it survives a reboot on a frozen machine. |

### Secrets

Secrets are never kept in `appsettings.json` or in environment variables, which every user on the machine can read. They live in `C:\POpsData\secure`, a folder with a protected ACL that grants only SYSTEM and Administrators: students can neither read nor list it. (`C:\POpsData` itself stays readable so the tray and watchdog can read `identity.key`.)

| File in `C:\POpsData\secure` | Written by | Description |
| ---------------------------- | ---------- | ----------- |
| `enroll.token`  | installer (`ENROLL_TOKEN`) | One-time enrollment token created in the panel. Deleted once the server has issued the device secret. |
| `agent.secret`  | the agent, from the server's `set_secret` | Per-device secret, sent as `X-Agent-Secret` on every connection. |
| `bypass.secret` | installer or administrator | Shared secret that verifies offline quarantine bypass codes. Offline bypass is disabled when it is missing. |

To set one by hand, put `EnrollToken` or `BypassSecret` into `appsettings.json` (or the legacy `POPS_ENROLL_TOKEN` / `POPS_BYPASS_SECRET` system variables) and restart the service. On start the agent moves the value into `C:\POpsData\secure`, replacing the stored one, and removes it from the file (or deletes the variable).

## Server authentication

1. **Enrollment.** While the agent has no device secret it sends the enrollment token as `X-Enroll-Token` on `/ws/agent/{hw_id}`. When the server accepts it, it replies after the first heartbeat with `{"action":"set_secret","secret":"…"}`; the agent stores the secret and deletes the token.
2. **Later connections** send `X-Agent-Secret`. When both a secret and a token are present both are sent; the server checks the secret first, so a device whose secret was lost can re-enroll with a new token without reinstalling.
3. **Rejection.** When the server enforces authentication and rejects the agent, it closes the socket with code 4401. The agent logs this and retries every 60 seconds instead of every 5, because each rejected attempt writes an audit record on the server.

The secret is bound to the device identity the server resolves; when the server renames the device (`set_identity`) it moves the secret with it, so the agent keeps using the same secret. Neither value is ever written to the log.

### Machines with freeze software

Enroll before freezing: install with the machine thawed, wait until the device appears in the panel (the secret is now on disk), then freeze, so the secret is part of the frozen image. A machine that enrolls while frozen loses the secret at the next reboot, and its one-time token is already used. To avoid that, set `PersistDir` to a folder that is not rolled back. With Windows Unified Write Filter, add a file exclusion for `C:\POpsData\secure` instead. Once the server enforces authentication, a device that has lost its secret needs a new enrollment token.

## Updates

Agent updates are signed MSI installs; the agent applies nothing unsigned.

1. The server sends `{"action":"update_agent","manifest":"<base64 of manifest.json>","manifest_sig":"<contents of manifest.json.sig>"}` — the release manifest produced by `tools/sign_release.py`.
2. The agent verifies the ed25519 signature with the public key compiled into it (`ReleaseVerifier.PublicKeyBase64`, the raw bytes of `keys/pops_release_ed25519.pub.pem`), requires the manifest version to be newer than its own (no downgrade, no re-install of the same version) and takes the MSI's name, size and SHA-256 from the signed manifest only.
3. It downloads that file from its own server only (`<ServerUrl>/updates/<name>`), stops reading at the signed size and deletes the file unless size and SHA-256 match. Only then does anything change on the machine.
4. The verified MSI is kept in `C:\POpsData\updates`, and `POpsUpdater` is copied to `C:\POpsData\updater` and started from there, so that `msiexec` never has to replace a running updater.
5. `POpsUpdater` holds `C:\POpsData\update.lock`, backs up the install folder file by file, closes the tray and watchdog and runs `msiexec /i … /qn /norestart` (0 and 3010 count as success; 1618 is retried). It then waits up to 90 seconds for the new version to write `C:\POpsData\health.json` (`{"version":"<version>","ts":<epoch>,"pid":…}`), which the service writes on every start; a `health.json` older than the install is ignored.
6. If the new version does not report healthy, the updater uninstalls it and reinstalls the previous MSI (every install keeps its own package as `C:\POpsData\packages\installed.msi`). If there is no previous MSI, it restores the file backup. A failed `msiexec` needs no extra step: the old version is restored by Windows Installer's own rollback.
7. The outcome goes to `C:\POpsData\update-result.json` (`success`, `install_failed`, `rolled_back`, `rollback_failed` or `rejected`, with the msiexec exit code and log path under `C:\POpsLogs`), and the agent logs it on its next start.

While `update.lock` is younger than 15 minutes the watchdog neither restarts the service nor relaunches the tray; once the lock is gone it starts the tray again in the user's session.
