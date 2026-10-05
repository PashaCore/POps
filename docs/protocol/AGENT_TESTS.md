# Protocol vectors in the agent tests

The files in [`examples/`](examples/) are shared test vectors. The backend checks them in
`Backend/tests/test_protocol.py`; every agent implementation should check them too, so that a change on either side
shows up as a failing test instead of a field the other side silently ignores. This page describes the tests for
the Windows agent (`Agent/POps.Tests`, xUnit). The Linux agent runs the same checks in
`Agent-Linux/tests/test_protocol_vectors.py` (pytest; the schema checks need `python3-jsonschema`).

## Finding the files

From the test project `Agent/POps.Tests` the vectors are at `../../docs/protocol/examples` and the schemas at
`../../docs/protocol/agent-to-server` and `../../docs/protocol/server-to-agent`. Tests run from the build output
(`bin/<configuration>/net10.0-windows/`), so resolve the path from the repository root, as
`ServerContractTests` already does with `Backend/pops/models.py`:

```csharp
static string Protocol(params string[] parts) =>
    Path.Combine(new[] { TestEnvironment.RepoRoot(), "docs", "protocol" }.Concat(parts).ToArray());

// every server -> agent vector, as raw text (the agent parses the exact bytes it would receive)
IEnumerable<object[]> ServerVectors() =>
    Directory.GetFiles(Protocol("examples", "server-to-agent"), "*.json")
        .Select(path => new object[] { Path.GetFileName(path) });
```

Do not copy the files into the test project: the point is that both sides read the same copy.

File names are `<type or action>[.<variant>].json`; the part before the first dot names the schema
(`examples/server-to-agent/execute.queue.json` → `server-to-agent/execute.json`). Each file holds one message.
`examples/unknown/` holds one message per direction that no schema describes.

## Tests to add

All of them belong to the `net10.0-windows` target and use the isolated environment of `TestEnvironment`
(temporary `SecureStore.Dir`, `AgentUpdate.DataDir`, fake kiosk registry). Build the worker as
`WorkerCommandTests` does: `SendOverride` collects outgoing messages, and `QuarantineControl` gets the fake
isolation callbacks, so no firewall rule or real setting is touched.

1. **Every server → agent vector is accepted.** `[Theory]` over `examples/server-to-agent/*.json`: pass the file
   text to `Worker.HandleServerMessageAsync(text, null, CancellationToken.None)`; it must not throw. Check the
   effect where it is observable:

   | Vector | Expected |
   | --- | --- |
   | `server_info.json` | `Handshake.Supports("result_ack") == true` and `Supports("update_result_ack") == true` |
   | `server_info.0_1_21.json` | the same; this is what servers 0.1.14 to 0.1.21 send (no `protocol`, which means 1) |
   | `set_secret.json` | the secret is stored (`AgentCredentials.CurrentSecret`) |
   | `set_identity.json` | `HwId` is `HW-9B41D07E5A2C` |
   | `set_bypass_secret.json` | ignored, nothing stored or sent (the test worker has no device-secret connection); `BypassSecretCommand.Process(secret, true, …)` with the vector's `secret` stores it and returns the `fingerprint` of `examples/agent-to-server/bypass_secret_ack.json` |
   | `execute*.json` with the terminal capability off | a `result` with `exit_code` -5 and a `capability_denied` with the same `task_id` |
   | `winget_install*.json` | agents that do not implement it (and do not send `X-Agent-Features: winget`): ignored, nothing sent. Agents that do, with the terminal capability off: a `result` with `exit_code` -5 and a `capability_denied` with the same `task_id`; with the `deploy` module off: the same with `capability` `deploy` and `reason` `module_disabled` |
   | `cancel_task.json`, `result_ack.json`, `update_result_ack.json`, `unlock.json` | no exception; nothing sent |
   | `lockdown.json` | the fake isolation is applied (`NetworkIsolation.StatePath` exists) |
   | `exam_mode.json` | exam mode applied with the vector's allow list, message, program list and `until`; an `exam_state` with `enabled: true` is sent. With the `exam` capability off: one `capability_denied` (`capability` `exam`, `action` `exam_mode`) and nothing applied |
   | `exam_mode.off.json` | no exception; exam mode removed if it was applied (then an `exam_state` with `enabled: false`) |
   | `set_capabilities.*.json` | the capabilities are off afterwards and a `capabilities` message is sent |
   | `remote_input.*.json` with Vision off | one `capability_denied` (capability `vision`) |
   | `select_monitor*.json`, `set_quality.json`, `clipboard.json` | Vision-channel messages: through `Worker.HandleVisionControlAsync` with Vision off, one `capability_denied`; `VisionRelay.TrayMessageFor` gives `VISION_SELECT:1` / `VISION_SELECT:all`, `VISION_QUALITY:…`, and `CLIPBOARD_SET:…` only with `userAccepted` |
   | `scan_updates.json`, `install_updates.*.json`, `wake_peer.json` with the module off (`AgentModules`) | `capability_denied` with `reason` `module_disabled` |
   | `update_agent.json` | refused: the manifest is signed with the test key, not the release key (see 3) |
   | `file_push.json`, `file_pull.json` with file transfer off | a `capability_denied` (capability `files`) or a `file_result` `rejected` with the same `transfer_id`; nothing is downloaded or uploaded (with it on, the URLs point at a test server that does not exist: a `file_result` `failed`) |
   | `unknown/server-to-agent.json` | ignored: no exception, nothing sent |

   Running `execute` for real is already covered by `WorkerCommandTests`; here it is enough to refuse it, so the
   vector does not start `cmd.exe`. `start_stream.json` is deprecated; the Windows agent still accepts it.

