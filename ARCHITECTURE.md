# POps System Architecture

POps is designed as a highly scalable, real-time endpoint management platform.

## 1. Web Dashboard (Frontend)
- **Tech:** PHP 8, Vanilla JS, CSS Custom Properties
- **Role:** The control center. It communicates entirely via the REST API provided by the Central Server.

## 2. Central Server (Backend)
- **Tech:** Python 3.9+, FastAPI, WebSockets, Uvicorn, PostgreSQL
- **Role:** The brain. It keeps one persistent WebSocket connection per endpoint and exposes REST endpoints for the dashboard. A single worker process is required, because connections are tracked in memory (see BENCHMARKS.md for measured capacity).

## 3. Windows Endpoint (Agent)
- **Tech:** .NET 8, C#, Windows Forms
- **Role:** The executor. Four shipped programs sharing one helper library (`POps.Shared`):
  - **POpsAgent:** The core Windows service (LocalSystem) maintaining the WebSocket connection and running commands.
  - **POpsTray:** The user-facing taskbar application; it also captures the screen for Vision over a named pipe.
  - **POpsWatchdog:** Runs in the user session and restarts the tray if it stops.
  - **POpsUpdater:** Installs signed MSI updates and rolls back if the new version does not come up healthy.
  - The older standalone `POpsVision.exe` was not shipped since 0.1.2-alpha; its source was removed in 0.1.14-alpha.
