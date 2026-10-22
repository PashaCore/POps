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
   - **Mandatory:** a full-screen notice counts down, then the session starts. If the user's desktop is not on
     screen when it ends (the PC is locked), the user gets the session notice on return
     ([below](#a-session-that-starts-while-the-pc-is-locked)).
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
    virtual machines). Neither captures the secure desktop; see
    [Secure desktop](#secure-desktop-uac-prompts-logon-screen).
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
- The secure desktop (UAC prompts, Ctrl+Alt+Del, the lock and sign-in screens) is neither shown nor controlled;
  the viewer gets a notice picture while it is up ([below](#secure-desktop-uac-prompts-logon-screen)).
- Agents before Vision v2 capture only the primary monitor; a v2 agent streams any screen, or all side by side.
- Frames travel over the same TLS connection as the rest of the agent traffic (`wss://`).
- Enforcement: once `enforce_agent_auth` is on, `/ws/vision` connections without valid agent credentials are
  rejected (`4401`) and audited.

## Secure desktop (UAC prompts, logon screen)

Windows shows some screens on a separate, protected desktop: the UAC prompt (while "Switch to the secure desktop
when prompting for elevation" is on, the Windows default), the Ctrl+Alt+Del screen, the lock screen and the
sign-in screen. It is the `Winlogon` desktop of the session's `WinSta0` window station. Only LocalSystem can open
it, and while it is up Windows sends the display, keyboard and mouse to it instead of the user's desktop. Vision
does not show or control it.

### What happens today

- **Detection:** before each frame the tray checks whether the input desktop (the one on screen) is the desktop
  it runs on (`OpenInputDesktop` with read access only, then its name). While the secure desktop is up the tray,
  running as the user, cannot open it.
- **Viewer:** instead of a frozen last picture the viewer gets a generated notice ("Güvenli masaüstü etkin", with
  an English line) in the normal frame format: a JPEG `stream_frame` on the JSON path, a full frame (`0x01`) with
  Vision v2. The tray sends it when the switch happens, again every 5 seconds so that a viewer that connects later
  also gets it, and with v2 whenever a full frame is needed (a dropped frame, `select_monitor`, a scale change).
  Nothing is read from the screen for it. The protocol is unchanged.
- **Return:** when the user's desktop comes back, capture continues. With v2 the tray reopens its DXGI sources
  (a duplication is lost on every desktop switch) and sends a full frame. A failed GDI capture (the desktop switched
  between the check and the capture) is retried on the next frame and does not end the v2 capture thread. The tray
  log records the switch and the return, never the picture.
- **Remote input:** the tray runs as the user on `WinSta0\Default`. While another desktop has the input, Windows
  refuses its `SendInput`, `mouse_event` and `SetCursorPos`; events that arrive meanwhile are dropped, not queued,
  and never reach the prompt. The admin cannot click **Yes** on a UAC prompt, type credentials into it, press
  Ctrl+Alt+Del or unlock the PC.
- **Previews:** a preview requested while the secure desktop is up gets no picture (the service waits 5 seconds
  for it).
- **Consent:** the consent dialog and the countdown are windows on the user's desktop, so a session requested while
  the PC is locked is seen only after the user returns. See
  [A session that starts while the PC is locked](#a-session-that-starts-while-the-pc-is-locked).
- **Nobody signed in** (sign-in screen after a restart or sign-out): there is no tray, so there is nothing to
  capture, and `start_vision_session` is not answered.

### A session that starts while the PC is locked

Owner decision 5: no silent session. A mandatory session that starts while the user's desktop is not on screen
shows its notice as soon as the desktop returns, for the rest of the session, and the PC records it.

- **Routine (Kullanıcıya sor):** cannot start while the PC is locked. The dialog waits on the user's desktop, with no
  time limit, and the session starts only after **Evet**. A countdown sent with a routine request is ignored.
- **Mandatory (Zorunlu müdahale):** the countdown runs on the hidden desktop and the session starts when it ends,
  as before. When the tray starts the session it checks whether the user's desktop is on screen (the same input
  desktop check as the capture). If it is not (lock screen, a UAC prompt, the Ctrl+Alt+Del screen), the tray marks
  the start (`START_VISION_TUNNEL:<fps>:locked` on the pipe) and, once the Vision tunnel is open, the service
  - writes event **1150** "Vision oturumu bilgisayar kilitliyken başladı" to the Windows event log (`session_id`,
    `requested_by`, `mandatory`; no reason, no screen content), once for that start, after 1010;
  - posts an ordinary event log entry to the server on the existing `POST /api/logs/{hw_id}` (`event_type`
    `agent.vision_locked_start`, category `vision`, `meta_data` with `session_id`, `requested_by`, `mandatory`). The
    server stores it like any agent log entry (`agent_logs_v2`); no protocol or server change;
  - tells the tray to set up the session notice, before capture starts.
- **While the desktop is away** the viewer gets the secure desktop notice picture, as before.
- **When the user's desktop returns** (the tray checks every half second), the tray first shows the notice, then
  lets capture continue, so the session sends no picture of the user's desktop before the notice is on it
  (previews are separate and unchanged):
  - a balloon: "Bilgisayarınız kilitliyken Bilgi İşlem yetkilisi *X* ekranınıza bağlandı …" with the session ID;
  - a banner at the bottom of the primary screen, always on top, which does not take focus and cannot be closed by
    the user: "Ekranınız Bilgi İşlem tarafından izleniyor (oturum bilgisayarınız kilitliyken başladı)", with the
    admin, the reason and the session ID. The admin sees it in the picture too.
- **Until the session ends.** Locking and unlocking again does not remove the banner. It closes when the session
  ends: `stop_stream`, the server closing the Vision tunnel, the command connection or the tray connection dropping,
  Vision switched off (capability or module). The tray then says "Bilgi İşlem oturumu sona erdi." A session that
  ends before the user returns shows nothing more.
- **Tray connection lost.** The service already ends the approved session when the tray disconnects; the tray now
  also stops capturing and closes the notice, so a session cannot go on without its notice after a reconnect.
- A session that starts while the user's desktop is on screen behaves as before: no 1150, no banner.
- Old tray with a new service: the tray does not send the flag, so nothing changes. New tray with an old service: the
  service ignores the flag (it reads only the frame rate); no 1150 and no banner.

### Why the secure desktop is not captured

1. **It needs a SYSTEM process inside the user's session.** Only LocalSystem can open the `Winlogon` desktop, and
   DXGI Desktop Duplication refuses it (`E_ACCESSDENIED`) to any other account. The service is LocalSystem but runs
   in session 0, which has its own window stations and cannot reach the console session's desktops (session 0
   isolation). Capturing the secure desktop means starting a second SYSTEM process in the user's session, which
   undoes that isolation on purpose. Any bug in it (frame encoding, message parsing, a library loaded from the
   wrong place) is a local privilege escalation, and the student at the PC is the person best placed to try one.
2. **Input there is elevation.** An administrator approves a UAC consent prompt with one click; the secure desktop
   exists so that no program can make that click. A remote click sent by a SYSTEM helper is exactly such a program.
   For a standard user the prompt asks for an administrator's password, which the admin would type remotely.
   Whether a Vision session may approve elevation is a policy decision before it is a technical one.
3. **Ctrl+Alt+Del needs a security setting.** `SendSAS` works only when the `SoftwareSASGeneration` policy
   ("Disable or enable software Secure Attention Sequence") allows services; Windows ships with it off. Turning it
   on for every PC weakens the secure attention sequence and is not something the agent should change.
4. **On the sign-in screen there is nobody to ask.** A routine session needs the user's yes, and a mandatory one
   tells the user with a countdown. With nobody signed in there is neither a user nor a tray, and a notice on the
   secure desktop would need yet another SYSTEM window there. Even with a user signed in, the lock screen can show
   notification previews, the user name and e-mail address; whether consent to "my screen" covers that is a
   privacy (KVKK) question.
5. **It cannot be tested where it is written.** The parts that matter (a SYSTEM process in the console session,
   following desktop switches, DXGI on `Winlogon`, input to a UAC prompt) run only as SYSTEM on a real secure
   desktop, that is, by hand on a lab PC. Unit tests can cover the decisions around them, not the mechanism. A
   half-tested SYSTEM component is worse than this documented limit.
6. **The binaries are not Authenticode-signed yet** ([`code-signing.md`](code-signing.md)). The tray pipe checks
   the image path and checks a signature only once one exists. A SYSTEM helper should require a valid signature in
   both directions, so it should not ship before signing.

### Design for a later implementation

**Components**

- `POpsSecureDesktop.exe`, new, in the install folder, signed. No window, no tray icon, no network, and no file,
  registry or process access beyond what is listed here. A dedicated capture thread (no windows or hooks, so
  `SetThreadDesktop` succeeds) follows the input desktop with `OpenInputDesktop` + `SetThreadDesktop`, captures with
  DXGI Desktop Duplication (recreated after each `DXGI_ERROR_ACCESS_LOST`) or GDI `BitBlt` from that desktop, and
  encodes Vision v2 frames (same header, same 2 MB limit). It captures only while the input desktop is `Winlogon`;
  on the user's desktop it sends nothing and the tray captures as today (least privilege).
- In the service: a launcher and a second pipe. Frames go through `VisionRelay` as today, so the viewer needs no
  change. Today's notice picture stays as the fallback when the helper is not allowed or fails.

**Starting it**

- Only the service starts it, only during an approved Vision session (`_visionSessionApproved`, Vision capability
  and module on), and only after the tray reports that the secure desktop is up.
- Token and process: `DuplicateTokenEx` of the service's own LocalSystem token (primary token),
  `SetTokenInformation(TokenSessionId)` with the console session from `WTSGetActiveConsoleSessionId` (needs
  `SeTcbPrivilege`, which LocalSystem has), then `CreateProcessAsUser` with `lpDesktop = "winsta0\winlogon"`, no
  inherited handles and no user environment block (the user's `DOTNET_*` variables must not reach a SYSTEM
  process). Built with `StartupHookSupport=false`, like the tray.
- Lifetime: never longer than the session. The service ends it on `stop_stream`, session end, tray disconnect,
  Vision capability or module off, and service stop. The helper exits on its own when its pipe closes, when no
  keep-alive arrives for a few seconds, or when the user's desktop returns.

**Pipe**

- A separate pipe (for example `POpsSecureDesktopPipe`) created by the service, with LocalSystem as the only entry
  in its ACL and as its owner: no user process can open it.
- The service accepts a client only if its image path is the installed helper, its token user is `S-1-5-18`, its
  session is the console session and its Authenticode signature is valid (required, not "when signed"). The helper
  checks that the pipe's owner is SYSTEM (`PipeOwner`).
- Messages: service to helper `START:fps`, `QUALITY:q,s,fps`, `SELECT:n|all`, `STOP`, and validated input events
  only if input is allowed; helper to service: v2 frames (checked with `VisionFrame.TryParse` as today) and
  `DESKTOP:winlogon|other`. Anything else closes the pipe. Messages are limited to the frame size (2 MB), not the
  tray pipe's 32 MB.

**Consent and audit**

- Same rules as today: the helper runs only inside a session that the user accepted, or a mandatory session whose
  notice was shown. For a mandatory session the notice should have been shown while the user's desktop was on
  screen (the tray reports that), so that a locked PC is never watched without the user having seen the countdown.
- The clipboard stays off on the secure desktop.
- Local audit (Windows event log, `POps Agent`): 1160 "secure desktop shown" (`session_id`, `requested_by`,
  `user_approved`, input on or off) and 1161 "secure desktop ended" (with the duration). The server records the
  same through an additive agent message (for example `vision_secure_desktop` with `state` `started` / `ended`) in
  `device_audit_logs`.
- Afterwards the tray tells the user that the admin also saw the secure desktop.
- Input to the secure desktop is off by default and can be switched on only locally (an MSI property like
  `VISION_ENABLED`, which the server can switch off but not on).

**Threat model**

| Who | Tries to | Stopped by |
| --- | --- | --- |
| Student at the PC | Open the helper pipe or feed it messages | Pipe ACL: LocalSystem only |
| Student at the PC | Run a fake helper or replace the exe | Only the service starts it, from the install folder; image path, SYSTEM token, session and signature checks |
| Student at the PC | Load code into it (DLL, startup hook, environment) | Install folder writable by administrators only; no user environment; `StartupHookSupport=false` |
| Student at the PC | Window messages to it | It has no windows; UIPI blocks lower-integrity senders |
| Student at the PC | Keep it running after the session | Lifetime bound to the session and a keep-alive |
| Compromised tray | Report a desktop switch that did not happen | The helper checks the input desktop itself and only runs inside an approved session |
| Compromised server | Watch the lock or sign-in screen, or approve UAC prompts, without consent | Same consent flow; `VISION_ENABLED=0` capability lock; input only if enabled locally; local audit 1160/1161 independent of the server |
| Bug in the helper | Crash or misparse | Fixed message set, strict parsing, no file or shell access; it exits on any error and the viewer falls back to the notice |

**Tests**

- Unit tests with seams (CI): the start decision (approved session, capability, module, secure desktop reported);
  the token and start parameters (session, desktop name, flags) built by a pure function; client verification with
  fake path, SID, session and signature; the message parser (allowed set, sizes, malformed input closes the pipe);
  lifetime (session end, tray disconnect, capability off, keep-alive timeout); audit events 1160/1161; the relay
  forwarding helper frames only in an approved session.
- By hand on a lab PC, never on a developer's machine: UAC consent and credential prompts, the Ctrl+Alt+Del screen,
  Win+L and unlock, sign-out and the sign-in screen, fast user switching, an RDP session (no console), two monitors,
  a virtual machine without DXGI (GDI). Kill the service and check that the helper exits; try to open the pipe as a
  standard user and as an administrator; in Process Explorer check token SYSTEM, the user's session, desktop
  `Winlogon`, no network connections and no child processes; check events 1160/1161.

### Decisions for the owner

1. May Vision show the secure desktop at all (lock screen notifications, user name, KVKK)?
2. View only, or also input (approving UAC prompts, typing an administrator's password remotely)? Recommended:
   view only first.
3. Ctrl+Alt+Del: turning on `SoftwareSASGeneration` on every PC. Recommended: no.
4. The sign-in screen with nobody signed in: not supported, or mandatory sessions only, with a reason and a notice
   drawn on the secure desktop?
5. ~~A mandatory session that starts while the PC is locked shows its countdown on the hidden user desktop. Should
   the countdown wait until the user's desktop is on screen?~~ **Decided and implemented:** the session is not
   held back, but it is never silent. The session notice is shown as soon as the user's desktop returns and stays
   until the session ends, and the PC writes "started while locked" (event 1150) to its local audit; see
   [A session that starts while the PC is locked](#a-session-that-starts-while-the-pc-is-locked).
6. Authenticode signing before a SYSTEM helper ships.

Until then, for tasks that need elevation use remote commands (**Uzak komut**, `execute`), which run as SYSTEM and
are audited, rather than a UAC prompt inside a Vision session.