2. **Messages the agent builds match the agent → server schemas.** Validate with a draft 2020-12 validator, for
   example the NuGet package `JsonSchema.Net` (test project only):

   ```csharp
   var schema = JsonSchema.FromFile(Protocol("agent-to-server", "heartbeat.json"));
   EvaluationResults r = schema.Evaluate(JsonSerializer.SerializeToNode(worker.HeartbeatPayload()),
       new EvaluationOptions { OutputFormat = OutputFormat.List });
   Assert.True(r.IsValid);
   ```

   Cover at least `Worker.HeartbeatPayload()` (with and without `dna_payload`), `AgentCapabilities.StatusMessage()`,
   the `result` and `capability_denied` of a refused `execute`, `AgentUpdate.PendingResultMessage()` from a sample
   `update-result.json`.

   Also check that the agent sends **only documented fields**: every property name of the produced JSON object (and
   of nested objects such as `agent_health` and `dna_payload`) must appear under `properties` of the schema. The
   backend test applies the same rule (`undocumented()` in `Backend/tests/test_protocol.py`):

   ```csharp
   static IEnumerable<string> Undocumented(JsonElement schema, JsonElement message, string prefix = "")
   {
       if (message.ValueKind != JsonValueKind.Object || !schema.TryGetProperty("properties", out JsonElement props))
           yield break;
       foreach (JsonProperty p in message.EnumerateObject())
       {
           if (!props.TryGetProperty(p.Name, out JsonElement sub)) { yield return prefix + p.Name; continue; }
           foreach (string inner in Undocumented(sub, p.Value, prefix + p.Name + ".")) yield return inner;
       }
   }
   ```

3. **The signed update vector.** Decode `manifest` from `examples/server-to-agent/update_agent.json`:
   `ReleaseVerifier.VerifySignature(bytes, manifest_sig, ReleaseVerifierTests.TestPublicKey)` is true, with the
   built-in release key it is false, and `ReleaseVerifier.Parse(bytes)` gives version `0.1.3-alpha` with one agent
   MSI. The bytes are the same as `TestData/manifest.json`.

4. **The agent → server vectors stay in step with the agent.** For each message the agent builds in test 2, the
   example of the same name (for example `examples/agent-to-server/capabilities.default.json`) must have the same
   set of top-level keys, so the vectors show what the agent really sends. A key the agent stops sending, or a new
   one, then fails here and the example and schema are updated in the same change.

## When the protocol changes

Change the schema and the examples first, then the code on both sides. `docs/protocol/README.md` has the
versioning rules (unknown fields and messages are ignored; additive changes keep protocol 1).
