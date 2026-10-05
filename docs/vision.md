# POpsVision (screen view and remote control)

POpsVision lets an admin see a PC's screen and, within a recorded session, control its mouse and keyboard. It is
built for remote support and supervision in a transparent way: the person at the PC is asked, or at least
clearly told, and every session is recorded with its reason.

## Components

- **Panel:** the **Uzak ekran** page (`Dashboard/vision.php`).
- **Backend:** the remote-control session endpoints and the `/ws/panel` and `/ws/vision/{hw_id}` WebSockets
  (`Backend/pops/routers/control.py`, `Backend/pops/manager.py`).
- **Agent:** the `POpsAgent` service opens the Vision WebSocket and relays frames and input; **POpsTray**, running
  in the user's session, asks for consent, captures the screen and applies remote input. The two talk over the
  local named pipe `POpsTrayPipe`, and the service accepts only the installed `POpsTray.exe` on it.

Older releases had a separate `POpsVision.exe`. It has not been built into releases or installed since 0.1.2-alpha,
and its source was removed in 0.1.14-alpha. Screen capture is done by the tray.

## Previews (thumbnails)

When you pick a lab on the **Uzak ekran** page, the wall requests a preview of each online PC's screen;
**Ekranları tazele** requests them again, and **Görüntüyü tazele** in the focus view refreshes one PC.

- Previews are available to `admin` and `superadmin` only. Viewers do not receive them.
- They do not need a session and do not ask the user. Instead, every preview updates the tray icon's tooltip
  ("POps - Son ekran önizlemesi: HH:mm"), and the tray shows a privacy notice at most once every five minutes.
- Requests go over `/ws/panel` (`remote_input` with `action: "get_thumbnail"`) or `GET /api/thumbnail/{pc_name}`.

## Live session

1. In the focus view of a PC, the admin turns on **Canlı izle**. The panel asks for the session type and a reason:
   - **Kullanıcıya sor**: the user must accept.
   - **Zorunlu müdahale**: no consent; a reason is required and the user sees a countdown.
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
7. Turning **Canlı izle** off, or leaving the page, calls `GET /api/stream/stop/{pc}` (the tray stops capturing
   and the Vision socket closes) and `POST /api/audit/session/end`.

The tray never logs remote keystrokes; its log records message types only. The audit log records that a session
was opened, not what was typed.

## Vision v2: capture and transport (agent 0.1.23+)

Used only when the server lists `vision_binary` in `server_info.features`; otherwise the agent keeps sending
full JPEG frames as JSON (`stream_frame`, base64), as before.

- **Capture (tray, user session):**
  - DXGI Desktop Duplication per screen, with GDI as the fallback where DXGI is not available (RDP, some
    virtual machines, a secure desktop).
  - The capture thread runs per-monitor DPI aware, so all coordinates are physical pixels.
- **Changed regions:** After a full frame, only changed regions are sent. They are found by comparing 64×64
  tiles with what the viewer already has; touching tiles are merged. If more than 8 regions remain or they cover
  more than half the screen, a full frame is sent instead.
- **Screens:**
  - The tray reports `{"type": "monitors", "list": [{"index", "width", "height", "primary"}]}` when the stream
    starts and after a display change.
  - `{"action": "select_monitor", "index": n | "all"}` picks a screen (default: primary). `all` puts every screen
    side by side in one image (monitor byte `0xFF`).
- **Quality:**
  - `{"action": "set_quality", "quality": 30–75, "scale": 0.5–1.0, "fps": 1–10}` sets the upper limits.
    Out-of-range values are clamped; missing ones default to 60, 1.0 and 5.
  - When the service has to drop a frame (the previous one is still being sent: frames are dropped, never
    queued), the tray lowers the JPEG quality, then the scale. The next frame is full, and regions are held back
    until it has gone. After 5 s without drops the tray steps back toward the limits.
- **Frames:** Binary WebSocket messages with an 18-byte big-endian header:
  - kind: `0x01` full, `0x02` region, `0x03` cursor position (no image);
  - monitor;
  - sequence (u32);
  - x, y, w, h;
  - full output width and height;
  - then the JPEG.

  **Coordinates are always the output's real pixels.** At a scale below 1 the JPEG is smaller, and the viewer
  draws it into the rectangle (x, y, w, h). A cursor message carries the position in x, y with w = h = 0. Frames
  are at most 2 MB.
- **Remote mouse:** While v2 runs, `mouse_move` x, y are pixels of the selected screen (or of the side-by-side
  image). The tray maps them to the physical screen.
- **Clipboard:**
  - Text only, at most 64 KB.
  - Only while a session the PC user **accepted** is open (not a mandatory session that only showed a notice).
  - Both directions: the viewer sends `{"action": "clipboard", "text"}`, and the agent sends
    `{"type": "clipboard", "text"}` when the user copies text.
  - The tray shows "Pano paylaşıldı".
  - The Windows event log records only the direction and the length (event 1100), never the text.

## Diagnostics (Teşhis)

The **Diğer işlemler** menu of the focus view has **Teşhis komutları**, which opens the **Teşhis** dialog. Its commands are queued like any other
command (`POST /api/deploy_orchestration`): they run as SYSTEM through the task queue, are recorded with the user
who sent them, need no Vision session, and are refused on a PC whose terminal capability is off. The agent's reply
is shown in the dialog. The buttons are hidden for viewers.

| Button | Command on the PC |
| --- | --- |
| POps süreçlerini listele | `tasklist` filtered to POps processes |
| Son ajan günlüğünü oku | last 20 lines of the newest log in `C:\POpsLogs` |
| Ekran yakalamayı yeniden başlat | ends `POpsTray.exe`; the watchdog starts the tray again within about 10 seconds |
| Saati eşitle | `w32tm /resync` |
| Ajanı yeniden başlat | ends `POpsAgent.exe`; Windows restarts the service after about 10 seconds (asks for confirmation) |
| Bilgisayarı yeniden başlat | `shutdown /r /t 5` (asks for confirmation) |

**Ajanı yeniden başlat** ends the agent before it can answer; the task is marked `Completed (Rebooted)` when the
agent reconnects. The **Uzak ekran** page sends no commands on its own.

## Turning Vision off on a PC

The capability policy can disable Vision (streaming, previews and remote input) on a PC:

- at install time with `VISION_ENABLED=0` on the MSI, or
- from **Sistem** → "Cihaz yetenekleri" (`POST /api/system/set-capabilities`), which can only switch it off.

The agent then refuses Vision requests and reports `capability_denied`; a running stream is stopped. Switching
it back on requires a local administrator (MSI repair or reinstall with `VISION_ENABLED=1`). See
[`agent.md`](agent.md#capability-policy).

## Quarantine from the **Uzak ekran** page

The **Diğer işlemler** menu of the focus view also has **Karantinaya al** / **Karantinayı kaldır** (admins and superadmins). Quarantine (`lockdown`) shows a
full-screen lock on the PC and isolates its network except for the POps server, DNS and DHCP; see
[`agent.md`](agent.md#quarantine-and-offline-bypass). Admins can also quarantine PCs from **Cihazlar**, **Sınıflar**
and the PC detail panel ([`dashboard.md`](dashboard.md#working-with-pcs)).

## Requirements and limits

- Vision needs the tray to be running in a signed-in user session. With nobody signed in there is no screen to
  capture.
- Only the primary monitor is captured.
- Frames travel over the same TLS connection as the rest of the agent traffic (`wss://`).
- Enforcement: once `enforce_agent_auth` is on, `/ws/vision` connections without valid agent credentials are
  rejected (`4401`) and audited.
