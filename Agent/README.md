# POps Agent

Windows endpoint agent (.NET 8). Includes the main agent, tray application, remote vision service, and watchdog.

## Configuration

All components read their settings from `C:\POps\appsettings.json`. No server address or secret is compiled into the binaries; each value can also be supplied as a system environment variable, which takes precedence over the file.

| `appsettings.json` key | Environment variable | Description |
| ---------------------- | -------------------- | ----------- |
| `ServerUrl`            | `POPS_SERVER_URL`    | POps backend URL, e.g. `http://pops.example.com:8000`. Falls back to `http://127.0.0.1:8000` when unset. |
| `BypassSecret`         | `POPS_BYPASS_SECRET` | Shared secret used to verify offline quarantine bypass codes. Offline bypass is disabled when empty. |
