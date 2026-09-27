# POpsVision (screen view and remote control)

POpsVision lets an admin see a PC's screen and, within a recorded session, control its mouse and keyboard. It is
built for remote support and supervision in a transparent way: the person at the PC is asked, or at least
clearly told, and every session is recorded with its reason.

## Components

- **Panel:** the **POpsVision** page (`Dashboard/vision.php`).
- **Backend:** the remote-control session endpoints and the `/ws/panel` and `/ws/vision/{hw_id}` WebSockets
  (`Backend/pops/routers/control.py`, `Backend/pops/manager.py`).
- **Agent:** the `POpsAgent` service opens the Vision WebSocket and relays frames and input; **POpsTray**, running
  in the user's session, asks for consent, captures the screen and applies remote input. The two talk over the
  local named pipe `POpsTrayPipe`, and the service accepts only the installed `POpsTray.exe` on it.

Older releases had a separate `POpsVision.exe`. It is no longer built into releases or installed; the source in
`Agent/POpsVision` is legacy. Screen capture is done by the tray.

## Previews (thumbnails)

When you open a lab on the POpsVision page, the wall view requests a preview of each online PC's screen;
**Ekranları Tazele** requests them again, and **Tazele** in the focus view refreshes one PC.

- Previews are available to `admin` and `superadmin` only. Viewers do not receive them.
- They do not need a session and do not ask the user. Instead, every preview updates the tray icon's tooltip
  ("POps - Son ekran önizlemesi: HH:mm"), and the tray shows a privacy notice at most once every five minutes.
- Requests go over `/ws/panel` (`remote_input` with `action: "get_thumbnail"`) or `GET /api/thumbnail/{pc_name}`.

## Live session

1. In the focus view of a PC, the admin turns on **Canlı Yayın**. The panel asks for the session type and a reason:
   - **Rutin Uzaktan Destek (Kullanıcı Onayı İster)**: the user must accept.
   - **Zorunlu Müdahale (Anında Bağlan)**: no consent; a reason is required and the user sees a countdown.
2. The panel calls `POST /api/audit/session/start`. The backend
   - records the session (who, target, reason, mandatory or not) in `enterprise_audit_logs` and in the
     hash-chained `device_audit_logs`,
   - grants this admin a session for this PC, valid until 30 minutes pass without activity,
   - sends `start_vision_session` to the agent with a countdown: 30 seconds for a mandatory session (5 seconds
     if the PC is quarantined), none for a routine one.
3. On the PC:
   - **Routine:** a dialog names the admin and the reason and asks whether to accept. If the user declines, the
     panel is told (`vision_rejected`) and nothing is captured.
   - **Mandatory:** a full-screen notice counts down, then the session starts.
4. The agent opens `/ws/vision/{hw_id}` (with the same credentials as its command channel) and the tray starts
   capturing the primary screen as JPEG. Frames are relayed to the backend, which forwards them **only** to the
   panel of the admin who holds the session. If that admin's panel is not connected, frames are dropped.
5. Frame rate: the page offers **1, 2 (default) and 5 FPS**; the tray caps capture at 5 FPS. Changing it applies
   to the running stream (`set_fps`).
6. **Kontrol** (after the stream has started) sends mouse moves, clicks, wheel and key presses. The backend
   forwards them only for an admin with an open session for that PC, and the agent applies them only while a
   session that the tray started (after consent or the mandatory notice) is active.
7. Turning **Canlı Yayın** off, or leaving the page, calls `GET /api/stream/stop/{pc}` (the tray stops capturing
   and the Vision socket closes) and `POST /api/audit/session/end`.

The tray never logs remote keystrokes; its log records message types only. The audit log records that a session
was opened, not what was typed.

## Diagnostics (Teşhis)

The focus view has a **Teşhis** button that opens "Uç Nokta Teşhisi". Its commands are queued like any other
command (`POST /api/deploy_orchestration`): they run as SYSTEM through the task queue, are recorded with the user
who sent them, need no Vision session, and are refused on a PC whose terminal capability is off. The agent's reply
is shown in the dialog. The buttons are hidden for viewers.

| Button | Command on the PC |
| --- | --- |
| POps süreçlerini listele | `tasklist` filtered to POps processes |
| Son logları oku | last 20 lines of the newest log in `C:\POpsLogs` |
| Ekran yakalamayı yeniden başlat | ends `POpsTray.exe`; the watchdog starts the tray again within about 10 seconds |
| Zamanı eşitle | `w32tm /resync` |
| Ajanı yeniden başlat | ends `POpsAgent.exe`; Windows restarts the service after about 10 seconds (asks for confirmation) |
| PC'yi yeniden başlat | `shutdown /r /t 5` (asks for confirmation) |

**Ajanı yeniden başlat** ends the agent before it can answer; the task is marked `Completed (Rebooted)` when the
agent reconnects. The POpsVision page sends no commands on its own.

## Turning Vision off on a PC

The capability policy can disable Vision (streaming, previews and remote input) on a PC:

- at install time with `VISION_ENABLED=0` on the MSI, or
- from **Sistem & Sürüm** → "Cihaz yetenekleri" (`POST /api/system/set-capabilities`), which can only switch it off.

The agent then refuses Vision requests and reports `capability_denied`; a running stream is stopped. Switching
it back on requires a local administrator (MSI repair or reinstall with `VISION_ENABLED=1`). See
[`agent.md`](agent.md#capability-policy).

## Quarantine from the POpsVision page

The focus view also has **Karantinaya Al** / **Karantinayı Kaldır** (shown to superadmins). Quarantine (`lockdown`) shows a
full-screen lock on the PC and isolates its network except for the POps server, DNS and DHCP; see
[`agent.md`](agent.md#quarantine-and-offline-bypass).

## Requirements and limits

- Vision needs the tray to be running in a signed-in user session. With nobody signed in there is no screen to
  capture.
- Only the primary monitor is captured.
- Frames travel over the same TLS connection as the rest of the agent traffic (`wss://`).
- Enforcement: once `enforce_agent_auth` is on, `/ws/vision` connections without valid agent credentials are
  rejected (`4401`) and audited.
