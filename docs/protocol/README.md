# Agent ↔ server protocol

This directory is the machine-readable definition of what a POps agent and the backend say to each other over
their WebSockets. It is written from the code (`Backend/pops/routers/agents.py`, `routers/control.py`,
`pops/taskqueue.py`, `pops/winget.py`, `routers/system/` and the Windows agent's `Worker.cs`), and a unit test keeps it
in step with that code. Any agent implementation (the Windows agent, the Linux agent) is expected to pass
the same test vectors.

| Path | Content |
| --- | --- |
| [`agent-to-server/<type>.json`](agent-to-server/) | One JSON Schema (draft 2020-12) per message an agent sends. |
| [`server-to-agent/<action>.json`](server-to-agent/) | One JSON Schema per message the server sends. |
| [`release-manifest.json`](release-manifest.json) | The signed `manifest.json` that `update_agent` carries. |
| [`examples/`](examples/) | Valid example messages (shared test vectors), one message per file. |
| [`AGENT_TESTS.md`](AGENT_TESTS.md) | How the agent test projects load the vectors. |

The agent's HTTP calls (inventory, event log, policy, software, Windows Update, help desk) are not part of this
directory: they are ordinary REST endpoints with Pydantic request models (`Backend/pops/models.py`), listed in
[`../api.md`](../api.md#agent-facing-http-endpoints). They use the headers `X-Agent-Id` and `X-Agent-Secret`.

## Transport

An agent opens up to two WebSockets to the server's own origin (the reverse proxy forwards `/ws/` to the
backend):

| Channel | URL | Open | Carries |
| --- | --- | --- | --- |
| Command | `wss://<server>/ws/agent/<hw_id>` | always; reconnects with backoff | heartbeats, commands, results |
| Vision | `wss://<server>/ws/vision/<hw_id>` | only during an approved remote-control session | `stream_frame` or binary frames, `monitors`, `clipboard` up; `remote_input`, `select_monitor`, `set_quality`, `clipboard` down |

- `<hw_id>` is the device ID (`HW-` and 12 characters). The ID in the URL is the identity of the connection;
  IDs inside messages never authorize anything.
- Each WebSocket text frame holds exactly one JSON object, UTF-8. Binary WebSocket messages are used only for
  screen frames on the Vision channel, and only by an agent whose server lists `vision_binary` (see
  [Vision v2 binary frames](#vision-v2-binary-frames)). The server accepts messages up to 16 MiB (uvicorn's
  default; a binary frame at most 2 MB); the Windows agent accepts up to 8 MiB on the command channel and 1 MiB on
  the Vision channel.
- An agent must not connect over plain `ws://` to a non-loopback server: the device secret travels in a header.

### Headers

| Header | Channel | Meaning |
| --- | --- | --- |
| `X-Agent-Secret` | both | Device secret received in `set_secret`. The server stores only its SHA-256. |
| `X-Enroll-Token` | command | Enrollment token from the panel, until the agent has a device secret. Never on the Vision channel. |
| `X-Agent-Version` | command (the Windows agent sends it on both) | The agent's release version. Stored per device and used for the version gates below. |
| `X-Agent-Platform` | command | `linux` from the Linux agent ([`Agent-Linux/`](../../Agent-Linux/README.md)); stored in `clients.platform`. Missing = `windows`. |
| `X-Agent-Features` | command | Optional. Comma-separated features the agent implements (lowercase `[a-z0-9_]`, at most 32 names), for example `winget`. Stored per connection; see [Agent features](#agent-features). |
| `X-Agent-Peer-Cache` | command | Optional, read only with the `peer_cache` feature. `port=<1024-65535>; ip=<LAN IPv4>; link=wired\|wireless`, every part optional: the port of the agent's cache server (default 8817), the address other PCs in the lab should use, and the link type (wired PCs are preferred as seeds). Kept in memory for that connection. |

An agent may send both `X-Agent-Secret` and `X-Enroll-Token`; the server checks the secret first.

### Close codes

| Code | Sent by | Meaning |
| --- | --- | --- |
| `4401` | server | Authentication failed (no or wrong secret while it is required, unusable enrollment token, enrollment without hardware data in the first message, re-enrollment of a device that has a secret). The Windows agent waits 60 s plus backoff. On the Vision channel it does not retry. |
| `4409` | server | The same ID and secret are connected from other hardware right now (copied installation). The Windows agent waits 10 minutes plus backoff. |
| `4000` | server | The device was deleted in the panel. |
| `1011` | server | Server error during the handshake, or 20 messages in one minute that could not be processed. |

### Command channel sequence

1. The agent connects with its headers. The server accepts the socket and checks the credentials before it reads
   anything; it may close with `4401` here.
2. The agent sends its **first message: a heartbeat with `dna_payload`**. The server registers the device from it.
   It may answer, in this order:
   1. `set_identity` (only without a valid device secret: a copied installation gets a new ID, a reinstalled PC
      gets its old one back),
   2. `set_secret` (only on an enrollment-token connection),
   3. `server_info` (always; from here on the connection is registered and receives commands),
   4. `set_bypass_secret` (secret connections, agent version 0.1.12 or newer, until acknowledged),
   5. `get_hardware` (when the server has no hardware inventory for the device),
   6. queued commands (`execute`; `winget_install`, `power` and `user_message` for an agent that announced
      `winget`, `power` and `message`),
   7. `exam_mode` when the device's lab has a running exam, or `exam_mode` with `enabled: false` when an exam the
      device may still apply ended early or the device left its lab,
   8. a re-sent `lockdown`/`unlock` if one is pending.
3. The first message is then also handled like any other heartbeat.
4. From then on the agent sends a heartbeat every 5 seconds, `capabilities` after the first heartbeat and after
   every change, results when they are ready and `update_result` while one is unacknowledged.

## Message direction and naming

**The rule:** a message from the agent to the server names itself in `type`; a message from the server to the
agent names itself in `action`. New messages follow this rule. Field names are `snake_case`.

Two messages predate the rule and keep their shape:

- **`heartbeat`** has no `type` in the Windows agent. The server recognises a heartbeat by `status`: any message
  whose `type` is not one of the types below and that carries `status` is a heartbeat, and every server release
  works this way. An agent may therefore send `"type": "heartbeat"`; the server ignores it.
- **`remote_input`** travels from the server to the agent with `"type": "remote_input"`, because the server
  forwards a panel message. Its sub-command is in `action` (`get_thumbnail`, `set_fps`) or `input_type` (mouse and
  keyboard).

## Versioning

### Protocol version

Everything in this directory is **protocol 1**: what the server and the Windows agent implement today (up to
0.1.21-alpha, and from 0.1.22-alpha `server_info` states it as `"protocol": 1`). A `server_info` without
`protocol` means 1.

The protocol version changes only for an incompatible change: removing or renaming a message or a field,
changing a field's type or meaning, making an optional field required, or changing the handshake or
authentication. Such a change needs a new major protocol version that the server announces in `server_info`, and
a server must keep serving agents of the previous version for as long as they are supported.

Additive changes keep protocol 1 and are negotiated as described below.

### Server features

`server_info.features` lists optional server behaviours an agent may rely on **for that connection**. Features in
use:

| Feature | Since | Meaning for the agent |
| --- | --- | --- |
| `result_ack` | 0.1.14-alpha | Every `result` with an integer `task_id` is answered with `result_ack` once it is stored. Keep each result (on disk) until its `result_ack` arrives and send unacknowledged results again after a reconnect. |
| `update_result_ack` | 0.1.14-alpha | An `update_result` with a `result_id` is answered with `update_result_ack` after it is stored. Keep the update result until then. |
| `update_progress` | 0.1.22-alpha | The server reads `update_progress` stages and shows them in the panel. Send them only to a server that lists this feature (older servers drop them anyway). |
| `file_transfer` | 0.1.23-alpha | The server sends `file_push` / `file_pull`, reads `file_result` and serves `GET /api/files/{id}/download` and `POST /api/files/{id}/upload` (device secret headers, one-time token). Report `files_enabled` in `capabilities`; a server without this feature never sends file commands. |
| `exam_mode` | 0.1.23-alpha | The server sends `exam_mode` for the device's lab and reads `exam_state`. Report `exam_state` after connecting and on every change; older servers send no `exam_mode` and drop `exam_state`. From 0.1.24 `exam_mode` also carries `now`, the server's time: take `until - now` as the time left and count it on a monotonic clock (at most 8 hours); without `now` compare `until` with the PC clock. |
| `winget` | 0.1.23-alpha | The server may send `winget_install` to an agent that announced `winget` in `X-Agent-Features`, and reads that header. Nothing for the agent to wait for: it is sent `winget_install` only if it announced the feature. |
| `vision_binary` | 0.1.23-alpha | The Vision channel takes binary frames (below) and `monitors`, and forwards `select_monitor` and `set_quality` from the viewer. Without it send only JSON `stream_frame`s: an older server closes the Vision channel on a binary message. |
| `vision_clipboard` | 0.1.23-alpha | The server relays `clipboard` in both directions during a session the PC user accepted. Without it do not send `clipboard`. |
| `power` | 0.1.23-alpha | The server sends power actions as `power` to an agent that announced `power` in `X-Agent-Features`; without it, shutdown and restart still arrive as `execute` (`shutdown /s\|/r /f /t N`). |
| `message` | 0.1.23-alpha | The server sends `user_message` to an agent that announced `message`; without it nothing is sent. |
| `peer_cache` | 0.1.23-alpha | The server stages agent updates per lab and may put `peers` in `update_agent` for an agent that announced `peer_cache`. An agent may skip keeping and serving its package cache when the server does not list it. |

Rules for agents:

- Features are per connection. Until `server_info` arrives the features are unknown; if it has not arrived 15
  seconds after connecting, the server has no features (servers before 0.1.14 send no `server_info`). The Windows
  agent uses the features of the previous connection as a hint during those 15 seconds.
- Unknown feature names are ignored.

A new optional server behaviour gets a new feature name; it is not tied to the server version.

### Agent features

An agent announces the optional server messages it implements in the `X-Agent-Features` header of the command
connection. The server stores the list for that connection (`agent_versions.features`; empty without the header)
and sends such a message only to an agent that announced it. Features in use:

| Feature | Server behaviour |
| --- | --- |
| `winget` | `winget_install` is sent for WINGET tasks. For an agent without it the task becomes `Denied` (exit code -8, "[REDDEDİLDİ] Bu bilgisayardaki ajan winget kurulumunu desteklemiyor …") and nothing is sent, so an agent that would ignore the message never leaves a task `Running`. |
| `power` | `power` is sent for power tasks (shutdown, restart, logoff, lock). For an agent without it shutdown and restart go as the old `execute` command (`shutdown /s\|/r /f /t <max(delay,5)>`, on Windows with a `/c` note limited to letters, digits, spaces and `. , : ; ? ' ( ) -`, on Linux without a note); logoff and lock become `Denied` (exit code -8, "[REDDEDİLDİ] Bu bilgisayardaki ajan bunu desteklemiyor …") and nothing is sent. |
| `message` | `user_message` is sent for message tasks. For an agent without it the task becomes `Denied` (exit code -8) and nothing is sent. |
| `peer_cache` | Agent updates are staged per lab: one agent with the feature (the seed) gets `update_agent` first; when it reports a successful `update_result`, the other agents with the feature in that lab get `update_agent` with `peers`. Agents without it are updated at once, without `peers`. The agent's duties are in [`../agent.md`, "Peer cache contract"](../agent.md#peer-cache-contract). |
| `exam` | Informational: exam mode is gated by the `exam_state` answer (no answer within 20 s = not supported), not by the header. |
| `files` | Informational: file transfer is gated by `files_enabled` in `capabilities`. |

A new message whose effect matters and that old agents would ignore gets a feature name here instead of a version
threshold.

### Agent versions

Where the server must know what an agent understands and there is no feature name, it compares
`X-Agent-Version` with these thresholds:

| Agent version | Server behaviour |
| --- | --- |
| 0.1.12 or newer | `set_bypass_secret` is sent (older agents would not acknowledge it). |
| 0.1.13 or newer | A task still `Running` when the same agent process reconnects stays `Running` (the agent sends its result on the new connection); for older agents it becomes `Unknown`. |

Everything else is safe for every agent version, because agents ignore what they do not know. A second agent
implementation should report in `X-Agent-Version` the POps release whose protocol behaviour it implements;
a lower or unparsable version only switches these behaviours off.

### Rules for both sides

- **Unknown fields are ignored.** The schemas are open on purpose (`additionalProperties` is not restricted), so a
  newer peer may add fields. A receiver must not fail on a field it does not know.
- **Missing optional fields** mean the peer is older: handle their absence as described in the schema.
- **A field with the wrong type is treated as missing** (for example a `result` whose `task_id` is not an integer is
  dropped, a non-boolean `quarantined` is ignored).
- **Unknown messages:** the server ignores an agent message whose `type` it does not know (no answer, the connection
  stays open). An agent ignores a server message whose `action` it does not know. A message that is not a JSON
  object, or that the server cannot process, counts as an error; 20 errors within 60 seconds close the connection
  with `1011`.
- **Refusals are answered, not ignored.** When an agent receives a known command for a capability that is switched
  off on the device (or whose server module is off for its lab), it answers with `capability_denied`; a refused
  `execute` is also reported as a `result` with `exit_code` -5.
- **Senders send only documented fields.** A new field is added to the schema first; the test below fails on
  undocumented fields in the server's messages and in the examples.
- New messages follow the direction rule (`type` up, `action` down), and a new server message whose effect matters
  is either harmless for an agent that ignores it or sent only to agents that announced they handle it.

## Messages

### Agent → server

| `type` | Channel | Server reaction | Schema | Examples |
| --- | --- | --- | --- | --- |
| *(none)* / `heartbeat` | command | Recorded in batches; quarantine state reconciled | [heartbeat](agent-to-server/heartbeat.json) | [first](examples/agent-to-server/heartbeat.first.json), [minimal](examples/agent-to-server/heartbeat.minimal.json), [typed](examples/agent-to-server/heartbeat.typed.json), [linux](examples/agent-to-server/heartbeat.linux.json) |
| `result` | command | Task output stored; `result_ack` | [result](agent-to-server/result.json) | [completed](examples/agent-to-server/result.completed.json), [failed](examples/agent-to-server/result.failed.json), [denied](examples/agent-to-server/result.denied.json), [legacy](examples/agent-to-server/result.legacy.json), [winget](examples/agent-to-server/result.winget.json), [winget_missing](examples/agent-to-server/result.winget_missing.json), [power](examples/agent-to-server/result.power.json), [no_session](examples/agent-to-server/result.no_session.json), [user_message](examples/agent-to-server/result.user_message.json) |
| `capabilities` | command | Stored; a pending switch-off is re-sent | [capabilities](agent-to-server/capabilities.json) | [default](examples/agent-to-server/capabilities.default.json), [terminal_off](examples/agent-to-server/capabilities.terminal_off.json), [files](examples/agent-to-server/capabilities.files.json) |
| `capability_denied` | command | Audited, notified; task `Denied`, file transfer `rejected`, exam marked refused; Linux `not_supported` quarantine clears the pending lock | [capability_denied](agent-to-server/capability_denied.json) | [execute](examples/agent-to-server/capability_denied.execute.json), [vision](examples/agent-to-server/capability_denied.vision.json), [policy](examples/agent-to-server/capability_denied.policy.json), [files](examples/agent-to-server/capability_denied.files.json), [exam](examples/agent-to-server/capability_denied.exam.json), [not_supported](examples/agent-to-server/capability_denied.not_supported.json), [winget](examples/agent-to-server/capability_denied.winget.json), [power](examples/agent-to-server/capability_denied.power.json), [message](examples/agent-to-server/capability_denied.message.json) |
| `file_result` | command | Transfer row updated and audited | [file_result](agent-to-server/file_result.json) | [done](examples/agent-to-server/file_result.done.json), [rejected](examples/agent-to-server/file_result.rejected.json) |
| `update_result` | command | Audited, notified; `update_result_ack` | [update_result](agent-to-server/update_result.json) | [success](examples/agent-to-server/update_result.success.json), [rolled_back](examples/agent-to-server/update_result.rolled_back.json), [legacy](examples/agent-to-server/update_result.legacy.json) |
| `update_progress` | command | Latest stage kept for the pending update; `rejected` ends it | [update_progress](agent-to-server/update_progress.json) | [example](examples/agent-to-server/update_progress.json) |
| `exam_state` | command | Kept per exam and device; corrected with `exam_mode` when it differs; leaving a running exam is audited and notified | [exam_state](agent-to-server/exam_state.json) | [in exam](examples/agent-to-server/exam_state.json), [off](examples/agent-to-server/exam_state.off.json) |
| `bypass_secret_ack` | command | Bypass key marked delivered | [bypass_secret_ack](agent-to-server/bypass_secret_ack.json) | [example](examples/agent-to-server/bypass_secret_ack.json) |
| `thumbnail` | command, Vision | Preview to admin panels | [thumbnail](agent-to-server/thumbnail.json) | [example](examples/agent-to-server/thumbnail.json) |
| `vision_rejected` | command | Forwarded to panels with the connection's device ID | [vision_rejected](agent-to-server/vision_rejected.json) | [example](examples/agent-to-server/vision_rejected.json) |
| `stream_frame` | Vision | Forwarded to session holders | [stream_frame](agent-to-server/stream_frame.json) | [example](examples/agent-to-server/stream_frame.json) |
| `monitors` | Vision | Stored for the tunnel; forwarded to session holders | [monitors](agent-to-server/monitors.json) | [example](examples/agent-to-server/monitors.json) |
| `clipboard` | Vision | Audited without the text, then forwarded to the holder of the accepted session | [clipboard](agent-to-server/clipboard.json) | [example](examples/agent-to-server/clipboard.json) |

### Server → agent

| `action` | When | Agent reaction | Schema | Examples |
| --- | --- | --- | --- | --- |
| `server_info` | after registration, every connection | Learns protocol and features | [server_info](server-to-agent/server_info.json) | [current](examples/server-to-agent/server_info.json), [0.1.14–0.1.21](examples/server-to-agent/server_info.0_1_21.json) |
| `set_identity` | first message, without secret | Stores the new ID | [set_identity](server-to-agent/set_identity.json) | [example](examples/server-to-agent/set_identity.json) |
| `set_secret` | enrollment | Stores the device secret | [set_secret](server-to-agent/set_secret.json) | [example](examples/server-to-agent/set_secret.json) |
| `set_bypass_secret` | after `server_info`, until acknowledged | Stores the key; `bypass_secret_ack` | [set_bypass_secret](server-to-agent/set_bypass_secret.json) | [example](examples/server-to-agent/set_bypass_secret.json) |
| `get_hardware` | inventory missing | `POST /api/inventory/{hw_id}` | [get_hardware](server-to-agent/get_hardware.json) | [example](examples/server-to-agent/get_hardware.json) |
| `execute` | task queue | Runs it; `result` | [execute](server-to-agent/execute.json) | [panel](examples/server-to-agent/execute.json), [queue](examples/server-to-agent/execute.queue.json) |
| `winget_install` | task queue, WINGET step, agent announced `winget` | Installs the package with winget; `result` | [winget_install](server-to-agent/winget_install.json) | [latest](examples/server-to-agent/winget_install.json), [version](examples/server-to-agent/winget_install.version.json) |
| `power` | task queue, power action, agent announced `power` | Tray countdown with the note, `result` just before acting, then shuts down, restarts, signs out or locks | [power](server-to-agent/power.json) | [shutdown](examples/server-to-agent/power.json), [lock](examples/server-to-agent/power.lock.json) |
| `user_message` | task queue, Mesaj gönder, agent announced `message` | Shows the message in the tray; `result` when shown or acknowledged | [user_message](server-to-agent/user_message.json) | [example](examples/server-to-agent/user_message.json) |
| `cancel_task` | task cancelled | Stops the process | [cancel_task](server-to-agent/cancel_task.json) | [example](examples/server-to-agent/cancel_task.json) |
| `result_ack` | after a `result` | Drops the kept result | [result_ack](server-to-agent/result_ack.json) | [example](examples/server-to-agent/result_ack.json) |
| `update_result_ack` | after an `update_result` | Drops the kept update result | [update_result_ack](server-to-agent/update_result_ack.json) | [example](examples/server-to-agent/update_result_ack.json) |
| `update_agent` | agent update deployed; with `peers` after the lab's seed is ready (`peer_cache`) | Verifies, downloads (peers first, then the server), installs; later `update_result` | [update_agent](server-to-agent/update_agent.json) | [example](examples/server-to-agent/update_agent.json), [peers](examples/server-to-agent/update_agent.peers.json) |
| `set_capabilities` | capability switched off | Applies only `false`; `capabilities` | [set_capabilities](server-to-agent/set_capabilities.json) | [terminal_off](examples/server-to-agent/set_capabilities.terminal_off.json), [both_off](examples/server-to-agent/set_capabilities.both_off.json) |
| `lockdown` | quarantine on, or re-sent | Lock screen and isolation | [lockdown](server-to-agent/lockdown.json) | [example](examples/server-to-agent/lockdown.json) |
| `unlock` | quarantine off, or re-sent | Removes both | [unlock](server-to-agent/unlock.json) | [example](examples/server-to-agent/unlock.json) |
| `exam_mode` | exam started or ended, (re)connect, device moved, state differs | Isolation with the allow list, tray banner, program block, own end at `until`; `exam_state` | [exam_mode](server-to-agent/exam_mode.json) | [on](examples/server-to-agent/exam_mode.json), [off](examples/server-to-agent/exam_mode.off.json) |
| `start_vision_session` | remote-control session opened | Consent or countdown; Vision channel | [start_vision_session](server-to-agent/start_vision_session.json) | [consent](examples/server-to-agent/start_vision_session.consent.json), [mandatory](examples/server-to-agent/start_vision_session.mandatory.json) |
| `stop_stream` | stream stopped, Vision module off | Stops capture, closes Vision | [stop_stream](server-to-agent/stop_stream.json) | [example](examples/server-to-agent/stop_stream.json) |
| `wake_peer` | Wake-on-LAN | Sends a magic packet | [wake_peer](server-to-agent/wake_peer.json) | [example](examples/server-to-agent/wake_peer.json) |
| `scan_updates` | panel | Windows Update scan; `POST /api/patches/{hw_id}` | [scan_updates](server-to-agent/scan_updates.json) | [example](examples/server-to-agent/scan_updates.json) |
| `install_updates` | panel | Installs updates; `POST /api/patches/{hw_id}` | [install_updates](server-to-agent/install_updates.json) | [security](examples/server-to-agent/install_updates.security.json), [all](examples/server-to-agent/install_updates.all.json) |
| `remote_input` (`type`) | panel preview and remote control | Preview, frame rate, input | [remote_input](server-to-agent/remote_input.json) | [get_thumbnail](examples/server-to-agent/remote_input.get_thumbnail.json), [set_fps](examples/server-to-agent/remote_input.set_fps.json), [mouse_move](examples/server-to-agent/remote_input.mouse_move.json), [mouse_click](examples/server-to-agent/remote_input.mouse_click.json), [mouse_wheel](examples/server-to-agent/remote_input.mouse_wheel.json), [keyboard](examples/server-to-agent/remote_input.keyboard.json) |
| `file_push` | admin sent a file (feature `file_transfer`) | Downloads once with its secret, checks size and SHA-256, writes to the allowlisted folder; `file_result` | [file_push](server-to-agent/file_push.json) | [example](examples/server-to-agent/file_push.json) |
| `file_pull` | admin asked for a file (feature `file_transfer`) | Reads the path within its profile rules, uploads once; `file_result` | [file_pull](server-to-agent/file_pull.json) | [example](examples/server-to-agent/file_pull.json) |
| `select_monitor` | viewer picks a screen or needs a full frame (Vision channel) | Streams that screen, or all side by side, starting with a full frame | [select_monitor](server-to-agent/select_monitor.json) | [screen](examples/server-to-agent/select_monitor.json), [all](examples/server-to-agent/select_monitor.all.json) |
| `set_quality` | viewer changes quality, scale or frame rate (Vision channel) | New upper limits | [set_quality](server-to-agent/set_quality.json) | [example](examples/server-to-agent/set_quality.json) |
| `clipboard` | viewer sends text, accepted session only (Vision channel) | Sets the clipboard; refuses outside an accepted session | [clipboard](server-to-agent/clipboard.json) | [example](examples/server-to-agent/clipboard.json) |
| `start_stream` | never (deprecated) | Windows agent: starts a stream | [start_stream](server-to-agent/start_stream.json) | [example](examples/server-to-agent/start_stream.json) |

### Vision v2 binary frames

With `vision_binary` the agent sends screen frames as binary WebSocket messages on the Vision channel instead of
`stream_frame`. A message is an 18-byte big-endian header followed by the JPEG:

| Offset | Size | Field |
| --- | --- | --- |
| 0 | 1 | kind: `0x01` full frame, `0x02` region, `0x03` cursor position (no image) |
| 1 | 1 | monitor: the screen `index` from `monitors`, or `0xFF` for all screens side by side in one image |
| 2 | 4 | sequence number (u32, wraps) |
| 6 | 2 + 2 | x, y: the region's top left (`0, 0` for a full frame; the cursor position for `0x03`) |
| 10 | 2 + 2 | w, h: the region's size (the whole output for a full frame; `0, 0` for `0x03`) |
| 14 | 2 + 2 | width and height of the whole output |
| 18 | rest | JPEG (absent for `0x03`) |

Coordinates are always the output's real pixels. At a scale below 1 the JPEG is smaller than (w, h) and the viewer
draws it into that rectangle. The server drops, without an answer, a message that breaks these rules and counts it
in `/metrics` (`pops_events_total{event="vision_frames_malformed"}`, `vision_frames_oversize`):

- longer than 2 MB (2,097,152 bytes, header included) or shorter than 18 bytes;
- an unknown kind, a monitor byte from 16 to 254, or an output width or height of 0;
- a full frame that does not start at `0, 0` or does not cover the whole output; a region of size 0 or outside the
  output; a cursor with a size, outside the output or with a payload;
- an image that is not a JPEG (fewer than 4 bytes, or not starting with `FF D8`).

After a full frame of an output the agent may send regions of it; it sends a full frame first after the stream
starts, after `select_monitor`, after a scale change and after it dropped a frame. Valid frames go only to the panels
of the admins holding a session for the device, prefixed with the tunnel's device ID (the frame itself carries no
ID); see [`../vision.md`](../vision.md) for the panel side.

## Test vectors

`examples/agent-to-server/` and `examples/server-to-agent/` hold one message per file, named
`<type or action>[.<variant>].json`; the part before the first dot names the schema. `examples/unknown/` holds a
message of each direction that no schema describes, for the "unknown messages are ignored" rule.

The `update_agent` examples carry the test manifest `Agent/POps.Tests/TestData/manifest.json` and its signature,
made with a throwaway key whose raw public key is `9enOkSVQHBXaXoAurkfvUFBqSbfYbJQbMwL0zEIPTqQ=` (the same vector
as `ReleaseVerifierTests`). It is not the release key, so a real agent refuses this message; tests verify it with
the test key. The `peers` URLs in `update_agent.peers.json` end in the SHA-256 of that manifest's MSI. The device
secret, bypass key and IDs in the examples are made up.

`Backend/tests/test_protocol.py` (CI job "Backend") checks that

- every schema is a valid draft 2020-12 schema and names its message, every field has a description, and the
  tables above list every message;
- every example validates against its schema and contains only documented fields, and removing any required
  field makes it invalid;
- every `action` the server code sends and every `type` it handles has a schema, and every schema is sent or
  handled by the server (except deprecated `start_stream`);
- the messages the server builds (server_info, set_identity, set_secret, set_bypass_secret, get_hardware,
  execute, winget_install, power, user_message, cancel_task, result_ack, update_result_ack, update_agent,
  set_capabilities, lockdown, unlock, exam_mode, start_vision_session, stop_stream, wake_peer, scan_updates,
  install_updates, remote_input, file_push, file_pull, select_monitor, set_quality, clipboard) validate and contain
  only documented fields: the test runs the real endpoint and queue code with a fake database and fake sockets;
- the agent examples go through the real `/ws/agent` and `/ws/vision` handlers without an error and have the
  documented effect (stored result, acknowledgement, audit record, forwarded frame, monitor list or clipboard
  text), valid binary frames are forwarded and broken ones dropped, and the unknown message is ignored;
- the first message of the load simulator (`tools/agent_simulator.py`) validates.

Agent test projects use the same files: see [`AGENT_TESTS.md`](AGENT_TESTS.md).
