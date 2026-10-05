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
   to the running stream (`set_fps`). With a Vision v2 agent the page offers 1, 2, 5 (default) and 10 FPS, a
   screen picker, image quality and scale, and a clipboard panel ([below](#vision-v2-server-relay-and-viewer)).
6. **Kontrol** (after the stream has started) sends mouse moves, clicks, wheel and key presses. The backend
   forwards them only for an admin with an open session for that PC, and the agent applies them only while a
   session that the tray started (after consent or the mandatory notice) is active.
7. Turning **Canlı izle** off, or leaving the page, calls `POST /api/stream/stop` (the tray stops capturing and
   the Vision socket closes) and `POST /api/audit/session/end`.

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

## Vision v2: server relay and viewer

The server side of Vision v2 (`Backend/pops/vision.py`, `pops/manager.py`, `pops/routers/control.py`) and the
viewer on the **Uzak ekran** page (`Dashboard/assets/pops_vision.js`, `Dashboard/vision.php`). The message
schemas and the frame header are in [`protocol/README.md`](protocol/README.md#vision-v2-binary-frames).

### Gating

- `server_info.features` lists `vision_binary` and `vision_clipboard`. An agent that does not see `vision_binary`
  keeps sending JSON `stream_frame`s, which the server relays as before; old and new agents work side by side,
  each on its own tunnel.
- A panel socket receives binary frames only after it announced that it can decode them:
  `{"type": "panel_hello", "features": ["vision_binary"]}` (the **Uzak ekran** page sends it when the socket
  opens). Other panel sockets of the same admin (for example **Uzak komut**) get no binary frames.

### Relay

- **Checks.** Every binary message on `/ws/vision/{hw_id}` is checked with the same rules as the agent's
  `VisionFrame.TryParse`: at most 2 MB, a known kind, monitor 0–15 or `0xFF`, a full frame that covers the whole
  output, a region inside it, a cursor with no size and no image, a JPEG signature. A bad message is dropped without
  an answer and counted in `/metrics` (`pops_events_total{event="vision_frames_malformed"}` and
  `vision_frames_oversize`; valid ones in `vision_frames_binary`); the first one per tunnel is logged. The tunnel
  stays open; a message the server fails to process (for example a database error) is logged and skipped, and only
  20 such failures within a minute close the tunnel (`1011`).
- **Who receives frames (F12).** Only panel sockets of an admin or superadmin who holds an open, unexpired session
  for that device, and only those that sent `panel_hello`. Viewers, other admins and panels without a session get
  nothing; with no such panel the frame is dropped.
- **Device identity.** A binary frame carries no device ID. The server puts the tunnel's authenticated device ID
  in front of it, so an agent cannot place frames under another device. (JSON `stream_frame`, `monitors` and
  `clipboard` likewise get the tunnel's ID; an `hw_id` the agent sends is ignored.)
- **Binary message on the panel socket:**

  | Bytes | Field |
  | --- | --- |
  | 1 | `0x01`: Vision frame (other values are reserved; the page ignores them) |
  | 1 | n: length of the device ID in bytes (1–255) |
  | n | device ID, UTF-8 (the tunnel's device) |
  | rest | the agent's message unchanged: 18-byte header and JPEG |

- **Slow panels drop frames instead of queueing them.** Each panel socket has its own send queue (0.1.14). Per
  device and output it keeps:
  - one full frame: a new full frame replaces everything still waiting for that output;
  - at most 32 regions or 4 MB of regions after it. A region that does not fit is dropped, the output is marked
    stale and its further regions are dropped until the next full frame, and the panel gets
    `{"type": "vision_resync", "hw_id", "monitor"}`;
  - only the latest cursor position.

  A panel whose send takes longer than 10 seconds, or that has more than 16 MB of binary frames waiting in total,
  is closed and the browser reconnects.
- **Screens.** `monitors` from the agent is checked, kept for the open tunnel and sent to the session holders as
  `{"type": "monitors", "hw_id", "list"}`. An admin who opens a session while the tunnel is already open gets the
  kept list.

### Viewer commands

The page sends `{"type": "vision_control", "device": "<hw_id>", "action": ...}` on its panel socket with
`select_monitor` (`index`), `set_quality` (`quality`, `scale`, `fps`) or `clipboard` (`text`). The server forwards
a command on the Vision channel only when

- the socket's user is an admin or superadmin (the role is re-read every 10 seconds) holding an open session for
  that device,
- the Vision module is on for the device's lab, and
- the values are in range (index 0–15 or `all`, quality 30–75, scale 0.5–1.0, fps 1–10, text up to 64 KB).

The agent receives only the contract's fields (`{"action": "select_monitor", "index": 1}` and so on), never the
panel's extra fields. `select_monitor` and `set_quality` extend the session's idle timeout like remote input.
Anything else is dropped silently; a refused clipboard text is answered with
`{"type": "clipboard_result", "hw_id", "ok": false, "reason"}` (`not_accepted`, `no_stream`, `invalid`, `rate`,
`module`, `audit`), a delivered one with `ok: true`.

### Viewer

- Frames are decoded in parallel (`createImageBitmap`) and drawn in arrival order, each into its rectangle on a
  canvas per output (screen index or `0xFF`). A region is drawn only on top of the full frame it belongs to:
  regions before any full frame, regions older (by sequence number) than the full frame below them, and regions
  whose output size differs from it are skipped. The cursor is drawn on top. The page shows the output of the
  latest frame, so it follows the agent after a screen switch.
- When regions are missing (a `vision_resync`, a JPEG that failed to decode, or more than 24 frames waiting to
  be decoded because the browser falls behind) the page stops drawing regions of that output and sends the current
  `select_monitor` again (for a `vision_resync` at once, otherwise at most every 3 seconds); the agent answers with
  a full frame. If none arrives the request is repeated every 3 seconds, at most five times, while that output is
  shown.
- **Controls** (shown when the agent sends binary frames or `monitors`): a screen picker (when there is more than
  one screen; **Tümü** = side by side), the frame rate (1, 2, 5, 10) and **Görüntü ayarları** (quality 30–75,
  scale 50/75/100 %), all sent as `set_quality`; the browser remembers them and sends them when a stream starts if
  they differ from the agent's defaults (60, 100 %, 5). The footer shows frames per second (regions of one capture
  count once) and kbit/s over the last 2 seconds, for old agents too.
- **Mouse:** positions are converted to pixels of the shown output (the selected screen or the side-by-side image)
  and sent in `remote_input` as before; the agent maps them to the physical screen. After a screen switch, mouse
  moves wait (up to 3 seconds) until the new screen is shown, because the agent maps them to its new selection.
  Old agents keep the previous conversion from the 75 % frame.
- **Pano** (clipboard panel): send text to the PC, and see and copy text the user copied on the PC. It is
  disabled in a mandatory session.
- Turning **Canlı izle** off keeps the last image as the preview, on the page and on the PC's card.

### Clipboard and privacy

- Text only, at most 64 KB (UTF-8). Empty text and anything longer is refused by the panel, the server and the
  agent.
- Only in a session the **PC user accepted**. The agent opens the Vision tunnel only after the user accepted a
  session (or a mandatory session's countdown ended), so when the tunnel opens the server looks at the device's
  open sessions: if there is exactly one and it was opened as **Kullanıcıya sor** (not **Zorunlu müdahale**), the
  tunnel belongs to it and its admin may use the clipboard. In every other case nobody may: a mandatory session,
  several sessions at that moment (the server cannot tell which one the user accepted), or a session that started
  while the tunnel was already open (refused with `not_accepted`). A reconnecting tunnel is judged the same way,
  and the right ends with the session or the tunnel. The agent checks consent on its side too and the tray shows
  "Pano paylaşıldı".
- Text copied on the PC goes only to that admin, never to other admins or viewers.
- At most 30 texts per minute per device in each direction.
- Every transfer is written to the hash-chained `device_audit_logs` as `clipboard` with the direction (`to_pc` or
  `from_pc`), the length in characters, the admin and the time, before the text is passed on; if the entry cannot be
  written the text is not passed on (`audit`). The text itself is never logged or stored.
- Frames, monitor lists and clipboard text are kept only in memory while they are forwarded; nothing is written to
  disk.

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
- Agents before Vision v2 capture only the primary monitor; a v2 agent streams any screen, or all side by side.
- Frames travel over the same TLS connection as the rest of the agent traffic (`wss://`).
- Enforcement: once `enforce_agent_auth` is on, `/ws/vision` connections without valid agent credentials are
  rejected (`4401`) and audited.
