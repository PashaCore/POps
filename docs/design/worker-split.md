# Agent service: split Worker.cs, run the agent tests in parallel

Status: proposed, not built. The code changes start after #97, #99, #100 and #104 are on main (see
[Merge order](#merge-order)).

## Problem

- **One class does everything.** `Agent/POps.Agent/POps.Agent/Worker.cs` is 1,919 lines on main. The `Worker` class
  runs the connection loop, startup, identity, hardware inventory, policy polling, the tray pipe's message routing,
  Vision, quarantine and every server command. Commands are dispatched by one if/else chain in
  `HandleServerMessageAsync` (lines 1044–1218) with 19 actions plus `"type": "remote_input"`.
- **Every feature edits the same lines.** The four open agent PRs add about 270 lines and four actions to Worker.cs.
  Three of them insert into the same else-if chain, and #97 (with #99 on top of it) and #100 add lines at the same
  place in `TestEnvironment.EnsureIsolated`. They conflict with each other before they conflict with anything else.
- **The tests run one at a time.** `Agent/POps.Tests/TestEnvironment.cs` sets
  `[assembly: CollectionBehavior(DisableTestParallelization = true)]` because the agent keeps its paths, test
  seams and runtime state in static properties. 25 of the 49 agent test files assign one. A test sets it in its
  constructor and resets it in `Dispose`, and `EnsureIsolated` resets six of them before every test. A test that
  forgets to reset, or resets to the wrong value, changes the result of whatever runs next.
  Examples on main and in open PRs:
  - Worker's constructor assigns the static `DnsPolicyMonitor.Quarantine` and `DnsPolicyMonitor.ErrorReporter`.
    The last Worker constructed in the process gets every DNS auto-quarantine. **Fixed** in
    [#137 test(agent): per-Worker DNS callbacks, no real folders in tests (split prerequisite)](https://github.com/PashaCore/POps/pull/137).
  - `AgentHttp.Client` is swapped for a fake by `ModulesTests`, `ReporterFlowTests` and `AgentHttpFlowTests`, and by
    `FileTransferTests` in #99.
  - `TestIsolationTests.RealFoldersAreNeverUsed` points `AgentUpdate.DataDir` at the real `C:\POpsData` for a moment.
    **Fixed** in
    [#137 test(agent): per-Worker DNS callbacks, no real folders in tests (split prerequisite)](https://github.com/PashaCore/POps/pull/137).
  - In #99, `FileTransferTests.Dispose` sets `FileTransfer.ProfileInfo` to `null` instead of back to `ReadProfiles`.
    A later test that reaches `FileTransfer.PullAsync` in the same run would throw. Only the serial order hides it.

Baseline (developer PC, Release, net10.0): 841 tests, 18 s of test time. The slowest classes start real processes
or wait on purpose: `CommandRunnerTests` 3.4 s, `ModulesTests` 2.5 s, `CommandOutputChunkTests` 1.7 s,
`UpdateProgressTests` 1.6 s, `ServerTrustTests` 1.5 s and `WuaJobTests` 1.0 s. Running in parallel will save some
time, but that is the smaller gain. The main gain is that a test's result no longer depends on which test ran
before it.

## Goals and non-goals

Goals:
- Worker.cs keeps only the hosted service: startup, background loops and the composition of the parts below.
- One handler class per server action, or per group of actions that share state, behind a dispatch table.
- The static state that tests change becomes per-instance objects passed in through constructors. Two Workers in
  one process share nothing they can change.
- xUnit parallelization is on for the net10.0 tests. Only a small, named collection still runs serially.

Non-goals (every step must keep these):
- **Behaviour unchanged.** The same replies are sent in the same order, the checks run in the same order, refusals
  stay the same (`result` with `exit_code` -5 before `capability_denied`), and the one-minute `capability_denied`
  throttle stays. An unknown action is still ignored, and invalid JSON still reaches the receive loop, which logs it.
- **Wire protocol unchanged.** Messages, fields, headers and close-code handling stay as they are, and nothing in
  `docs/protocol` changes. The protocol vector tests from #104 pass without edits at every step.
- **Same logs and Event Log IDs.** The text, tag and error flag of every `POpsHelpers.Log` call and every
  `LocalAudit` event ID (1000–1100) stay the same. Schools troubleshoot from these lines, so this series does not
  reword or translate them.
- No new features, no new agent dependencies, no DI container for handlers, and no switch from `POpsHelpers.Log` to
  `ILogger`.
- The tray, updater and watchdog are out of scope, and so is the Linux agent (#108, Python). `POps.Shared` changes
  only where one of its statics is a test seam (`ConfigPaths`, `ServerTrust`).

## What Worker.cs does today

| Responsibility | Lines on main | Moves to |
| --- | --- | --- |
| `AgentPolicy` (policy JSON model) | 32–41 | `AgentPolicy.cs` |
| Fields, constructors, test seams | 44–157, 1312–1319 | Worker (composition), `AgentHarness` in tests |
| Startup: identity, credentials, hardware binding, capabilities, kiosk sync, server CA, slow WMI | 159–243 | Worker startup, `DeviceIdentity` |
| Log retention loop, config problem | 245–278 | Worker (background loop), `AgentConfig` |
| Connection loop, backoff, auth headers, connection lost, 4409, plain-HTTP mode | 280–500 | `CommandConnection` |
| `set_secret` | 502–524 | `SecretsHandler` |
| Isolation refresh loop, policy polling, `ApplyPolicy`, module changes, helpdesk menu | 526–613 | `PolicySync` |
| Tray pipe message routing (`EnsureTrayPipeServer`) | 615–767 | `TrayMessageRouter` |
| `set_bypass_secret`, offline bypass attempt, DNS auto-quarantine | 769–813 | `SecretsHandler`, `QuarantineService` |
| Vision tunnel, Vision v2 relay, viewer controls, close codes | 815–1013 | `VisionSession` |
| Receive loop and `HandleServerMessageAsync` | 1015–1218 | `CommandConnection`, `CommandDispatcher` + handlers |
| Snapshot, heartbeat | 1220–1273 | `RemoteInputHandler`, `CommandConnection` |
| `set_capabilities`, `DenyCapabilityAsync` | 1275–1310 | `CapabilitiesHandler`, `CapabilityGate` |
| Sender, result spool, update result and progress | 1321–1435 | `CommandChannel`, `ResultOutbox`, `UpdateReporter` |
| Identity file, ACLs, WMI hardware and inventory, `get_hardware` upload | 1437–1703 | `DeviceIdentity`, `HardwareInfo`, `InventoryHandler` |
| `CommandExecutionResult`, `AgentStartupHealth`, `TrayPipeServer` | 1711–1918 | their own files, unchanged |

### Server → agent, command channel

| `action` | Today | Checked before acting | Target handler |
| --- | --- | --- | --- |
| `execute` | inline, 1085–1131 | terminal capability, terminal module (`result` -5, then `capability_denied`); a running duplicate `task_id` is ignored | `ExecuteHandler` |
| `cancel_task` | inline, 1176–1182 | integer `task_id` | `ExecuteHandler` |
| `get_hardware` | `SendHardwareInfoAsync` | cached inventory exists | `InventoryHandler` |
| `set_capabilities` | `HandleSetCapabilitiesAsync` | only `false` applies | `CapabilitiesHandler` |
| `start_vision_session` | inline, 1208–1216 (raw JSON to the tray) | vision capability, then vision module | `VisionHandler` |
| `start_stream` (deprecated) | inline, 1142–1161 | the same | `VisionHandler` |
| `stop_stream` | inline, 1162–1165 | none | `VisionHandler` |
| `update_agent` | inline, background task to `AgentUpdate.HandleUpdateCommandAsync` (element cloned) | in `AgentUpdate` | `UpdateHandler` |
| `update_result_ack` | `HandleUpdateResultAck` (static) | `result_id` matches | `UpdateHandler` |
| `wake_peer` | inline, 1171–1172 | wol module | `WakeOnLanHandler` |
| `set_identity` | `UpdateIdentityFile` | non-empty | `IdentityHandler` |
| `set_secret` | `HandleSetSecret` | well-formed secret | `SecretsHandler` |
| `set_bypass_secret` | `HandleSetBypassSecretAsync` | connection used the device secret | `SecretsHandler` |
| `lockdown`, `unlock` | inline, 1183–1193 (`/api/logs` when unlock fails) | none | `QuarantineHandler` |
| `server_info` | `Handshake.OnServerInfo` | none | `HandshakeHandler` |
| `result_ack` | `HandleResultAck` | integer `task_id` | `ResultAckHandler` |
| `scan_updates`, `install_updates` | inline, 1200–1207 to `PatchManager` | patches module | `PatchesHandler` |
| `"type": "remote_input"` | inline, 1049–1081: `get_thumbnail` is answered on the socket it came on, everything else goes to the tray | `device` is this PC; vision capability, module, session approval for input | `RemoteInputHandler` |
| `exam_mode` (#97) | `HandleExamModeAsync` | exam capability | `ExamHandler` |
| `file_push`, `file_pull` (#99) | `HandleFileTransferAsync` | files capability | `FileTransferHandler` |
| `winget_install` (#100) | `HandleWingetInstallAsync` | `task_id`, terminal capability, deploy module, winget found | `WingetInstallHandler` |
| anything else | ignored | | no handler |

The Vision channel (`/ws/vision`) carries `remote_input` and the viewer's `select_monitor`, `set_quality` and
`clipboard`, all handled by `ReceiveVisionInputsAsync` and `HandleVisionControlAsync`. They stay out of the command
dispatcher: that socket has its own 1 MiB limit, and an oversized message closes the tunnel. They move with
`VisionSession` and reuse the vision checks of `RemoteInputHandler`.

### Agent → server

| `type` | Sent from today | Send path | Target |
| --- | --- | --- | --- |
| heartbeat (no `type`) | `SendHeartbeatAsync`, every 5 s | own path: lock with the service token, only when open, marks "heartbeat sent" | `CommandConnection` |
| `capabilities` | loop, after the first heartbeat; `set_capabilities` | `TrySendCommandMessageAsync` (honours `SendOverride`) | `CommandConnection`, `CapabilitiesHandler` |
| `capability_denied` | `DenyCapabilityAsync` (once a minute per capability/action/reason unless `task_id`) | the same | `CapabilityGate` |
| `result` | `SendResultAsync` (execute, refusals; winget in #100) | result spool on `result_ack` servers, else an in-memory queue of 20 | `ResultOutbox` |
| `update_result`, `update_progress` | heartbeat tick; `AgentUpdate` callback | the same, `update_progress` only after the heartbeat and with the feature | `UpdateReporter` |
| `bypass_secret_ack` | `HandleSetBypassSecretAsync` | the same | `SecretsHandler` |
| `thumbnail` | `get_thumbnail` | directly on the receiving socket, lock without timeout, not `SendOverride` | `RemoteInputHandler` |
| `vision_rejected` | tray `REJECT_VISION_TUNNEL` | directly on the command socket, 2 s lock timeout, fire and forget | `VisionSession` |
| `stream_frame`, Vision v2 frames, `monitors`, `clipboard` | tray frames, `VisionRelay` | Vision socket, 1.5 s lock | `VisionSession` (`VisionRelay` unchanged) |
| `exam_state` (#97), `file_result` (#99) | the new handlers | `TrySendCommandMessageAsync` | `ExamHandler`, `FileTransferHandler` |

HTTP calls made by Worker itself: `GET /api/agent_policies` (every minute), `POST /api/inventory/{hw_id}`
(`get_hardware`) and `POST /api/logs/{hw_id}` (bypass used, auto-quarantine, unlock failed). The other reports
already live in their own classes (`SoftwareReporter`, `PatchManager`, `SessionReporter`, `Helpdesk`,
`ActivityHistory`, `DnsPolicyMonitor`).

From the tray the service receives 13 text messages (`USER_COMMAND:`, `FAIR_USE_ACK`, `ACTIVE_APP:`,
`ACTIVE_WINDOW:`, `START_VISION_TUNNEL`, `REJECT_VISION_TUNNEL:`, `STOP_VISION_TUNNEL`, `UNLOCK_BYPASS:`,
`VISION_MONITORS:`, `CLIPBOARD:`, `TICKET_CREATE:`, `TICKET_LIST`, `ACTIVITY_LIST`), JPEG frames and Vision v2
frames.

### Open PRs that add to Worker.cs

| PR | Branch | Adds to Worker.cs | New statics |
| --- | --- | --- | --- |
| #97 | `agent-exam-mode` | about 145 lines: `exam_mode`, exam loop (2 s tick, app block, allow-list refresh), `exam_state` after `capabilities`, banner on tray connect, capability audit name | `ExamMode.Resolver`; `NetworkIsolation.ScriptRunner` default in `EnsureIsolated` |
| #99 | `agent-file-transfer` (on top of #97) | about 55 lines: `file_push`, `file_pull`, `FileTransferTask` test seam | `FileTransfer.PublicDesktopOverride`, `FileTransfer.ProfileInfo` |
| #100 | `agent-winget` | about 70 lines: `winget_install`, `X-Agent-Features` header | `WingetInstall.Locator` |
| #104 | `agent-protocol-vectors` | 3 lines (`dna_payload` never null); `ProtocolVectorTests` builds a Worker like `WorkerCommandTests` | none |

## Target structure

### Dispatcher

```csharp
// One command from the server. Root lives only until HandleAsync returns; a handler that keeps it for background
// work clones it (update_agent does this today).
internal sealed class ServerCommand
{
    public string Action { get; init; }              // "remote_input" for the typed message
    public JsonElement Root { get; init; }
    public string Raw { get; init; }                 // forwarded verbatim to the tray (start_vision_session, remote_input)
    public ClientWebSocket Connection { get; init; } // the socket it came on (thumbnail reply); null in tests
    public CancellationToken Stopping { get; init; }
}

internal interface ICommandHandler
{
    IReadOnlyList<string> Actions { get; }
    Task HandleAsync(ServerCommand command);
}

internal sealed class CommandDispatcher
{
    private readonly Dictionary<string, ICommandHandler> _byAction = new(StringComparer.Ordinal);
    private readonly ICommandHandler _remoteInput;

    public CommandDispatcher(IEnumerable<ICommandHandler> handlers, ICommandHandler remoteInput)
    {
        _remoteInput = remoteInput;
        foreach (ICommandHandler handler in handlers)
            foreach (string action in handler.Actions)
                _byAction.Add(action, handler);   // two handlers for one action fail at startup and in a test
    }

    // Same contract as HandleServerMessageAsync today: the type/action reads are the same expressions (a JSON
    // array or a non-string action still throws to the receive loop, which logs it); unknown actions are ignored.
    public async Task DispatchAsync(string message, ClientWebSocket connection, CancellationToken stopping)
    {
        using JsonDocument doc = JsonDocument.Parse(message);
        JsonElement root = doc.RootElement;
        bool remoteInput = root.TryGetProperty("type", out JsonElement type) && type.GetString() == "remote_input";
        string action = remoteInput ? "remote_input" : root.TryGetProperty("action", out JsonElement a) ? a.GetString() : "";
        ICommandHandler handler = remoteInput ? _remoteInput
            : action != null && _byAction.TryGetValue(action, out ICommandHandler h) ? h : null;
        if (handler == null) return;
        await handler.HandleAsync(new ServerCommand
            { Action = action, Root = root, Raw = message, Connection = connection, Stopping = stopping });
    }
}
```

Handlers are awaited one at a time on the receive loop, as the else-if branches are today. Work that runs longer
than the dispatch (`execute`, `update_agent`, file transfers, `get_thumbnail`) keeps starting its own task, as it does
today. The dispatcher catches nothing, so exceptions still reach `ReceiveCommandsAsync` and its log lines.

A short handler, for scale:

```csharp
internal sealed class PatchesHandler : ICommandHandler
{
    private readonly PatchManager _patches;
    private readonly CapabilityGate _gate;

    public PatchesHandler(PatchManager patches, CapabilityGate gate) { _patches = patches; _gate = gate; }

    public IReadOnlyList<string> Actions { get; } = new[] { "scan_updates", "install_updates" };

    public async Task HandleAsync(ServerCommand command)
    {
        if (!_gate.ModuleEnabled(AgentModules.Patches))
        {
            await _gate.DenyAsync("patches", command.Action, reason: AgentModules.DisabledReason);
            return;
        }
        if (command.Action == "scan_updates") _patches.RequestScan();
        else _patches.RequestInstall(command.Root.TryGetProperty("scope", out JsonElement s)
            && s.ValueKind == JsonValueKind.String ? s.GetString() : null);
    }
}
```

### Handlers

| Handler | Actions | Uses |
| --- | --- | --- |
| `HandshakeHandler` | `server_info` | `ServerHandshake` |
| `ExecuteHandler` | `execute`, `cancel_task` | `CommandRunner`, `CapabilityGate`, `ResultOutbox`, audit |
| `WingetInstallHandler` | `winget_install` | the same, plus the winget locator |
| `ResultAckHandler` | `result_ack` | `ResultOutbox` |
| `UpdateHandler` | `update_agent`, `update_result_ack` | `AgentUpdate`, `UpdateReporter` |
| `CapabilitiesHandler` | `set_capabilities` | `CapabilityGate`, `VisionSession` (stops a stream), sender |
| `VisionHandler` | `start_vision_session`, `start_stream`, `stop_stream` | `VisionSession`, `CapabilityGate`, tray |
| `RemoteInputHandler` | `"type": "remote_input"` | `VisionSession`, `CapabilityGate`, tray, sender |
| `QuarantineHandler` | `lockdown`, `unlock` | `QuarantineService` |
| `IdentityHandler` | `set_identity` | `DeviceIdentity` |
| `SecretsHandler` | `set_secret`, `set_bypass_secret` | credentials, `HardwareBinding`, `SoftwareReporter`, sender, audit |
| `InventoryHandler` | `get_hardware` | `HardwareInfo`, `AgentHttp`, health telemetry |
| `WakeOnLanHandler` | `wake_peer` | `CapabilityGate` |
| `PatchesHandler` | `scan_updates`, `install_updates` | `PatchManager`, `CapabilityGate` |
| `ExamHandler` | `exam_mode` | `ExamMode`, `CapabilityGate`, tray, sender, audit; the exam loop stays a background loop |
| `FileTransferHandler` | `file_push`, `file_pull` | `FileTransfer`, `CapabilityGate`, tray, sender, audit |

### Shared services

| Service | Replaces | Notes |
| --- | --- | --- |
| `CommandChannel` | `_commandWs`, `_wsCommandLock`, `TrySendCommandMessageAsync`, `SendOverride` | `TrySendAsync(payload)` → sent or not; `SendOnAsync(socket, payload, lockTimeout)` for the thumbnail and `vision_rejected` paths, which bypass `SendOverride` today and must keep their lock behaviour |
| `ResultOutbox` | `SendResultAsync`, `FlushPendingResultsAsync`, `HandleResultAck`, `_pendingResults`, `Results` | durable on `result_ack` servers, otherwise the queue of 20, as today |
| `CapabilityGate` | `DenyCapabilityAsync`, `_lastDenialNotice`, the capability and module reads | one instance, so the one-minute throttle is shared by all actions as today |
| `TrayChannel` | `ToTray`, `TrayOverride`, `_trayPipe?.SendCommandToDesktop` | step (a) keeps both paths (see [Test seams](#test-seams-during-the-move)) |
| `VisionSession` | the ten Vision fields, tunnel connect/close, receive loop, relay, snapshot, `StartCapture`, close codes | read by the heartbeat, `set_capabilities`, module changes, connection loss and the tray router |
| `QuarantineService` | `_quarantine` plus `HandleBypassAttemptAsync`, `AutoQuarantineAsync` | `QuarantineControl` itself is unchanged |
| `UpdateReporter` | `ReportUpdateResultAsync`, `ForwardUpdateProgressAsync`, `ReportUpdateProgressAsync`, `_heartbeatSent` | |
| `AgentIdentity` | `_hwId`, `_pcName`; `AgentConfig`: `_serverUrl`, `ConfigProblem` | handlers read `HwId` when they use it and never cache it (`set_identity` changes it at run time) |
| `IAuditSink` | `LocalAudit.Write` | production writes to the Event Log exactly as today; tests record event IDs |
| `TimeProvider` | inline `DateTime.UtcNow` in the gate and the exam expiry | .NET's own `System.TimeProvider` |
| `ServerHandshake` | (exists) | |

`POpsHelpers.Log` stays static: it writes one log file per process.

### Connection loop and tray

- **`CommandConnection`** owns the socket, auth headers, connect, receive loop, the 4401/4409 backoff and the
  heartbeat tick. The tick stays literal code in one method, in today's order: heartbeat, `capabilities` once (and
  `exam_state` once with #97), update progress, update result, pending results, wait 5 s, reset the backoff.
- **`TrayMessageRouter`** replaces the lambda in `EnsureTrayPipeServer` with an ordered list of (match, handler).
  The order is today's, because `START_VISION_TUNNEL` is a prefix match. Frame events go to `VisionSession`, and
  the connect/disconnect events stay together in one place.
- **`PolicySync`** holds the policy poll, `ApplyPolicy` and the module-change reaction. The isolation refresh loop
  and log retention stay as Worker's background loops.

### Files and size budget

New files go next to today's: `Agent/POps.Agent/POps.Agent/Commands/` for the dispatcher and handlers, and the
services beside the classes they wrap. Budgets:
- `Worker.cs`: 300 lines at most (composition, startup, background loops).
- Each handler: 200 lines at most, most well under 100. A handler that grows past that is holding logic that belongs
  in its domain class (`ExamMode`, `FileTransfer`, `AgentUpdate`).
- `CommandConnection`, `VisionSession`: 350 lines at most.
- No new file over 400 lines.
- `TrayPipeServer.cs` and `HardwareInfo.cs` (about 180 lines each) are moved unchanged.

The parts are wired by hand in one place (Worker's constructor, or an `AgentComposition` it calls), about 40 lines.
Tests build the same graph with fakes. The host's DI container keeps registering only Worker.

### Test seams during the move

In step (a) the test files that construct a Worker compile and pass unchanged: 11 on main, plus
`ProtocolVectorTests` (#104), `ExamModeTests` (#97), `FileTransferTests` (#99) and `WingetInstallTests` (#100).
To make that possible, `Worker.HandleServerMessageAsync(string, ClientWebSocket, CancellationToken)` stays as a
one-line call into the dispatcher. The internal properties `SendOverride`, `TrayOverride`, `Quarantine`,
`CommandRunner`, `HwId`, `Binding`, `Software`, `Results`, `UpdateResults`, `Handshake`, `TrayPipe` and
`ConfigProblem` stay too, forwarding to the services. The tray also keeps its two paths in step (a):
`QuarantineControl` and some Vision messages write to the pipe directly, so they never reach `TrayOverride`. Merging
the two would change what those tests record, but not what the tray receives.

In step (b) the tests move class by class to an `AgentHarness` helper, and the forwarding properties go:

```csharp
using var agent = AgentHarness.Create();     // own temp folders, fake machine, recording sender, tray and audit
await agent.HandleAsync("{\"action\":\"lockdown\"}");
Assert.Contains(1020, agent.AuditIds);       // quarantine start, not checkable today
```

## Static state

"Test sets it" names the main writers; files are counted on main.

| Static | Read by | Test sets it | Replacement | Step |
| --- | --- | --- | --- | --- |
| `SecureStore.Dir` | `SecureStore.PathOf` callers: capabilities, credentials, isolation, quarantine, bypass, result spool, binding, kiosk, generalizer; exam and files in the PRs | 17 test files and `TestEnvironment` | `AgentPaths.SecureDir`; `SecureStore` becomes an instance over it | b1 |
| `AgentUpdate.DataDir` and the paths derived from it (identity, health, lock, result, progress, inbox) | `AgentUpdate`, Worker, `Helpdesk`, `PatchManager`, `SessionReporter`, `SoftwareReporter`, `Generalizer`, `FileTransfer` | 14 test files and `TestEnvironment` | `AgentPaths.DataDir` | b1 |
| `POpsHelpers.ConfigPaths` | `ResolveServerUrl`, `ReadConfigValue`, Worker's ACL fix, credential migration | `ConfigProblemTests`, `POpsHelpersTests`, `AgentCredentialsTests`, `ReviewFourTests` | `AgentPaths.ConfigPaths`, passed to `ResolveServerUrl` and `ReadConfigValue` overloads | b1 |
| `ServerTrust.CaPath` and its cache (`_ca`, `_loadedPath`, `_broken`, last-reject time) | Worker (reload, sockets), `AgentHttp.Handler`, `capabilities` (`server_ca`) | `ServerTrustTests` | `ServerTrust` instance in the context (only the agent uses it) | b1 |
| `AgentCapabilities` state | Worker, the gate, the heartbeat, `capabilities` | `Load()` after writing `capabilities.json` | `CapabilityState` instance | b2 |
| `AgentModules` (`_closed`, `_known`) | Worker, `Helpdesk`, `PatchManager`, `SoftwareReporter` | `Apply`, `Reset` before every test | `ModuleState` instance | b2 |
| `AgentCredentials` (`_currentSecret`, `_badTokenLogged`) | auth headers, Vision auth, `AgentHttp.CanReport` | `SaveSecret` | `CredentialStore` instance | b2 |
| `AgentHttp.Client`, `Handler`, `MaxResponseBytes`, `_noSecretLogged` | every HTTP report | `ModulesTests`, `OperationalFlowTests`, `FileTransferTests` (#99) | `AgentHttp` instance built from an `HttpMessageHandler` | b2 |
| `AgentUpdate` `_busy`, `_preparingVersion`, `_auditedResult`, `TrustedKeyOverride`, `LaunchOverride`, `InstalledVersionOverride` | update command and reporting | `UpdateProgressTests`, `RollbackDrillTests`, `Pr24ReviewTests`, `AgentUpdateTests` | `AgentUpdater` instance with key, launcher and version as constructor arguments | b2 |
| `NetworkIsolation.ScriptRunner` | isolation, exam (#97) | `NetworkIsolationFlowTests`; `ExamModeTests` and a default in `EnsureIsolated` (#97) | constructor argument | b3 |
| `KioskMode.Registry` | quarantine, kiosk sync | `TestEnvironment`, `KioskPoliciesTests` | constructor argument (`IKioskRegistry` exists) | b3 |
| `BitsDownload.Enabled`, `Runner` | update download | `TestEnvironment`, `BitsDownloadTests` | `AgentUpdater` options | b3 |
| `TrayPipeServer.PipeName`, `ClientCheckOverride` | the pipe | `TrayPipeLifetimeTests` | constructor options | b3 |
| `CommandRunner.StreamDrainTimeout`; task files in the real `%TEMP%` | `CommandRunner` | none | constructor arguments; task folder from `AgentPaths` | b3 |
| `WindowsUpdateAgent.AbortGrace`, `_browseOnlyUnreadableLogged` | WUA jobs | `WuaJobTests` | argument / instance | b3 |
| `Generalizer.IsAdministrator`, `StopAgent` | `--generalize` (no Worker) | `GeneralizeTests` | arguments to `Generalizer.Run` | b3 |
| `ExamMode.Resolver` (#97), `FileTransfer.PublicDesktopOverride`, `ProfileInfo` (#99), `WingetInstall.Locator` (#100) | the new handlers | their test classes | handler constructor arguments | b3 |
| `DnsPolicyMonitor`: timer, policy, index, reported and recent lists, baseline, flags, `UtcNow`, `CacheReader`, `Reporter`, `Quarantine`, `ErrorReporter`, `IndexBuilds` | Worker (configure, start, user change) | `DnsPolicyMonitorTests`, `QuarantineControlTests`, `ReReviewTests` | instance owned by Worker; callbacks and clock as constructor arguments | b4 |

These can stay static, because they are immutable or describe the whole process or machine:
- constants, compiled regexes, `JsonSerializerOptions`, `AppVersion`, `TrayExePath`, `LocalAudit.Source`, and pure
  functions (`LocalAudit` event builders, `VisionRelay.TrayMessageFor`, `CommandExecutionPolicy`, `ReconnectBackoff`);
- `POpsHelpers.Log` with its lock and `LogDirectoryOverride` (set once per test process) and `Component` (set once
  per program). `POpsHelpersTests.LogFile_PerComponent` changes `Component`; it moves to the serial collection or
  calls a pure `LogFilePath(component, day)` overload;
- `SecureStore.SystemSid` (the service account, set once per test process). `SecureStoreTests` changes it and goes
  serial, or `ProtectedFileSecurity` takes the SID as an argument;
- `NetworkIsolation.Gate`, `KioskMode.Gate` and the exam mode gate. They serialize changes to the machine's firewall
  and registry, of which there is one per machine. With fakes they only serialize, which is harmless.

The replacements meet in one object per Worker:

```csharp
// Built once by Program (production paths, the real machine) and once per test (temp folders, fakes). Services and
// handlers get the parts they use through their constructors, never the whole context.
internal sealed class AgentContext
{
    public AgentPaths Paths { get; }              // data, secure, config files, server CA, task files, pipe name
    public AgentMachine Machine { get; }          // PowerShell runner, kiosk registry, DNS resolver, updater launcher,
                                                  // pipe client check, winget locator
    public CapabilityState Capabilities { get; }
    public ModuleState Modules { get; }
    public CredentialStore Credentials { get; }
    public ServerTrust Trust { get; }
    public AgentHttp Http { get; }
    public IAuditSink Audit { get; }
    public TimeProvider Clock { get; }
}
```

During step (b), each converted static keeps a facade that forwards to a process-default instance, so unconverted
code keeps working. The facade is deleted in the PR that converts its last caller, and at the end of (b) none are
left. The order follows how many test files each static blocks: paths first, then runtime state, machine seams,
and `DnsPolicyMonitor` last (the largest single change).

## Migration plan

Each PR is behaviour-preserving and small enough to review. The proof is the same at every step: the full agent
suite (net10.0 and net472) and `ProtocolVectorTests` from #104 pass without edits, the warnings gate builds with zero
warnings, and a log-line diff matches. For the diff, the string arguments of every `POpsHelpers.Log` call and the
`LocalAudit` event builders called in the removed code are listed before and after, and the two lists must be equal.
Steps marked "real PC" also get the manual check on a test machine: enrol, `execute`, Vision session with consent,
`lockdown`, unlock by bypass code, agent update.

### Merge order

1. **#104 first.** It is the safety net every later step leans on.
2. **#97, then #99** (stacked on #97), and **#100** in either order. Each adds a branch to the else-if chain. #97
   and #100 both add lines at the same place in `EnsureIsolated`, so whichever merges second resolves a small
   conflict there. Once #104 is on main, the PR that adds `exam_enabled` and `files_enabled` also keeps
   `capabilities.default.json` in step with what the agent sends (see the note in #104).
3. **The split starts after these four are on main.** If it started earlier, each of them would have to be rewritten
   by hand as a handler: Git cannot carry a branch of the if/else chain into a new file. While the split is in
   progress, new agent features wait or are written as handlers once (a1) is merged.

### (a) Dispatcher and handlers, tests unchanged

- **a0. Move types out of Worker.cs.** `TrayPipeServer`, `AgentStartupHealth`, `CommandExecutionResult` and
  `AgentPolicy` get their own files, and the static WMI/ACL helpers move to `HardwareInfo`, verbatim (about 400
  lines). No open PR touches these lines, so this one may go before the merge order above. Review with
  `git diff --color-moved`.
- **a1. Dispatcher.** `ServerCommand`, `ICommandHandler`, `CommandDispatcher`; `HandleServerMessageAsync` calls the
  dispatcher. This PR moves the small handlers: handshake, result ack, update, Wake-on-LAN, patches, identity,
  secrets, inventory, quarantine. Groups that are still inside Worker register as a `DelegateHandler` around the
  existing method, so the if/else chain disappears in this PR. New tests: every message in
  `docs/protocol/server-to-agent/` maps to exactly one handler, a duplicate fails, and an unknown action sends
  nothing.
- **a2. Execute.** `ExecuteHandler`, `WingetInstallHandler`, `ResultOutbox`, `CapabilityGate`. Covered by
  `WorkerCommandTests`, `WorkerResultAckTests`, `CommandResultTests`, `WingetInstallTests` and the protocol vectors.
- **a3. Vision.** `VisionSession`, `VisionHandler`, `RemoteInputHandler`, `CapabilitiesHandler`. This is the riskiest
  step, so it is its own PR, checked on a real PC. Covered by `VisionRelayTests`, the vision cases of `ModulesTests`
  and `WorkerCommandTests`, `TrayPipeLifetimeTests` (connection loss stops capture) and the protocol vectors.
- **a4. Exam and files.** `ExamHandler` with the exam loop, and `FileTransferHandler`. Covered by `ExamModeTests` and
  `FileTransferTests`. Checked on a real PC (firewall group, banner).
- **a5. Connection and tray.** `CommandConnection`, `TrayMessageRouter`, `PolicySync`. Worker.cs is now within its
  budget. Covered by `TrayPipeLifetimeTests`, `UpdateProgressTests`, `ReviewFourTests` (4409) and `ModulesTests`.
  Checked on a real PC (reconnect, 4401 backoff).

Conflicts: each (a) step conflicts only with agent PRs opened after it starts. Keeping the steps to a few days each
keeps that window short.

### (b) Context object, statics removed

- **b1. Paths:** `AgentPaths`, `AgentContext`, `AgentHarness`. Tests that only needed their own folders move to the
  harness.
- **b2. Runtime state:** capabilities, modules, credentials, `AgentHttp`, `ServerTrust`, update state.
- **b3. Machine seams:** isolation runner, kiosk registry, BITS, updater launcher and key, pipe options, command
  runner timeouts and task folder, WUA grace, generalizer, and the three seams from #97/#99/#100.
- **b4. `DnsPolicyMonitor`** becomes an instance.

Each PR deletes the matching lines in `TestEnvironment.EnsureIsolated`. After b4 `EnsureIsolated` is gone, and
`TestEnvironment` only creates the per-process root and log folder. Test edits in (b) are limited to building the
object under test with the harness. The assertions do not change. `TestIsolationTests.RealFoldersAreNeverUsed` is
rewritten: instead of setting the real path for a moment, it checks that `AgentHarness` paths never point at
`C:\POps*`.

### (c) Parallel tests, per collection

This step can land as soon as (a) is done; it does not wait for (b).

- `[assembly: CollectionBehavior(DisableTestParallelization = true)]` stays only for net472 (the MSI custom action
  tests, under `#if NETFRAMEWORK`).
- Two collections are defined with `[CollectionDefinition(..., DisableParallelization = true)]`. xUnit 2.9 runs
  these one at a time, after all parallel collections have finished:
  - **`SharedState`:** every class that still touches a static. Its base class is the only one that calls
    `EnsureIsolated`; the parallel classes use a `TestBase` that changes nothing.
  - **`Machine`:** classes that are serial because of the machine, not the code: real processes and timing
    (`CommandRunnerTests`, `CommandOutputChunkTests`, `WuaJobTests` with its 300 ms to 5 s window, the
    `DnsDomainIndexTests` "20 rounds under 5 s" check) and the real named pipe until its name is an instance option.
- Everything else runs in parallel, one collection per class (xUnit's default).
- Each (b) PR moves the classes it frees from `SharedState` to the default.
- Proposed: for the first two weeks CI runs the net10.0 tests twice. Parallel scheduling differs between runs, so a
  test that depends on timing or on another test gets two chances to show it.

### (d) Parallel by default

- `SharedState` is empty and is deleted. `Machine` stays, and every class in it carries a comment saying why.
- A guard test fails when a new static mutable field or settable static property appears in `POpsAgent` or
  `POps.Shared`: it reflects over the assemblies, skips compiler-generated types and checks an allow list with a
  reason per entry. A new static then has to be argued for in review instead of slipping in as a test seam.
- `MaxParallelThreads` stays at xUnit's default (the number of cores) unless the Windows runner shows contention.

## Risks and how to check them

- **Order of checks changes a reply.** Example: vision capability is checked before the vision module, and `result`
  -5 goes before `capability_denied`. Handlers keep the code blocks verbatim. The protocol vectors cover the
  refusals, and a1–a4 add a test wherever an order is not yet covered (both vision switches off, for example).
- **`JsonElement` outlives its document.** The root is disposed when dispatch returns. Background work must clone
  it or read its values first (`update_agent` clones, `execute` and the file transfers read first). Review checks
  every `Task.Run` in a handler.
- **State shared between groups.** The Vision flags are read by the heartbeat, `set_capabilities`, module changes,
  connection loss and the tray router. `VisionSession` owns them and keeps them `volatile` where they are today.
  `hw_id` is read at use, never cached.
- **Exceptions.** The dispatcher and handlers keep today's try/catch blocks and add none, so the receive loop logs
  the same errors.
- **Timing-sensitive tests under load.** `WaitForResult` polls for 30 s, `CommandRunnerTests` assert durations and
  kill timeouts, and the WUA and DNS index tests measure time. They go to `Machine`. Parallel runs are repeated
  locally (20 runs) before (c) and (d) merge, and each flaky test is fixed or moved to `Machine`, not retried.
- **Named pipes.** A unique name per test becomes an instance option in b3. The pipe ACL uses the process-wide
  `SystemSid`, which is set once.
- **Temp folders.** `TestEnvironment.Root` is per process and `NewDir` per test, so they are already safe. The
  exception is `CommandRunner`, which writes its `pops_task_*.bat` files to the real `%TEMP%`, while
  `CleanupStaleTaskFiles()` defaults to `%TEMP%` too. A cleanup in one test could delete another test's running task
  file. b3 moves the task folder into `AgentPaths` before those classes leave `Machine`. **Fixed** in
  [#137 test(agent): per-Worker DNS callbacks, no real folders in tests (split prerequisite)](https://github.com/PashaCore/POps/pull/137).
- **Ports.** `AgentHttpTests` and `WebSocketMessagesTests` listen on port 0 (an ephemeral port), and no agent test
  uses a fixed port.
- **Machine state reached from tests.** `LocalAudit.Write` goes to the real Event Log today; without the source
  registered it fails and logs. With `IAuditSink` tests stop touching it. #97 subscribes to the process-wide
  `NetworkChange.NetworkAddressChanged` in the exam loop and never unsubscribes; a4 makes that subscription
  disposable with the Worker.
- **Logs.** All tests write one log file under one lock. That is contention, not wrong results.
- **Warnings gate.** New files build under `10.0-recommended` with warnings as errors. Worker.cs relies on
  `#pragma warning disable CA1416` and `#nullable disable`; the new files use `[SupportedOSPlatform("windows")]` and
  the same nullable setting. None of them gets a baseline entry in `Agent/.editorconfig` (Worker.cs has none today).
- **Coverage floor.** Moving code does not change what is covered. The dispatcher tests add a little to the 62 %
  `AGENT_FLOOR`.
- **A half-finished migration.** If (b) stalls, facades and instances live side by side. Each b-PR deletes its
  facades, and the guard test from (d) can land early with the remaining statics on its allow list. The list then
  only shrinks.

## Open questions for the owner

1. Is it acceptable to start the split only after #97, #99, #100 and #104 are merged, and to hold new agent
   features (or write them as handlers) while (a) is in progress? a0 can go now.
2. Wiring by hand in Worker, as proposed, or registering handlers in the host's DI container?
3. Comment language in the new files: the agent's code comments are Turkish today. Keep Turkish for consistency
   with the files around them, or write the new files in English?
4. Facades: delete each in the PR that converts its last caller (proposed), or keep `SecureStore.Dir` and similar
   for one release in case scripts outside the repository use them?
5. `POps.Shared`: `ConfigPaths` becomes a parameter and `ServerTrust` an instance, while `LogDirectoryOverride` and
   `Component` stay static. Agreed?
6. Clock: .NET's `System.TimeProvider` with a small fake in the tests (proposed, no new package), or
   `Microsoft.Extensions.TimeProvider.Testing`?
7. CI: run the net10.0 tests twice for the first two weeks of (c), and keep the default thread count on the
   Windows runner?
8. `start_stream` is deprecated and the server never sends it. Keep its handler as is in this series (proposed,
   since the series changes no behaviour) and drop it in a separate PR later?
