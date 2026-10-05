# Fuzzing

The backend code that parses untrusted input is fuzzed with [Atheris](https://github.com/google/atheris), a
coverage-guided Python fuzzer built on libFuzzer. Each target in `fuzz/` feeds generated inputs to the real code and
asserts a property that must hold for every input. A failed assertion or an unexpected exception is a finding.

## Targets

| Target | Code under test | Property |
| --- | --- | --- |
| `fuzz_agent_ws.py` | The `/ws/agent` handler (`pops/routers/agents.py`) with a fake database and socket, as in `Backend/tests/test_protocol.py`. Input: the frames an agent sends, separated by NUL bytes. Each input runs twice: as the first messages of a connection, and after a valid first heartbeat. | The handler never raises. A connection whose frames are all JSON objects is never closed and no frame counts as an error: a field with the wrong type is ignored ([protocol rules](protocol/README.md#rules-for-both-sides)). Every message sent to the agent has a schema in `docs/protocol/server-to-agent` and only documented fields. Heartbeats, results, capabilities, previews, file transfer results, exam state, audit entries and panel broadcasts name only the connection's device. Every string written to the database, the audit log or the heartbeat batch can be stored by PostgreSQL (no NUL, valid UTF-8). The first message's name and hardware fields reach `clients` with the column's type. |
| `fuzz_request_models.py` | Every request model in `pops/models.py`, and the DNS domain list cleaner of the agent policy. Input: a JSON body. | Validation returns a model or raises `ValidationError` (422), nothing else. `StrictInput` models reject unknown fields, nested ones included. `target_mode` is one of `ALL`, `LAB`, `PC`. Cleaned domain lists hold at most 5000 unique names per category, without whitespace, control or format characters, paths or schemes. |
| `fuzz_release_manifest.py` | `Backend/release_verify.py` (`upload-release`, `fetch-release`). The first input byte picks a mode: the target signs the manifest with a temporary key, takes the signature from the input, or signs and then flips a byte. | `verify_manifest()` returns a manifest or raises `ReleaseVerifyError`, nothing else. Only the exact signed bytes are accepted. An accepted manifest has the shape its callers rely on ([release-manifest.json](protocol/release-manifest.json)), and `artifact_entry()` returns an artefact or `None`. |
| `fuzz_notify_settings.py` | The notification settings check (`pops/routers/notifications.py`) and `resolve_webhook()` (`pops/notify.py`). Input: webhook URL, e-mail list and the DNS answer. DNS is faked; IP literals go through the real `getaddrinfo` in numeric-only mode, so nothing touches the network. | The check returns the settings or a 400, nothing else. Accepted e-mail lists hold at most 20 single addresses. An accepted webhook is `http(s)`, at most 500 characters, and every address DNS returns is allowed: with `NOTIFY_WEBHOOK_ALLOW_PRIVATE` off, no loopback, private, link-local (cloud metadata), CGNAT, ULA, unspecified, multicast or broadcast address passes in any notation, IPv4-mapped IPv6 included. The connection goes to one of the checked addresses. |

`fuzz/common.py` holds the shared setup: the backend import path, dummy values for the required environment
variables (no database or network is used), and instrumentation for POps code only. `fuzz/json.dict` is a small
libFuzzer dictionary of JSON tokens and values Python or PostgreSQL treat specially (`1e999`, `\u0000`, lone
surrogates). `fuzz/corpus/<target>/` holds the seed inputs. `fuzz_agent_ws.py` also starts from the protocol
examples in `docs/protocol/examples/agent-to-server/`. Inputs that once failed are kept as `regression-*` seeds, so
they run first every time.

## Running locally

Atheris has wheels for CPython 3.12 and newer on Linux x86-64. Use a separate virtual environment:

```bash
python3.12 -m venv /tmp/fuzz-venv
/tmp/fuzz-venv/bin/pip install --require-hashes -r Backend/requirements.lock
/tmp/fuzz-venv/bin/pip install --require-hashes -r .github/requirements/fuzz.lock
PATH=/tmp/fuzz-venv/bin:$PATH fuzz/run.sh            # every target, 60 seconds each
PATH=/tmp/fuzz-venv/bin:$PATH fuzz/run.sh 600 agent_ws    # one target, 10 minutes
```

New inputs found during a run go to a temporary directory, so the seeds in the repository do not change. When a
target fails, its input is saved as `<target>-crash-<hash>` in `$FUZZ_ARTIFACTS` (by default a temporary directory;
the script prints the path) and the script exits with 1. To reproduce it:

```bash
python fuzz/fuzz_agent_ws.py /path/to/agent_ws-crash-<hash>
```

Fix the code, add a unit test for the case to `Backend/tests/test_units.py`, and copy the input to
`fuzz/corpus/<target>/regression-<short-name>`.

## CI

The `fuzz` job in `.github/workflows/ci.yml` runs `fuzz/run.sh 60` on every pull request and push to `main` (not on
release tags), on Python 3.12 with dependencies installed from the hash-locked files. When a target fails, the job
uploads the failing input as the `fuzz-failures` artifact. CI's flake8 step also lints `fuzz/`.

## Adding a target

1. Create `fuzz/fuzz_<name>.py`. Import `common` first, import the POps modules inside
   `with common.backend_imports():`, and end with `common.run(TestOneInput)`.
2. In `TestOneInput(data)`, call the code and `assert` the property. An exception the code is allowed to raise must be
   caught explicitly; anything else is a finding.
3. Add a few seed inputs under `fuzz/corpus/<name>/` and the name to the default list in `fuzz/run.sh`.
4. Run it locally for a few minutes before you open the pull request.

## Findings so far

The first runs found these bugs, all fixed:

- An agent that sent `\u0000` or a lone surrogate in any string (for example in a command's output) made the
  database write fail. A task result was never stored, and the agent kept resending it. In the batched heartbeat
  write, one such heartbeat dropped the heartbeats of every device in the batch. Agent messages are now cleaned
  when they are parsed.
- A wrong-typed `dna_payload`, `hardware`, `capabilities` or `hostname` in the first message, or a number such as
  `1e999` in `agent_health`, closed the connection with 1011 instead of being ignored.
- `vision_rejected` was forwarded to the panels as sent. Any connected agent could name another device, or no
  device, and end an administrator's remote-control session. The server now sets `hw_id` to the connection's device.
- A signed release manifest that was not a JSON object, or had malformed artefact entries, raised an error other than
  `ReleaseVerifyError` (HTTP 500 instead of 400).
- The agent policy kept domain entries with tabs or control characters. Such an entry never matches, and a NUL made
  saving the policy fail.
