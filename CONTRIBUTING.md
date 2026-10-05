# Contributing to POps

Thank you for helping with POps (Pasha Operations Platform). This guide explains how the repository is laid out,
how to run each part locally, which rules CI enforces, which areas need extra review, and how releases are made.

By participating you agree to the [Code of Conduct](CODE_OF_CONDUCT.md); report unacceptable behaviour to
`opensource@pashacore.com.tr`. **Security vulnerabilities are not reported as public issues**; see
[Reporting security issues](#reporting-security-issues).

Useful background before a first change: [`docs/getting-started.md`](docs/getting-started.md) (terms),
[`docs/architecture.md`](docs/architecture.md) (components and flows), [`docs/decisions.md`](docs/decisions.md)
(why things are built this way) and [`ROADMAP.md`](ROADMAP.md).

## Ways to contribute

- **Bugs:** search the [issues](https://github.com/PashaCore/POps/issues) first. A new report should include the
  server OS, the backend and agent versions (the panel's **Sistem** page shows both), the Windows version
  for agent problems, steps to reproduce, and the relevant log lines
  ([`docs/troubleshooting.md`](docs/troubleshooting.md) lists where the logs are). Remove host names, IP addresses
  and secrets from logs before posting.
- **Enhancements:** open an issue that explains the problem for lab administrators, not only the proposed
  solution. Larger design changes are discussed before code is written.
- **Pull requests:** see [Pull requests](#pull-requests).
- **Questions:** ask in [GitHub Discussions](https://github.com/PashaCore/POps/discussions) (category **Q&A**).
  Issues are for bugs and concrete proposals; the issue forms point to the right place.

## Good first issues

Issues labelled
[`good first issue`](https://github.com/PashaCore/POps/issues?q=is%3Aissue+is%3Aopen+label%3A%22good+first+issue%22)
are small, self-contained tasks for a first contribution. Each one says why it matters, which files to look at and
what "done" means (acceptance criteria). They avoid the areas the maintainers are changing at the moment, so a pull
request does not collide with other work, and most of them need neither a Windows machine nor a running server.
[`help wanted`](https://github.com/PashaCore/POps/issues?q=is%3Aissue+is%3Aopen+label%3A%22help+wanted%22) marks
larger items where outside help is welcome.

To pick one up:

1. Read the issue and the files it points to. If something is unclear, ask in the issue before you start.
2. Comment that you would like to take it, so two people do not work on the same thing. A maintainer assigns it
   to you. Take one at a time.
3. Fork the repository, branch from `main` and keep the change to what the issue asks for.
4. Open a pull request that says `Fixes #<number>`, fill in the template and check the acceptance criteria one by
   one. CI must pass.
5. If you cannot finish, say so in the issue so someone else can take it over. That is fine.

Maintainers writing a good first issue: describe the context, name the files, list acceptance criteria, keep it to
a change one person can review in one sitting, and add the label.

## Repository layout

| Path | Contents |
| --- | --- |
| `Backend/` | FastAPI backend. `server.py` only builds the app. `pops/` holds configuration, database pool, panel security, agent authentication, audit log, connection manager, models, notifications, scheduler, task queue, hardware-DNA identity and Wake-on-LAN, and the newer modules: `tenancy.py` (organisational-unit scopes), `sso.py` / `sso_ldap.py` / `sso_oidc.py` (directory and OIDC sign-in), `cluster.py` (several workers with Redis), `exams.py`, `power.py`, `winget.py`, `filestore.py`, `peer_cache.py`, `glpi.py`; `pops/routers/` has one router per endpoint group, and `pops/routers/system/` covers versions, releases, enrollment, agent updates, self-update and capabilities (`system_routes.py` is its old import name); `release_verify.py` checks signed releases. `migrate.py` and `migrations/NNNN_*.sql` own the schema. `setup_env.py` is the server setup script. `tests/` holds the pytest unit tests and the script tests (`conftest.py` explains how they are collected). `requirements.txt` is the input, `requirements.lock` the hash-locked install. `storage/`, `updates/`, `releases/` and `transfers/` are runtime data and git-ignored. |
| `Dashboard/` | PHP 8 panel: one file per page (`index.php`, `devices.php`, `system.php`, ...), `includes/` (`header.php` with the fetch wrapper and escaping helpers, `session.php`, `sidebar.php`, `config.example.php`), `assets/` (shared scripts, the icon set, bundled fonts) with `assets/pages/` for page scripts moved out of the PHP files, and `lang/en/` for the English texts ([`docs/i18n.md`](docs/i18n.md)). |
| `Agent/` | .NET 10 agent: `POps.Agent` (Windows service), `POpsTray`, `POpsWatchdog`, `POpsUpdater`, `POps.Shared` (helpers shared by the programs), `POps.Tests` (xUnit). `Directory.Build.props` takes the version from `VERSION` and turns on the warnings gate; `.editorconfig` holds the warning baseline. |
| `Agent-Linux/` | Linux agent for Pardus/Debian/Ubuntu (Python 3 on the distribution's packages, decision D-22): `pops_agent/` package, `pops-agent` launcher, `debian/` (control file, systemd unit, maintainer scripts), `build_deb.py` (reproducible `.deb`), `tests/` (pytest) ([`Agent-Linux/README.md`](Agent-Linux/README.md)). |
| `Installer/agent/` | WiX 5 MSI (`Package.wxs`, `POps.Agent.Installer.wixproj`) and its custom actions (`CustomActions/`, .NET Framework 4.7.2). |
| `Installer/server/` | `install.sh` (native install), the nginx and Apache templates, `pops-deploy-backend` (deploy with health check, pre-migration dump and rollback), `pops-selfupdate` with its systemd `.path` and `.service` units, `pops-backup` / `pops-restore`, `pops-tls`, and `tests/` for the scripts. |
| `docker/`, `docker-compose.yml` | Optional container setup ([`docs/docker.md`](docs/docker.md)). |
| `fuzz/` | Atheris fuzz targets (`fuzz_*.py`), their seed inputs in `corpus/` and `run.sh` ([`docs/fuzzing.md`](docs/fuzzing.md)). |
| `tools/` | `sign_release.py` (sign, verify, generate keys, self-test), `backend_lock.sh` (`Backend/requirements.lock` and `.github/requirements/*.lock`), `export_openapi.py` (`docs/openapi.json`), `agent_simulator.py` (load test), `bench_charts.py` and `bench_devices_delta.py` (benchmark charts and measurements), `demo_fleet.py` (fake PCs for the demo), `check_docs_versions.py` and `check_nullable_baseline.py` (CI checks), `i18n/` and `html_sinks/` (panel checks). |
| `tests/` | `e2e/`: panel end-to-end tests (Playwright); `unit-js/`: JavaScript unit tests for the panel helpers (Node's built-in test runner). |
| `deploy/demo/` | The public read-only demo ([`deploy/demo/README.md`](deploy/demo/README.md)). |
| `keys/` | The release **public** key, the tag-signing public key and `allowed_signers`. Private keys (`*.key.pem`) are git-ignored and never committed. |
| `docs/` | Operator and developer documentation, including the decision log `decisions.md`, the agent protocol in `protocol/` (JSON Schemas, example messages, `AGENT_TESTS.md`), design notes in `design/`, `integrations/glpi.md`, the Turkish guides in `tr/` and `openapi.json`. |
| `.github/` | `workflows/ci.yml`, `release.yml`, `codeql.yml`, `scorecard.yml`, `lock-refresh.yml`; `requirements/` (hash-locked CI tools: `pytest`, `backend-ci`, `fuzz`, `signing`, `lock-tools`); `scripts/ci_schema_check.py`; Dependabot; CODEOWNERS; issue and PR templates. |
| `VERSION`, `CHANGELOG.md` | The single version source and the changelog. |
| `Shared/`, `assets/`, `screenshots/` | A placeholder README; images used by the README (`screenshots/v0.1.23/` holds the current ones). |

The panel is written in Turkish and every page is translated to English ([`docs/i18n.md`](docs/i18n.md)). `CHANGELOG.md`
and most of `docs/` are in English; code comments are mostly Turkish.

## Local setup

### Backend (Python 3.10+, PostgreSQL)

CI and the reference server use **Python 3.12**; CI also runs the unit tests on **3.10**, the minimum (Ubuntu
22.04), so do not use syntax or libraries that need 3.11 or newer (`tomllib`, `except*`, `typing.Self`, ...).
CI tests migrations on PostgreSQL 13.

```bash
python3.12 -m venv venv                     # any 3.10+; venv/ is git-ignored
. venv/bin/activate
pip install --require-hashes -r Backend/requirements.lock
pip install flake8 jsonschema==4.26.0     # lint and the protocol test only
pip install --require-hashes -r .github/requirements/pytest.lock   # the test runner

# a development role and database (as the PostgreSQL superuser)
sudo -u postgres createuser --pwprompt pops_dev
sudo -u postgres createdb -O pops_dev pops_dev

cp .env.example .env                        # repository root; set at least:
#   JWT_SECRET  (python3 -c "import secrets; print(secrets.token_hex(32))")
#   DB_USER=pops_dev  DB_PASS=...  DB_NAME=pops_dev  PANEL_ADMIN_PASS=...
python Backend/migrate.py                   # --status lists applied and pending migrations
cd Backend && python -m uvicorn server:app --host 127.0.0.1 --port 8000 --reload
curl http://127.0.0.1:8000/api/health       # {"status":"ok","database":true,...}
```

- The backend and `migrate.py` read `.env` (repository root or `Backend/`). Variables already set in the
  environment take precedence.
- The first start creates `PANEL_ADMIN_USER` (default `admin`) as superadmin if that account does not exist.
- Migrations also run at every startup; running `migrate.py` first just shows errors earlier.
- Do **not** use `Backend/setup_env.py` for development. It is the server setup script: it also changes the
  database role's password and the panel admin's password.
- `--reload` is for development. Production runs one uvicorn worker without it ([D-01](docs/decisions.md)).
- Code layout: [`docs/backend.md`](docs/backend.md). Endpoints: [`docs/api.md`](docs/api.md). Settings:
  [`docs/configuration.md`](docs/configuration.md).

### Panel (PHP 8)

Needs PHP 8 with the `curl` extension and nginx or Apache with PHP.

```bash
cp Dashboard/includes/config.example.php Dashboard/includes/config.php   # git-ignored
```

- The browser calls `/api/` and `/ws/` on the panel's **own origin**, so the web server must serve `Dashboard/` and
  proxy `/api/`, `/ws/` (with WebSocket upgrade), `/updates/` and `/download/` to the backend. `php -S` alone does
  not do that. Working examples: [`Installer/server/nginx.example.conf`](Installer/server/nginx.example.conf) and
  [`docker/apache-pops.conf`](docker/apache-pops.conf); or use Docker Compose (below).
- PHP signs users in by calling the backend at `POPS_API_INTERNAL_URL` (default `http://localhost:8000`), taken
  from the web server's environment or the repository-root `.env`.
- Plain `http://` works on a development machine; the cookies are marked `Secure` only over HTTPS.
- New pages include `includes/header.php`. It provides `escapeHtml()` and `jsArg()` for API values and wraps
  `fetch` so `/api/` calls send the cookie and the `X-Requested-With` header the backend requires.
- Interface text is written in Turkish and wrapped for translation (`_e('…')` / `__('…')` in PHP, `POps.t('…')` in
  JavaScript); the English goes into `Dashboard/lang/en/<page>.json`. How to convert a page, the rules and the
  glossary: [`docs/i18n.md`](docs/i18n.md).

### Agent (Windows, .NET 10 SDK)

Needs 64-bit Windows and the .NET 10 SDK. WiX and the other build tools come from NuGet; nothing else has to be
installed. There is no solution for the whole agent, so build the projects CI builds:

```powershell
dotnet build Agent/POps.Agent/POps.Agent.sln -c Release
dotnet build Agent/POpsTray/POpsTray.csproj -c Release
dotnet build Agent/POpsWatchdog/POpsWatchDog.sln -c Release
dotnet build Agent/POpsUpdater/POpsUpdater.sln -c Release
dotnet test Agent/POps.Tests/POps.Tests.csproj -c Release
```

- **Warnings are errors.** The agent, tray, watchdog, updater and `POps.Shared` build with the .NET 10 recommended
  analyzers (`AnalysisLevel` `10.0-recommended`) and `TreatWarningsAsErrors` (`Agent/Directory.Build.props`;
  `POps.Tests` is not gated, and NuGet vulnerability warnings stay warnings). The warnings that existed when the
  gate was added are listed per file and rule in `Agent/.editorconfig`; any other warning fails the build and CI.
  When a listed file is clean, delete its lines; never add lines. Nullable reference types are on in every product
  project, the MSI custom actions included. Files that were not nullable-clean carry a `#nullable disable` line and
  are listed in `tools/nullable_baseline.txt`; `tools/check_nullable_baseline.py` (CI) fails when any other file
  turns nullable off or a listed file no longer does. When you clean a file, delete the line and its list entry; new
  files must be nullable-clean.
- The tests cover logic only (not the firewall, the pipe or the service), need no administrator rights and run in
  a temporary folder; they never touch `C:\POps`, `C:\POpsData` or `C:\POpsLogs`. They target `net10.0-windows`
  (agent) and `net472` (MSI custom actions).
- To try a change end to end, build the MSI as in [`Installer/README.md`](Installer/README.md#build-locally) and
  install it on a **disposable Windows VM**. The agent runs as SYSTEM, resets folder permissions and can add
  firewall rules.
- The agent refuses a plain `http://` server unless it is on the same machine. Use
  `SERVER_URL=http://127.0.0.1:8000` for a backend on the VM itself; otherwise put TLS in front with a certificate
  the VM trusts. Enrollment tokens are created on the panel's **Sistem** page.
- Agent internals: [`Agent/README.md`](Agent/README.md), [`docs/agent.md`](docs/agent.md).

### Docker Compose (alternative)

Runs PostgreSQL, the backend and the panel (Apache, with the proxy already configured):

```bash
cp .env.example .env    # JWT_SECRET, BYPASS_SECRET, DB_USER, DB_PASS, DB_NAME, PANEL_ADMIN_PASS
docker compose up -d --build
curl http://127.0.0.1:8080/api/health       # panel: http://127.0.0.1:8080/login.php
```

The code is copied into the images at build time, so run `docker compose up -d --build` again after a change.
Details and limits: [`docs/docker.md`](docs/docker.md).

## Checks and tests

### What CI runs

`.github/workflows/ci.yml` runs on every push and pull request to `main`; `codeql.yml` adds CodeQL for Python,
JavaScript and C#; `scorecard.yml` runs the OpenSSF Scorecard on pushes to `main` and weekly (results under
Security → Code scanning and on the README badge).

| Job | Checks | Run it locally |
| --- | --- | --- |
| Build (4 agent projects) | `dotnet build -c Release` of `POps.Agent`, `POpsUpdater`, `POpsWatchdog` and `POpsTray` | see [Agent](#agent-windows-net-10-sdk) |
| Agent unit tests | `dotnet test Agent/POps.Tests/POps.Tests.csproj` (.NET 10 and .NET Framework 4.7.2) with a coverage summary | same |
| Backend (Python 3.12, 3.10) | `flake8 Backend/ Agent-Linux/ tools/ assets/readme/ fuzz/` (3.12); importing `server` and `setup_env`; `test_units.py`; `docs/openapi.json` is up to date (3.12); `test_protocol.py` | `flake8 Backend/ Agent-Linux/ tools/ assets/readme/ fuzz/`, `python -m pytest -m "not integration"`, `python tools/export_openapi.py` |
| Backend lock file | `Backend/requirements.lock` and `.github/requirements/*.lock` match their inputs | `tools/backend_lock.sh --check` (uv from `.github/requirements/lock-tools.lock`) |
| Linux agent | `Agent-Linux/tests` with the distribution's `python3` and packages; the `.deb` built twice must be identical; `dpkg -i` (service not started), file modes, `pops-agent version`, purge | `python3 -m pytest Agent-Linux/tests`, `python3 Agent-Linux/build_deb.py --out /tmp/deb` ([`Agent-Linux/README.md`](Agent-Linux/README.md#building-and-testing)) |
| Linux agent against the backend | the agent from the source tree enrolls with a backend over TLS (school CA from `pops-tls`), runs a command, has its result acknowledged, and is refused a switched-off command (`Agent-Linux/tests/test_integration.py`) | see the job in `ci.yml` (`POPS_IT_URL`, `POPS_IT_CA`, `POPS_IT_ADMIN_PASS`) |
| Dashboard checks | `php -l` on every PHP file; dark mode stays removed; `check_html_sinks.py`; `check_i18n.py`; JavaScript unit tests | `find Dashboard -name '*.php' -print0 \| xargs -0 -n1 php -l`, `python3 tools/html_sinks/check_html_sinks.py`, `python3 tools/i18n/check_i18n.py`, `node --test tests/unit-js/*.test.mjs` |
| Panel end-to-end | Playwright (Chromium) on the real panel and backend: every page at 1440 and 390 px, main flows, no console errors or outside requests | `cd tests/e2e && npm ci && npx playwright test` with an empty `DB_NAME` ([docs/testing.md](docs/testing.md#panel-end-to-end-tests)) |
| Version consistency | Actions pinned to commit SHAs; `VERSION` == top CHANGELOG release heading == built `<Version>`; docs state the current versions (`check_docs_versions.py`); the nullable baseline only shrinks (`check_nullable_baseline.py`) | `python3 tools/check_docs_versions.py`, `python3 tools/check_nullable_baseline.py` |
| Migrations (PostgreSQL 13) | fresh `migrate.py`, `ci_schema_check.py`, second run applies nothing | see below |
| Several backend workers (Redis) | `Backend/tests/test_ha.py`: two workers on one database and one Redis, then Redis cut off ([docs/ha.md](docs/ha.md)) | `POPS_TEST_REDIS=redis://127.0.0.1:6379/0 python -m pytest -v Backend/tests/test_ha.py` with the `DB_*` variables of an empty database |
| Backup and restore (PostgreSQL 16) | `pops-backup` with test-restore, a tampered backup and a tampered audit chain refused, `pops-restore --db-only` | see the job in `ci.yml` |
| TLS tool and nginx template | `pops-tls init` / `show` / `renew`, chain and name checks; the nginx template renders and passes `nginx -t` | see the job in `ci.yml` |
| Server scripts | shellcheck; `Installer/server/tests/test_deploy.sh` (deploy, rollback, pre-migration dump, venv rebuild, signed tags) with fakes, without root | `bash Installer/server/tests/test_deploy.sh` |
| Release signing tool | `tools/sign_release.py selftest` (temporary key, no secret needed) | same command |
| Fuzzing (Atheris) | each target in `fuzz/` (agent WebSocket messages, request models, release manifest, notification settings) for 60 s; not on release tags | `fuzz/run.sh` with Python 3.12 ([docs/fuzzing.md](docs/fuzzing.md)) |
| Security invariants (integration) | `python -m pytest -m "not integration"` and then the integration scripts against a running backend under coverage, with a coverage floor | `Backend/tests/run_local.sh` (see below) |

### Lint and formatting

- `flake8 Backend/ tools/ assets/readme/` must report nothing. The configuration is the repository-root `.flake8`: line length 120,
  E203 ignored.
- Formatting follows black with `-l 120 -S` (line length 120, quotes left as written). CI runs flake8 only, not
  black.

### Backend tests (pytest)

The runner is [pytest](https://docs.pytest.org/), from the hash-locked `.github/requirements/pytest.lock` (see
[Local setup](#backend-python-310-postgresql)). The configuration is the repository-root `pytest.ini`, and
`Backend/tests/conftest.py` explains how the files are collected.

```bash
python -m pytest -m "not integration"                    # no database or server: test_units.py, test_protocol.py
python -m pytest Backend/tests/test_units.py -k bypass   # one test
python -m pytest --junitxml=report.xml                   # JUnit output for an IDE or CI
```

There are two kinds of test file in `Backend/tests/`:

- **pytest tests** (`test_units.py`): every `test_*` function is a separate test. Write new tests this way: plain
  functions with `assert`, and `import pytest` when you need its helpers.
- **Script tests** (all the others, written before the switch): a file that runs itself, either with
  `if __name__ == "__main__":` or with `asyncio.run(main())` at module level, prints its checks and exits non-zero on
  failure. pytest never imports these; it runs each one as **one test** in its own process, and a failure shows the
  checks that failed (`FAIL` / `✘` lines) with the full output. A new script test needs no registration: drop the file
  in `Backend/tests/` and it is collected.

A script that reads `POPS_TEST_HTTP` needs a **running backend** and gets the `integration` marker; without
`POPS_TEST_HTTP` it is skipped. These tests also write to **their database directly**: they create users and
enrollment tokens and switch `enforce_agent_auth`. Run them only against an **empty, throwaway database**, never
against a real server. They share one server and database, so they run one after another in a fixed order
(`SCRIPT_ORDER` in `conftest.py`, the same order CI always used); new scripts run after the listed ones, by name.

`Backend/tests/run_local.sh` does what the `security` job in `ci.yml` does: tests without a server, migrations, a
temporary server on port 8099, the integration tests, and the coverage report with `COVERAGE=1`:

```bash
createdb pops_test                          # empty database your role owns
export DB_HOST=127.0.0.1 DB_PORT=5432 DB_USER=<user> DB_PASS=<password> DB_NAME=pops_test JWT_SECRET=dev
COVERAGE=1 bash Backend/tests/run_local.sh  # COVERAGE=1 needs coverage==7.10.7; omit it to skip the report
```

By hand, against a server you started yourself on 8099:

```bash
export POPS_TEST_HTTP=http://127.0.0.1:8099 CORS_ALLOWED_ORIGINS= NOTIFY_WEBHOOK_ALLOW_PRIVATE=1 GLPI_ALLOW_PRIVATE=1
python -m pytest -v -m integration                       # all of them, in order
python -m pytest -v Backend/tests/test_security.py       # one script
```

- Export every `DB_*` variable and `JWT_SECRET` yourself. A value missing from the environment is read from `.env`,
  which may point at another database. The server and the tests must use the same `JWT_SECRET`, because the tests
  create their own tokens.
- `NOTIFY_WEBHOOK_ALLOW_PRIVATE=1` lets `test_features.py` send webhooks to its own receiver on `127.0.0.1`, and
  `GLPI_ALLOW_PRIVATE=1` lets `test_glpi.py` reach its fake GLPI there. `run_local.sh` sets them, and the other
  variables the scripts expect (`METRICS_TOKEN`, `POPS_DEMO_USERS`, `PEER_CACHE_SEED_TIMEOUT_SECONDS`,
  `POPS_SSO_ALLOW_INSECURE_FOR_TESTS`). `test_sso.py` starts OpenLDAP with Docker and skips that part without it.

### Migration check

With the same `DB_*` variables pointing at an empty database:

```bash
python Backend/migrate.py
python .github/scripts/ci_schema_check.py
python Backend/migrate.py                    # must apply nothing
```

## Rules

### Enforced by CI

- **flake8 clean** and importable on Python 3.10 and 3.12.
- **PHP syntax:** every file under `Dashboard/` passes `php -l`.
- **Dark mode must not come back.** The panel has one light theme since 0.1.2-alpha. Any `data-theme` or
  `toggleTheme` under `Dashboard/` fails the build.
- **Translations:** every `Dashboard/lang/*/*.json` parses, uses the same placeholders as its Turkish key and only
  has keys that exist in the code (`tools/i18n/check_i18n.py`, see [`docs/i18n.md`](docs/i18n.md)).
- **Version consistency:** `VERSION`, the top release heading in `CHANGELOG.md` (the `Unreleased` heading is
  skipped) and the `<Version>` that `Agent/Directory.Build.props` produces must be equal. `VERSION` changes only
  in a release commit; between releases, changes collect under `## [Unreleased]`.
- **Migrations** build an empty database, pass the schema check and are idempotent.
- **Agent** projects build without warnings (warnings are errors outside the `Agent/.editorconfig` baseline), only
  the files in `tools/nullable_baseline.txt` turn nullable off (`tools/check_nullable_baseline.py`) and the unit
  tests pass.

### Enforced by review

- **Migrations.** Add a new file `Backend/migrations/NNNN_<name>.sql` with the next free number. Keep it plain,
  idempotent DDL (`CREATE TABLE IF NOT EXISTS`, `ADD COLUMN IF NOT EXISTS`) and additive: a code rollback does not
  undo a migration. Never edit, rename or renumber a migration once it is on `main`; servers that self-update from
  `main` may already have applied it. Never change the schema from Python code. For a new table, add a
  `(table, column)` pair to `.github/scripts/ci_schema_check.py` and a row to the migration table in
  [`docs/database.md`](docs/database.md). A migration that changes or deletes data must tell operators in the
  CHANGELOG to take a `pg_dump` first.
- **Changelog.** Every user-visible change gets an entry under `## [Unreleased]` in the same pull request, in the
  [Keep a Changelog](https://keepachangelog.com/en/1.0.0/) sections (`Added`, `Changed`, `Removed`, `Security`,
  `Fixed`, and `Known issues` when needed). Follow the existing style: start with the component in bold
  (`**Backend:**`, `**Dashboard:**`, `**Agent:**`, `**Backend/Dashboard:**`, `**Docs:**`), write in plain English
  what changed for the user and why, and name new migrations, settings and endpoints and the agent version a
  change needs.
- **Commit messages** follow the pattern in `git log`: `type(scope): summary`. Types: `feat`, `fix`, `docs`,
  `refactor`, `test`, `ci`, `chore`, `perf`, `style`. Common scopes: `backend`, `agent`, `dashboard`, `system`,
  `security`, `installer`, `deploy`, `release`, `docker`, `notify`, `auth`, `deps`. The summary is imperative,
  lower case, without a final period, and describes the effect, for example
  `fix(notify): block webhooks to internal addresses (SSRF), pin the checked IP, do not follow redirects`. The body
  explains why and how it was verified, and names pentest findings or issues (`F1`, `#22`). Release commits are
  `chore(release): <version>`.
- **No secrets in the repository.** `.env`, `Dashboard/includes/config.php` and `*.key.pem` are git-ignored. Do not
  commit passwords, tokens, keys, real server addresses or data from real devices; examples use
  `pops.example.com`. Server secrets come from `.env`, agent secrets live only in `C:\POpsData\secure`. A secret
  that was ever committed must be rotated: deleting it from the tree leaves it in the history.
- **Dependencies.** Pin exact versions in `Backend/requirements.txt` and prefer the standard library, since servers
  may have to install offline. After every change run `tools/backend_lock.sh` and commit `Backend/requirements.lock`
  with it (hash-locked; see [`docs/backend.md`](docs/backend.md#dependencies)). Dependabot proposes updates; the
  `lock-refresh` workflow adds the lock to its PRs. The agent stays on .NET 10 (LTS) packages; Dependabot
  ignores major updates (11.x) until the next LTS.
- **Agent protocol.** A new or changed WebSocket message starts in [`docs/protocol/`](docs/protocol/README.md):
  schema and example first, then the code; `Backend/tests/test_protocol.py` fails on anything undocumented.
- **Documentation** changes with the code: endpoints in [`docs/api.md`](docs/api.md), settings in
  [`docs/configuration.md`](docs/configuration.md) and `.env.example`, tables in `docs/database.md`, security
  behaviour in [`docs/security.md`](docs/security.md) and [`SECURITY.md`](SECURITY.md). A new or changed design
  decision gets an entry in [`docs/decisions.md`](docs/decisions.md).
- **Design rules** ([`docs/decisions.md`](docs/decisions.md)): nothing hidden from the person at the PC (no stealth
  mode, no keystroke logging, remote sessions ask or announce); new agent HTTP endpoints require a valid device
  secret even while enforcement is off, as the software and Windows Update endpoints do; notifications come only
  from events the server decides on, never from severity an agent reports; outbound calls from the server are
  optional, time-limited and never fatal, so an offline server keeps working.

## Security-sensitive areas

Changes in these areas need a careful second review, a test in `Backend/tests/` or `Agent/POps.Tests` where
possible, and a `### Security` CHANGELOG entry when behaviour changes. The threat model is in
[`SECURITY.md`](SECURITY.md), the controls in [`docs/security.md`](docs/security.md).

| Area | Code | What must keep holding |
| --- | --- | --- |
| Panel sign-in and sessions | `Backend/pops/security.py`, `pops/routers/auth.py`, `Dashboard/includes/session.php`, `header.php` | bcrypt only; JWT only in the httpOnly cookie or a Bearer header; `X-Requested-With` on cookie-authenticated writes; role and `token_version` re-read from the database on each request; a 2FA challenge is never accepted as a session; login and 2FA rate limits. |
| Agent authentication | `pops/agent_auth.py`, `pops/routers/agents.py`, `pops/dna.py`; `AgentCredentials.cs`, `SecureStore.cs` | Secrets stored only as SHA-256 and compared in constant time; no re-enrollment of an enrolled device without `allow-reenroll`; `X-Agent-Id` bound to the target device; rejections audited; no credentials over a non-loopback `http://` URL. |
| Signed updates and release keys | `tools/sign_release.py`, `Backend/release_verify.py`, `pops/routers/system/` (`releases.py`: upload, fetch; `updates.py`: deploy), `ReleaseVerifier.cs`, `AgentUpdate.cs`, `Agent/POpsUpdater`, `Installer/agent`, `keys/`, `release.yml` | Verify the signature and SHA-256 before anything changes; no downgrade; the manifest only from the agent's own server, the package only from that server or a lab peer it lists (`PeerCache.cs`, `PeerDownload.cs`), never trusted before the signed size and SHA-256 match; the private key never in the repository, on a server or on an agent; an update or rollback never leaves a PC without an agent. |
| Capability policy | `AgentCapabilities.cs`, `Worker.cs`, `Installer/agent/CustomActions`, `pops/routers/system/identity.py` (`set-capabilities`) | The server can only disable; an "enable" from the server is ignored; an unreadable policy file means disabled; an MSI update without the properties keeps the current state. |
| Remote control and Vision | `pops/routers/control.py`, `pops/manager.py`, `Worker.cs`, `PipeClientVerifier.cs`, `Agent/POpsTray` | Input only for an admin with an open, reasoned session; frames only to panels that hold the session; the agent applies input only in a session the tray started after consent or notice; the pipe accepts only the installed `POpsTray.exe`. |
| Audit hash chain | `pops/audit.py`, migration `0004`, `audit-verify` in `pops/routers/control.py` | Only server code writes `device_audit_logs`; security actions are recorded with the acting user; the chain is written under its lock; never secrets or keystrokes in any log. |
| Notifications and webhooks | `pops/notify.py`, `pops/routers/notifications.py` | Only server-decided events notify; webhook addresses must be public when saved and when sent; the checked address is pinned; redirects are not followed; SMTP credentials only in `.env`. |
| CSV export | `pops/routers/reports.py` | Cells starting with `=`, `+`, `-`, `@`, a tab or a carriage return are prefixed with `'`. |
| Server self-update and deploy | `Installer/server/pops-selfupdate*`, `pops-deploy-backend`, `pops/routers/system/selfupdate.py` | The backend never runs as root and only writes the request file; its content is never executed; only `origin/main` is deployed; health check with exact rollback. |
| Uploads and downloads | `pops/routers/tasks.py` | `secure_filename` and a fixed storage folder; `/download/` is unauthenticated, so it must never serve anything confidential. |
| Quarantine and offline bypass | `NetworkIsolation.cs`, `OfflineBypass.cs` | The previous firewall state is restored on unlock; bypass codes keep the lockout and the constant-time comparison. |

Agent files named without a path are in `Agent/POps.Agent/POps.Agent/`.

## Pull requests

- Fork the repository (or use a branch if you have write access) and branch from `main`. Keep one topic per pull
  request, and keep refactoring separate from behaviour changes.
- Fill in the [pull request template](.github/pull_request_template.md) and say how you tested: which tests, and
  for agent changes which Windows version. For changes to the updater or the MSI, say whether the rollback drill
  was run.
- All CI jobs and CodeQL must pass.
- [`.github/CODEOWNERS`](.github/CODEOWNERS) requests a review from the maintainer (`@TheP4SHA`) for every path.

## Release process

For maintainers. The mechanics are in `.github/workflows/release.yml`; see also
[`docs/deployment.md`](docs/deployment.md#releases).

1. `main` is green and `## [Unreleased]` in `CHANGELOG.md` is complete.
2. Make one commit `chore(release): X.Y.Z-alpha` that:
   - sets `VERSION` to `X.Y.Z-alpha` (it must start with a numeric `a.b.c`, which becomes the MSI version together
     with the CI run number);
   - adds `## [X.Y.Z-alpha] - YYYY-MM-DD` under the now empty `## [Unreleased]`, with a short summary and upgrade
     notes above the moved entries, and updates the compare links at the end of the file.

   `Agent/Directory.Build.props` needs no change. Push and wait for the `version` job.
3. Tag with the release SSH key and push: `git tag -s vX.Y.Z-alpha -m vX.Y.Z-alpha && git push origin vX.Y.Z-alpha`.
   Servers with `/etc/pops/allowed_signers` do not self-update to an unsigned tag
   ([`docs/self-update.md`](docs/self-update.md#sürüm-etiketlerinin-imzası)).
4. `release.yml` builds the agent zip and MSI (Windows) and the server tarball. The `release` job runs in the
   GitHub environment `release`: it checks that the tag is `v<VERSION>` and that the CHANGELOG has that section,
   signs `manifest.json` with `POPS_RELEASE_PRIVATE_KEY`, verifies it against
   `keys/pops_release_ed25519.pub.pem`, writes `SHA256SUMS` and publishes the release with the CHANGELOG section as
   notes. A version containing `-` is published as a pre-release. Keep the key as a secret of the `release`
   environment with required reviewers, so a signed release cannot be published without a manual approval.
   Changing `release.yml` on `main`, or starting it by hand on a branch, builds the packages without publishing.
5. Roll out. **Server:** self-update from the panel or `pops-deploy-backend` (take a `pg_dump` first when a
   migration changes data). **Agents:** on **Sistem**, download the release from GitHub (or upload it on an
   offline server), dispatch it to one pilot PC or lab, check the update results, then dispatch it to the rest.
6. Rollback drill: when a release changes `POpsUpdater` or what it depends on (such as `POps.Shared`), ship it
   normally first, then run the drill with the next release, because the updater that runs is always the
   installed one. Steps: [`Agent/README.md`](Agent/README.md#rollback-drill).
7. Key generation, storage and rotation: [`keys/README.md`](keys/README.md).

## Reporting security issues

Do not open a public issue. Email **security@pashacore.com.tr** with a description, steps to reproduce and the
impact, as described in [`SECURITY.md`](SECURITY.md).
