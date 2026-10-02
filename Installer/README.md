# POps Installer

Scripts and resources for deploying and installing POps.

- `agent/` — Windows MSI for the agent (WiX 5): `Package.wxs`, the build project and the custom actions that write settings and secrets.
- `server/` — reference copy of the backend deploy script.

## Agent MSI

The release workflow builds `POps-Agent-<version>-win-x64.msi`, attaches it to every GitHub release and lists it with its SHA-256 in the signed `manifest.json`. It has no prerequisites: from 0.1.15-alpha every component is published self-contained and the .NET 10 runtime is installed with it (about 270 files, a ~41 MB MSI; earlier versions needed the .NET 8 Desktop Runtime). See [`docs/decisions.md`](../docs/decisions.md) D-19.

### Install

```
msiexec /i POps-Agent-<version>-win-x64.msi /qn /l*v C:\POpsLogs\msi-install.log SERVER_URL=https://pops.example.com ENROLL_TOKEN=<token from the panel>
```

| Property | Needed | Written to |
| -------- | ------ | ---------- |
| `SERVER_URL`    | first install | `appsettings.json` → `ServerUrl` (in the install folder). Must be `https://`; plain `http://` is accepted only for `127.0.0.1` / `localhost`. |
| `ENROLL_TOKEN`  | recommended   | `C:\POpsData\secure\enroll.token` |
| `ENROLL_TOKEN_FILE` | recommended for unattended installs | Reads the enrollment token from this file into `C:\POpsData\secure\enroll.token`; takes precedence over `ENROLL_TOKEN`. |
| `BYPASS_SECRET` | optional, legacy | `C:\POpsData\secure\bypass.secret`. Shared-fleet bypass secret; deprecated in favour of the per-device key delivered by the server. |
| `BYPASS_SECRET_FILE` | optional, legacy | Reads the shared bypass secret from this file; takes precedence over `BYPASS_SECRET`. |
| `PERSIST_DIR`   | optional      | `appsettings.json` → `PersistDir` (see `Agent/README.md`, machines with freeze software) |
| `TERMINAL_ENABLED` | optional   | `C:\POpsData\secure\capabilities.json`: `1` allows the panel's remote terminal (`execute`) on this PC, `0` disables it. See *Capability policy* in `Agent/README.md`. |
| `VISION_ENABLED`   | optional   | same file: `1` / `0` for screen streaming, previews and remote input. |
| `SERVER_CA_CERT` | optional      | path to the school CA's PEM (`pops-ca.pem`, served at `https://<server>/pops-ca.pem`) → `C:\POpsData\secure\server-ca.pem`. The agent then accepts the server certificate only if it chains to that CA. `system` removes the file (Windows trust store). Do **not** pass it for a Let's Encrypt or other public certificate. See [`docs/tls.md`](../docs/tls.md). |
| `INSTALLFOLDER` | optional      | install folder, default `C:\Program Files\POps`. See *Install folder* below. |

- Every property is optional on an upgrade: a value that is not given keeps the installed one. A first install without `SERVER_URL` (and without an old install to take it from) fails with a clear message in the log.
- A plain `http://` server address is refused, including one migrated from an older install. Over `ws://` the device secret, the enrollment token and the commands the agent runs as SYSTEM would cross the network in clear text. Pre-MSI installs that used `http://<ip>:8000` therefore need `SERVER_URL=https://…` on the command line.
- The four secret properties are hidden from the MSI log. Prefer `ENROLL_TOKEN_FILE`: direct values on an `msiexec` command line are still visible to other logged-on users. A named file must be readable by the installer (which runs as SYSTEM); an unreadable or empty file stops the install with a Turkish error. When both forms are supplied, the file wins.
- An enrollment token enrolls up to `max_uses` devices (1–10000, chosen when it is created on the **Sistem & Sürüm** page; 1 by default) until it expires (1 hour to 30 days, 72 hours by default), so one token can enroll a whole lab. Each device still receives its own secret. A device that is already enrolled cannot take a new secret with a token unless a superadmin allows re-enrollment for it (`POST /api/system/allow-reenroll`).
- `appsettings.json` and the secret files are written by a custom action, not installed as MSI files, so they survive upgrades. `appsettings.json` is readable only by SYSTEM and Administrators.
- `SERVER_CA_CERT` must point to a CA certificate in PEM form. A file that is not a PEM certificate, or that is the server's own certificate rather than the CA that signed it, stops the install with a message saying so; nothing is written in that case. Only the first certificate block of the file is kept.

### Install folder

The service runs `POpsAgent.exe` from `INSTALLFOLDER` as SYSTEM, and the updater is copied from there. Users must therefore neither write to the folder nor delete, rename or replace it, or any folder above it. The MSI checks this before any file is copied and stops with a message in the log if the folder is:

- a network path (`\\server\share\...`), on a removable, network or non-NTFS drive, or a drive root (`C:\`, `D:\`);
- a system folder or a folder above one: Windows, Program Files, Program Files (x86), ProgramData, `C:\Users` (a subfolder such as `C:\Program Files\POps` is fine);
- below a folder that users can delete, rename or change the permissions of, for example inside a user profile, or below a folder that the install would create with such inherited permissions;
- an existing folder that already holds other programs' files **and** is writable by users. Install POps into a separate, new folder instead.

If the folder is new, or holds only POps files (an older POps install), and users could write to it, for example `C:\POps`, which inherits Modify for Authenticated Users from `C:\`, the MSI makes it SYSTEM-owned with SYSTEM and Administrators full control and Users read and execute, and logs this. A folder users cannot write to, such as the default under Program Files, is left as it is. The MSI never changes the permissions of a drive root, a shared folder or another program's folder.

### What the package does

- Installs the agent, tray, watchdog and updater into `C:\Program Files\POps` and registers the `POpsAgent` service (LocalSystem, automatic start; Windows restarts it on failure).
- Registers the `POps Agent` source in the Windows Application event log for the service's local high-impact audit events; uninstall removes the source registration.
- Starts the tray in every user session through `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run` (`POpsTray`). From 0.1.6-alpha the service also starts the tray and the watchdog in the signed-in user's session within about 30 seconds after an install or an update, so nobody has to sign out and in again. The tray no longer writes its own per-user Run entry. An Active Setup entry runs the tray once per user at their next sign-in, before the desktop, in a mode that only deletes the per-user entry older versions left (`POpsTrayApp`), so an old tray cannot start next to the new one.
- **Upgrades** replace the installed version inside one transaction: the old product is removed right after `InstallInitialize`, and if the new version fails to install Windows Installer restores the old one. The MSI version is `a.b.c.<CI run number>`; a build with the same `a.b.c` and a higher run number is treated as an upgrade. Installing an older version over a newer one is refused, except with `POPS_ROLLBACK=1`, which only `POpsUpdater` passes when it rolls back: the older package then removes the newer version inside the same transaction, so a failed rollback leaves the newer version installed instead of leaving the machine without an agent. With `POPS_ROLLBACK=1` the package sets `REINSTALLMODE=amus` (unless `REINSTALLMODE` is given): otherwise Windows Installer skips every component whose installed file has a higher version and the removal of the newer version leaves no service and no POps executables, although msiexec returns 0 (up to 0.1.6).
- **Pre-MSI installs** are taken over: the old `POpsAgent` service is stopped and deleted, the tray, watchdog and other POps processes are closed, and the settings are migrated — `ServerUrl` from `C:\POps\appsettings.json` first (the only file released pre-MSI builds read), then from `C:\Program Files (x86)\POps`; a `BypassSecret` found there moves into `C:\POpsData\secure`. After the new service has started, the old install folder (`C:\Program Files (x86)\POps`) and the settings in `C:\POps` are deleted along with leftovers such as `PashaCoreAgent.*`, `apply_update.bat` and `appsettings.Development.json`. Files that are still in use are deleted at the next restart.
- Every install keeps its own package as `C:\POpsData\packages\installed.msi`. `POpsUpdater` rolls back to it when the next version does not start cleanly; see *Updates* in `Agent/README.md`.
- **Uninstall** removes the service, the files, the Run entry and `appsettings.json`. `C:\POpsData` (device identity and secret) and `C:\POpsLogs` are kept, so a reinstalled device returns with the same identity.

### Checking an install

```
sc qc POpsAgent
icacls "C:\Program Files\POps\appsettings.json"
icacls C:\POpsData\secure
```

`appsettings.json` and `C:\POpsData\secure` must list only `NT AUTHORITY\SYSTEM` and `BUILTIN\Administrators`, without `(I)`. The agent log is in `C:\POpsLogs`; lines tagged `[SECURE]` cover the secret store.

### Build locally

```
dotnet publish Agent/POps.Agent/POps.Agent/POpsAgent.csproj -c Release -r win-x64 --self-contained true -o dist/POps
dotnet publish Agent/POpsWatchdog/POpsWatchDog/POpsWatchDog.csproj -c Release -r win-x64 --self-contained true -o dist/POps
dotnet publish Agent/POpsUpdater/POpsUpdater/POpsUpdater.csproj -c Release -r win-x64 --self-contained true -o dist/POps
dotnet publish Agent/POpsTray/POpsTray.csproj -c Release -r win-x64 --self-contained true -o dist/POps
dotnet build Installer/agent/POps.Agent.Installer.wixproj -c Release -p:PopsPublishDir=<full path to dist\POps>\ -p:ProductVersion=0.1.2.1
```

Publish the tray **last**: the four programs share the runtime files in one folder, and the tray (Windows Forms) needs the WindowsDesktop builds of `System.Drawing.dll` and `Microsoft.VisualBasic.dll`; the others accept them, the reverse breaks the tray. The release workflow checks this. Without `ProductVersion` the version comes from the root `VERSION` file with `.0` as the fourth field. The WiX toolset and its extensions are NuGet packages pinned in the project files; nothing needs to be installed globally.
