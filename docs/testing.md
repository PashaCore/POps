# Testing and coverage

This page answers two questions: how the tests run, and **which critical paths are tested and which are not**.
A high test count says little on its own; the table below is the honest list.

## How the tests run

| Suite | What it is | Where it runs |
|---|---|---|
| `Backend/tests/test_units.py` | Pure unit tests, no database or server. | CI `backend` job (Python 3.12 and 3.10) |
| `Backend/tests/test_protocol.py` | Agent protocol ([`protocol/`](protocol/README.md)): every schema and example, and the real `/ws/agent` and `/ws/vision` handlers, task queue and endpoints run with a fake database and fake sockets; every message they send or accept must match its schema. Needs `jsonschema` (CI installs it). | CI `backend` job (Python 3.12 and 3.10), `security` job under coverage |
| Backend lock file | `tools/backend_lock.sh --check`: `Backend/requirements.lock` regenerated from `requirements.txt` must be unchanged. | CI `backend-lock` job |
| `Backend/tests/test_*.py` (others) | Integration tests against a running backend and an empty PostgreSQL database. | CI `security` job, `Backend/tests/run_local.sh` |
| `Agent/POps.Tests` | xUnit tests for the agent, updater logic, shared code and the MSI custom actions (pure logic and temp-folder file operations; no firewall, pipe or service). | CI `test-agent` job (Windows) |
| `Agent-Linux/tests` | pytest for the Linux agent on the distribution's own `python3` and packages, without root: identity/DNA, manifest verification, command limits and refusals, result spool, inventory parsers, update selection and the installer's rollback paths (fake `dpkg`/`systemctl`), the shared protocol vectors, a mock server; the `.deb` built twice must be byte-identical and is installed with `dpkg -i` (service not started). | CI `linux-agent` job |
| `Agent-Linux/tests/test_integration.py` | The Linux agent from the source tree against a running backend over TLS (`pops-tls` CA): enrollment, platform, inventory, commands with exit codes, acknowledged results, refusal when the terminal is off. | CI `linux-agent-backend` job |
| Migrations | Fresh migrate, schema check, second run must apply nothing. | CI `migrations` job (PostgreSQL 13) |
| Release signing | `tools/sign_release.py selftest`: sign/verify with a temporary key, tampered manifest and artefact rejected. | CI `signing` job |
| Panel syntax and escaping | `php -l` on every page; the HTML-sink scanner (`tools/html_sinks`). | CI `dashboard` job |
| `tests/e2e/` (panel end to end) | Playwright in headless Chromium against the real panel (`php -S`) and backend on a seeded throwaway database: every page at 1440×900 and 390×844, and the main flows (below). | CI `panel-e2e` job, [locally](#panel-end-to-end-tests) |
| Server scripts | `Installer/server/tests/test_deploy.sh`: `pops-deploy-backend` and `pops-selfupdate` in a temporary folder with fake `systemctl`, `curl`, `sudo`, `pip` and Python interpreters, without root. | CI `server-scripts` job |

Run the backend suite locally (never against a production database):

```bash
export DB_HOST=127.0.0.1 DB_PORT=5432 DB_USER=<u> DB_PASS=<p> DB_NAME=<empty test db> JWT_SECRET=dev
COVERAGE=1 bash Backend/tests/run_local.sh   # COVERAGE=1 needs the coverage package; omit it to skip the report
```

### Panel end-to-end tests

`tests/e2e/` sits at the repository root, not under `Dashboard/`, so the panel's web root never serves the test
router, the seed data or `node_modules`. `npx playwright test` starts its own stack (`global-setup.js`) and stops it
afterwards; CI and a local run do the same steps:

1. `db.py prepare`: `DB_NAME` must be empty (or left over from an earlier e2e run, recognised by the
   `pops_e2e_marker` table, which is then wiped). Any other database is refused: the tests delete labs and change
   settings.
2. `python Backend/migrate.py`, then the backend with uvicorn on `127.0.0.1:8099`. The admin password
   (`PANEL_ADMIN_PASS`) and `JWT_SECRET` are generated per run. The backend's outbound requests (GitHub release
   check) go to a closed proxy port, so the stack is offline and the panel's "could not check" path is what runs.
3. `db.py seed seed.sql`: made-up PCs, labs, tasks, 126 log entries, tickets, licences. Two rows carry HTML-like
   text that must stay text.
4. A copy of `Dashboard/` (with `config.php` from `config.example.php`) served by
   `php -S 127.0.0.1:8098 router.php`. The router does the clean addresses (`/devices` → `devices.php`) and forwards
   `/api/`, `/download/` and `/updates/` to the backend, like nginx/Apache in production. `php -S` cannot proxy
   WebSockets, so `/ws/` gets a 404.

