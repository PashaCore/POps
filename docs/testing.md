# Testing and coverage

This page answers two questions: how the tests run, and **which critical paths are tested and which are not**.
A high test count says little on its own; the table below is the honest list.

## How the tests run

| Suite | What it is | Where it runs |
|---|---|---|
| `Backend/tests/test_units.py` | Pure unit tests, no database or server. | CI `backend` job |
| `Backend/tests/test_*.py` (others) | Integration tests against a running backend and an empty PostgreSQL database. | CI `security` job, `Backend/tests/run_local.sh` |
| `Agent/POps.Tests` | xUnit tests for the agent, updater logic, shared code and the MSI custom actions (pure logic and temp-folder file operations; no firewall, pipe or service). | CI `test-agent` job (Windows) |
| Migrations | Fresh migrate, schema check, second run must apply nothing. | CI `migrations` job (PostgreSQL 13) |
| Release signing | `tools/sign_release.py selftest`: sign/verify with a temporary key, tampered manifest and artefact rejected. | CI `signing` job |
| Panel | `php -l` on every page only. | CI `dashboard` job |

Run the backend suite locally (never against a production database):

```bash
export DB_HOST=127.0.0.1 DB_PORT=5432 DB_USER=<u> DB_PASS=<p> DB_NAME=<empty test db> JWT_SECRET=dev
COVERAGE=1 bash Backend/tests/run_local.sh   # COVERAGE=1 needs the coverage package; omit it to skip the report
```

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
| Update result → audit log and notification wording | Tested (logic) | `test_units.py`; the WebSocket handler path itself is not exercised |
| **Agent update + rollback on a real machine (updater, MSI downgrade)** | **Manual** | Rollback drill on a real machine (see [agent.md](agent.md)). The drill decision logic is unit-tested (`RollbackDrillTests`); the MSI downgrade is not. The September 2026 false `rollback_failed` came from exactly this gap. |
| Quarantine delivery, resend and reconciliation | Tested | `test_features.py`; agent-side isolation logic in `NetworkIsolationTests`, `QuarantineControlTests` |
| Kiosk lock screen cannot be bypassed | Not tested | Known gap: Ctrl+Alt+Del still reaches Task Manager / sign-out (roadmap) |
| Hash-chained device audit log | Tested | Written by the tested paths above; batched chain verification and tamper detection in `test_units.py`, the `/api/system/audit-verify` endpoint in `test_review4.py` |
| Notifications, webhook SSRF guard | Tested | `test_features.py` |
| Scheduled tasks, software inventory, patch status, reports and CSV formula escaping | Tested | `test_features.py` |
| Clone of an enrolled image refused (4409), keyless requests for a keyed device refused, a bad agent message does not drop the connection, scheduled-task expiry / misfire / no duplicates, encrypted bypass keys | Tested | `test_review4.py` |
| Licences and help desk (panel + agent, throttling) | Tested | `test_helpdesk_licenses.py` |
| Request ID, `/metrics` access control, diagnostics | Tested | `test_ops.py` |
| Migrations from empty and idempotency | Tested | `migrations` job |
| Server self-update and deploy rollback | Manual | Field-verified (`deploy-status.json` `state=ok`); needs root and systemd |
| Panel pages (PHP) | Not tested | Syntax only; the headless-browser end-to-end setup exists locally but not in CI |
| Vision screen tunnel | Partly | Frame scoping tested (`test_remote_authz.py`); the tunnel and tray capture are not |
