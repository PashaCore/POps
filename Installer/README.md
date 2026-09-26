# POps Installer

Scripts and resources for deploying and installing POps.

- `agent/` — Windows MSI for the agent (WiX 5): `Package.wxs`, the build project and the custom actions that write settings and secrets.
- `server/` — reference copy of the backend deploy script.

## Agent MSI

The release workflow builds `POps-Agent-<version>-win-x64.msi`, attaches it to every GitHub release and lists it with its SHA-256 in the signed `manifest.json`. It needs the .NET 8 Desktop Runtime (x64) and refuses to start without it.

### Install

```
msiexec /i POps-Agent-<version>-win-x64.msi /qn /l*v C:\POpsLogs\msi-install.log SERVER_URL=https://pops.example.com ENROLL_TOKEN=<token from the panel>
```

| Property | Needed | Written to |
| -------- | ------ | ---------- |
| `SERVER_URL`    | first install | `appsettings.json` → `ServerUrl` (in the install folder). Must be `https://`; plain `http://` is accepted only for `127.0.0.1` / `localhost`. |
| `ENROLL_TOKEN`  | recommended   | `C:\POpsData\secure\enroll.token` |
| `BYPASS_SECRET` | optional      | `C:\POpsData\secure\bypass.secret` |
| `PERSIST_DIR`   | optional      | `appsettings.json` → `PersistDir` (see `Agent/README.md`, machines with freeze software) |
| `INSTALLFOLDER` | optional      | install folder, default `C:\Program Files\POps` |

- Every property is optional on an upgrade: a value that is not given keeps the installed one. A first install without `SERVER_URL` (and without an old install to take it from) fails with a clear message in the log.
- A plain `http://` server address is refused, including one migrated from an older install. Over `ws://` the device secret, the enrollment token and the commands the agent runs as SYSTEM would cross the network in clear text. Pre-MSI installs that used `http://<ip>:8000` therefore need `SERVER_URL=https://…` on the command line.
- `ENROLL_TOKEN` and `BYPASS_SECRET` are hidden from the MSI log. A command line is still visible to other logged-on users while `msiexec` runs, so install from a deployment tool (GPO, Intune, the panel's remote command) or while no student is signed in.
- The server treats an enrollment token as single-use, so each device needs its own token.
- `appsettings.json` and the secret files are written by a custom action, not installed as MSI files, so they survive upgrades. `appsettings.json` is readable only by SYSTEM and Administrators.

### What the package does

- Installs the agent, tray, watchdog and updater into `C:\Program Files\POps` and registers the `POpsAgent` service (LocalSystem, automatic start; Windows restarts it on failure).
- Starts the tray in every user session through `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run` (`POpsTray`). The tray no longer writes its own per-user Run entry. An Active Setup entry runs the tray once per user at their next sign-in, before the desktop, in a mode that only deletes the per-user entry older versions left (`POpsTrayApp`), so an old tray cannot start next to the new one.
- **Upgrades** replace the installed version inside one transaction: the old product is removed right after `InstallInitialize`, and if the new version fails to install Windows Installer restores the old one. The MSI version is `a.b.c.<CI run number>`; a build with the same `a.b.c` and a higher run number is treated as an upgrade. Installing an older version over a newer one is refused, except with `POPS_ROLLBACK=1`, which only `POpsUpdater` passes when it rolls back: the older package then removes the newer version inside the same transaction, so a failed rollback leaves the newer version installed instead of leaving the machine without an agent.
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
dotnet publish Agent/POps.Agent/POps.Agent/POpsAgent.csproj -c Release -r win-x64 --self-contained false -o dist/POps
dotnet publish Agent/POpsTray/POpsTray.csproj -c Release -r win-x64 --self-contained false -o dist/POps
dotnet publish Agent/POpsWatchdog/POpsWatchDog/POpsWatchDog.csproj -c Release -r win-x64 --self-contained false -o dist/POps
dotnet publish Agent/POpsUpdater/POpsUpdater/POpsUpdater.csproj -c Release -r win-x64 --self-contained false -o dist/POps
dotnet build Installer/agent/POps.Agent.Installer.wixproj -c Release -p:PopsPublishDir=<full path to dist\POps>\ -p:ProductVersion=0.1.2.1
```

Without `ProductVersion` the version comes from the root `VERSION` file with `.0` as the fourth field. The WiX toolset and its extensions are NuGet packages pinned in the project files; nothing needs to be installed globally.