Every test fails on: an uncaught page error or a console error (failed requests included; the only exception is the
WebSocket handshake to the panel's own `/ws/`), a request to any origin other than the panel's own (the panel must
work offline), an unexpected `alert`/`confirm`, and a PHP warning, notice or error in the `php -S` log.

| Spec | What it checks |
|---|---|
| `smoke.spec.js` | Sign-in page and all 13 pages at 1440×900 and 390×844: HTTP 200 without a redirect to sign-in, `<title>`, `h1`, the active menu entry, loading indicators gone, no horizontal overflow (`scrollWidth <= innerWidth + 1`). |
| `auth.spec.js` | A protected page redirects to sign-in; sign in, sign out (the session is really gone); a wrong password shows the error. |
| `devices.spec.js` | Search by name and IP filters the list, HTML-like names render as text, the empty state; a row opens the detail drawer (IP, lab, ID, recent tasks) and it closes. |
| `labs.spec.js` | Create a lab, rename it, delete it through the lab menu and its dialogs; each step survives a reload. |
| `tasks.spec.js` | A task created with `POST /api/deploy_orchestration` appears as "Sırada" with its target; the drawer shows command and PC; after an API cancel the list updates by polling; the filters. |
| `logger.spec.js` | Kayıtlar: 25 → 50 per page, next/last/first page, the choice survives a reload; the filter panel (lab, person), its badge and chips, clearing. |
| `system.spec.js` | Sistem: every tab sets `?tab=`, survives a reload and opens from the address; overview tiles; task and event charts draw, metric charts draw or say "Ölçümler toplanıyor"; the 7-day range. |
| `settings.spec.js` | Ayarlar → Genel: the organisation name saves and the sign-in page shows it; the queue limit rejects 0, saves and survives a reload (both restored afterwards). |
| `roles.spec.js` | A viewer created through the API (with deploy, terminal and settings in its permission list) sees only its pages in the menu, gets "Yetkisiz Erişim" on admin pages, has no selection, bulk actions, lab menu, queue buttons or scheduled tasks, and the API refuses its writes. |

Run them locally (PHP 8 CLI with `curl`, Node 20+, Python 3.10+ with `Backend/requirements.txt`, and an empty
database your role owns; ports 8098/8099 free or moved with `E2E_PANEL_PORT` / `E2E_API_PORT`):

```bash
createdb pops_e2e
export DB_HOST=127.0.0.1 DB_PORT=5432 DB_USER=<user> DB_PASS=<password> DB_NAME=pops_e2e
export E2E_PYTHON=venv/bin/python            # default: python3; E2E_PHP defaults to php
cd tests/e2e
npm ci
npx playwright install chromium               # once; --with-deps on a fresh Linux machine
npx playwright test                           # about 35 s; the same database can be reused
npx playwright show-report                    # after a failure: steps, screenshots, traces
```

The stack's logs (`backend.log`, `php.log`, `migrate.log`, `seed.log`) are in `tests/e2e/.stack/`; CI uploads them
with the HTML report and the traces as the `panel-e2e-report` artifact when the job fails.

An integration test that times out once and then passes is often the host, not the code: while the disk stalls, every
PostgreSQL commit waits (the PostgreSQL log then shows `using stale statistics instead of current ones because stats
collector is not responding`). Check that log for the time of the failure before chasing it in the code.

## Coverage

The backend runs under `coverage.py` during the integration suite (config: `Backend/.coveragerc`); the report is
written to the CI job summary and uploaded as the `backend-coverage` artifact. CI fails if total coverage drops
below the floor in `.github/workflows/ci.yml` (`--fail-under`). Raise the floor when coverage goes up; never lower
it to make a change pass.

Baseline (2026-09-29): **backend 69.6 %** of statements. Lowest modules, and the next targets:

| Module | Coverage | Why it is low |
|---|---|---|
| `system_routes.py` | 29 % | GitHub release fetch, agent deploy and self-update paths need network or root; only upload/verify and version are exercised. |
| `pops/wol.py` | 25 % | Wake-on-LAN sends UDP broadcasts; untested. |
| `pops/routers/tasks.py` | 38 % | Orchestration, package upload and storage endpoints. |
| `pops/routers/devices.py` | 41 % | Device edit/delete, labs, hardware inventory views. |
| `migrate.py` | 52 % | The CLI branch is exercised by the `migrations` job, which does not collect coverage. |

Agent coverage is collected with `coverlet.collector` (`dotnet test --collect:"XPlat Code Coverage"`, both targets:
the agent on net10.0-windows and the MSI custom actions on net472) and shown in the `test-agent` job summary.
Baseline (2026-09-29): `POps.Shared` 80 %, `POpsAgent` 48 %, MSI custom actions 77 %. `coverlet.collector` stays on
6.0.4: 8.x and 10.x write an empty report for the net472 target.

0.1.14 (2026-10-02, local run): `POpsAgent` **64.3 %**, `POps.Shared` 86.0 %, MSI custom actions 77.2 %. The
"Agent coverage summary" step fails when `POpsAgent` drops below **62 %** (`AGENT_FLOOR` in
`.github/workflows/ci.yml`); raise it when coverage goes up, never lower it to make a change pass. The server
command handling is covered through `Worker.HandleServerMessageAsync` with outgoing messages captured
(`SendOverride`), a fake quarantine and a fake PowerShell runner for the firewall (`NetworkIsolation.ScriptRunner`),
so tests never touch the firewall. Still low: the tray pipe server, the Windows Update agent, WMI inventory and the
Vision tunnel, which need a user session, Windows Update or a live server.

## Critical paths

"Tested" means an automated test fails if the behaviour breaks. "Manual" means it is verified by hand on a real
machine and a regression would not be caught by CI.

| Path | Status | Tests / how verified |
|---|---|---|
| Panel login, 2FA (TOTP), 2FA bypass attempt | Tested | `test_2fa.py` |
| Session revocation (token_version), role read from the database | Tested | `test_f4_accountability.py` |
| Remote input / preview needs admin + an open audit session; frames only to that session | Tested | `test_remote_authz.py` |
| Agent enrollment, per-device secret, enforce mode, cross-device spoofing, enrollment takeover | Tested | `test_security.py`, `test_agent_authz.py` |
| Signed release: server rejects a wrong key; agent verifies signature, size, hash, downgrade | Tested | `test_security.py`, `ReleaseVerifierTests`, `AgentUpdateTests`, `signing` job |
| Update result → audit log and notification wording | Tested | `test_units.py` (wording); `test_protocol.py` drives the `/ws/agent` handler: stored and acknowledged, no acknowledgement without `result_id` |
| Agent ↔ server message formats (server side) | Tested | `test_protocol.py` against `docs/protocol`; the agent side is described in `docs/protocol/AGENT_TESTS.md` and not yet implemented |
| **Agent update + rollback on a real machine (updater, MSI downgrade)** | **Manual** | Rollback drill on a real machine (see [agent.md](agent.md)). The drill decision logic is unit-tested (`RollbackDrillTests`); the MSI downgrade is not. The September 2026 false `rollback_failed` came from exactly this gap. |
| Quarantine delivery, resend and reconciliation | Tested | `test_features.py`; agent-side isolation logic in `NetworkIsolationTests`, `QuarantineControlTests` |
| Kiosk lock screen cannot be bypassed | Not tested | Known gap: Ctrl+Alt+Del still reaches Task Manager / sign-out (roadmap) |
| Hash-chained device audit log | Tested | Written by the tested paths above; batched chain verification and tamper detection in `test_units.py`, the `/api/system/audit-verify` endpoint in `test_review4.py` |
| Notifications, webhook SSRF guard | Tested | `test_features.py` |
| Scheduled tasks, software inventory, patch status, reports and CSV formula escaping | Tested | `test_features.py` |
| Clone of an enrolled image refused (4409), keyless requests for a keyed device refused, a bad agent message does not drop the connection, scheduled-task expiry / misfire / no duplicates, encrypted bypass keys | Tested | `test_review4.py` |
| Licences and help desk (panel + agent, throttling) | Tested | `test_helpdesk_licenses.py` |
| Modules: organisation and lab settings, dependencies, `409` on every module, queue/scheduler/policy enforcement, install profiles | Tested | `test_modules.py`, `test_units.py` |
| Request ID, `/metrics` access control, diagnostics, overview history | Tested | `test_ops.py`, `test_units.py` |
| API tokens (superadmin-only management, hash-only storage, viewer GET-only, admin limits, expiry and revocation, `last_used_at` throttle, `token:<name>` in tasks and audit), `/api/v1` and REST names equal to the old paths, `task_sequence`/`taskSequence`, CSRF for cookie sessions, shared rate limit and metric labels | Tested | `test_api_tokens.py`, `test_units.py` |
| Exam mode: validation (allow list, end time, programs), roles, delivery on start, re-delivery on reconnect and to PCs moved in, `enabled: false` on end, on reconnect after an early end, to PCs moved out and to agents still in exam, auto-end (read path and scheduler function), module off and lab deletion, left-early notification, `capability_denied`, audit entries, history | Tested | `test_exam.py`, `test_units.py`; the agent-side firewall, banner and process block are the agent's |
| `docs/openapi.json` matches the code | Tested | CI `backend` job (`tools/export_openapi.py --check`) |
| Demo accounts (`POPS_DEMO_USERS`) cannot change anything: every write endpoint from the route table returns `403` | Tested | `test_demo.py` |
| File transfer (server side): push and pull end to end with simulated agents, single-use tokens, cross-device refusal, size caps, roles, capability, audit, 7-day expiry of pulled files | Tested | `test_files.py`, `test_units.py`; the agent side is built and tested by the agent team |
| Migrations from empty and idempotency | Tested | `migrations` job |
| Server self-update and deploy rollback | Tested (fakes) | `test_deploy.sh`: rollback of code and venv after a failed `pip`, copy or health check; venv rebuilt when its Python is too old (and put back on failure); early stop without a new enough Python; signed release tags. The real systemd path is field-verified (`deploy-status.json` `state=ok`). |
| Panel pages (PHP) | Tested | End-to-end smoke of every page at desktop and phone width, and the main flows (sign-in, devices, labs, tasks, logs, Sistem, Ayarlar, viewer role) in Chromium: `tests/e2e/`, CI `panel-e2e` job. Not covered: Vision and remote command against a live agent (no agent and no WebSocket proxy in the stack). |
| Vision screen tunnel | Partly | Frame scoping tested (`test_remote_authz.py`); the tunnel and tray capture are not |
