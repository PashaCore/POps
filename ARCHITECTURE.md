# POps System Architecture

POps is designed as a highly scalable, real-time endpoint management platform.

## 1. Web Dashboard (Frontend)
- **Tech:** PHP 8, Vanilla JS, CSS Custom Properties
- **Role:** The control center. It communicates entirely via the REST API provided by the Central Server.

## 2. Central Server (Backend)
- **Tech:** Python 3.12, FastAPI, WebSockets, Uvicorn, PostgreSQL
- **Role:** The brain. It maintains thousands of persistent WebSocket connections to the endpoints and exposes REST endpoints for the dashboard.

## 3. Windows Endpoint (Agent)
- **Tech:** .NET 8, C#, Windows Forms
- **Role:** The executor. A 5-component modular system:
  - **POpsAgent:** The core background service maintaining the WebSocket heartbeat.
  - **POpsTray:** The user-facing taskbar application.
  - **POpsVision:** The 1-5 FPS screen streaming and remote I/O module.
  - **POpsWatchdog:** A resilience service ensuring the agent stays alive.
  - **POpsUpdater:** Handles seamless OTA (Over-The-Air) updates.
