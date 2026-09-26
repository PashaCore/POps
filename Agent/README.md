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
