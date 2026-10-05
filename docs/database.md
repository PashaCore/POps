# Database

POps stores everything in one PostgreSQL database, accessed by the backend through an `asyncpg` pool
(`DB_POOL_MIN` / `DB_POOL_MAX`, default 2 / 20 connections). There is no SQLite or other storage backend. Connection settings are the
`DB_*` variables in [`configuration.md`](configuration.md).

## Migrations

The schema is owned by the migration runner `Backend/migrate.py` and the numbered SQL files in
`Backend/migrations/`:

- Files are applied in file-name order (`0001_baseline.sql`, `0002_…`), each exactly once, and recorded in the
  `schema_migrations` table (`version`, `applied_at`).
- Each file runs in its own transaction. The whole run holds a PostgreSQL advisory lock, so two runners (for
  example the deploy script and a restarting backend) cannot apply the same migration twice.
- The backend applies pending migrations **automatically at startup**, before it accepts connections.
- `0001_baseline.sql` is idempotent (`CREATE … IF NOT EXISTS`), so it is safe on a database that already has the
  tables. On an empty database the migrations build the whole schema; CI checks this on PostgreSQL 13 and checks
  that a second run applies nothing.

Run the migrations by hand from the backend directory. `migrate.py` takes `DB_*` from the environment or from
the `.env` next to it:

```bash
cd /opt/pops                                   # backend directory (install.sh default)
sudo -u pops venv/bin/python migrate.py --status   # list applied and pending migrations
sudo -u pops venv/bin/python migrate.py            # apply pending migrations
```

**Adding a schema change:** create a new file with the next free number, `Backend/migrations/NNNN_<name>.sql`. Keep it
plain DDL without parameters and make it idempotent where possible (`ADD COLUMN IF NOT EXISTS`). Never change
tables from Python code at startup and never edit a migration that has already been released.

### Migration files

| File | Adds |
| --- | --- |
| `0001_baseline.sql` | The original schema (all core tables below) and the default `concurrent_limit` = 5. |
| `0002_enroll_and_secret.sql` | `enroll_tokens` and `agent_secrets` (agent authentication). |
| `0003_enroll_multiuse.sql` | `enroll_tokens.max_uses` / `use_count` (one token for a whole lab). |
| `0004_audit_hash_chain.sql` | `device_audit_logs.prev_hash` / `entry_hash` (tamper-evident chain). |
| `0005_totp_2fa.sql` | `users.totp_secret` / `totp_enabled` (opt-in 2FA). |
| `0006_agent_capabilities.sql` | `clients.cap_*` columns (capability policy) and `clients.running_version`. |
| `0007_allow_reenroll.sql` | `clients.allow_reenroll` (one-time re-enrollment permission). |
| `0008_token_version_and_task_actor.sql` | `users.token_version` (session revocation) and `tasks.created_by`. |
| `0009_notifications_schedules_inventory.sql` | `notifications`, `scheduled_tasks`, `device_software`, `device_patch_status`. |
| `0010_licenses_helpdesk.sql` | `licenses`, `tickets`, `ticket_messages`. |
| `0011_quarantine_pending.sql` | `clients.pending_quarantine_action` / `pending_quarantine_reason` (lock or unlock requested while the device was offline or before it confirmed). |
| `0012_server_ca.sql` | `clients.cap_server_ca` (how the agent verifies the server certificate: school CA or Windows store). |
| `0013_agent_health_bypass_keys.sql` | `clients.agent_health` (heartbeat health summary) and `agent_bypass_keys` (per-device offline bypass keys). |
| `0014_hardening.sql` | Enrollment tokens stored as hashes; `tasks.exit_code` / `dispatched_at`; partial index for the task queue; agent identity enforcement on by default for new installs. |
| `0019_task_context.sql` | `tasks.title`, `source`, `reason`, `client_ip` and `batch_id` (what a task is, which panel page sent it, why, from which address, and which request it belongs to) and indexes on `batch_id` and on `target_pc`. |
| `0022_api_tokens.sql` | `api_tokens` (API tokens for automation). |
| `0023_update_progress.sql` | `pending_updates.stage`, `detail`, `attempt`, `attempt_of`, `stage_at`: the last stage the agent reported for a pending update (`update_progress`, see [`api.md`](api.md#update_progress-agent-update-stages)). |
| `0020_refused_results.sql` | Tasks the agent refused but an older server stored as `Completed` (output starting with `[REDDEDİLDİ]`, no exit code) become `Denied` with exit code `-5`. |
| `0018_modules.sql` | `module_settings` (module on/off for the organisation or a lab; `config` for module settings) and, on an installation that already has devices, `install_profile = custom`. |
| `0017_task_expiry.sql` | `tasks.expires_at`, `tasks.schedule_id`, `tasks.agent_started_at` and the pending-by-schedule index. |
| `0016_task_retry.sql` | `tasks.retry_of` (a retry opens a new task) and its index. |
| `0015_p1_reliability.sql` | `users.totp_last_step` (a 2FA code works once); `clients.last_disconnect_at` / `last_disconnect_reason`; `pending_updates` and `update_results` (agent update tracking); indexes for reports, device activity, task history and the hardware-fingerprint lookup. |

## Tables

Device-related tables are keyed by `pc_name`, which holds the device's hardware ID (`HW-…`), not its Windows
host name. The host name is a separate column. Apart from `ticket_messages` → `tickets`, there are no foreign
keys between tables; consistency (for example when a lab is renamed or a device deleted) is kept by the
application code.

### Devices and labs

| Table | Contents |
| --- | --- |
| `clients` | One row per device: host name, display name, lab, `status` (`Online` / `Offline` / last heartbeat status), `last_seen`, active window, boot count, signed-in user (`logged_user`), IP, hardware fingerprint (`dna_uuid`, `dna_bios`, `dna_disk`, `dna_mac`, `dna_ram`, `cap_ram_readable`), `is_quarantined`, capability state (`cap_terminal_enabled`, `cap_vision_enabled`, `cap_terminal_disable_requested`, `cap_vision_disable_requested`), `running_version`, `allow_reenroll`, `agent_health` (health summary from the last heartbeat, agents 0.1.12+; see `Backend/pops/agent_health.py`), `last_disconnect_at` / `last_disconnect_reason` (when and why the last connection closed). New devices land in lab `Atanmamis_Cihazlar` (unassigned). |
| `hw_inventory` | Hardware inventory per device (CPU, RAM, motherboard, GPU, OS, IP, MAC, disks, last update). |
| `agent_versions` | Agent version per device, from the `X-Agent-Version` header at connect. |
| `custom_labs` | Lab names created in the panel. |
| `lab_settings` | Per lab: main PC and seating layout (`layout_json`). |

At startup the backend marks every device `Offline`; agents that reconnect are written `Online` again.

### Tasks, schedules and packages

| Table | Contents |
| --- | --- |
| `tasks` | Command queue: `target_pc`, `target_lab`, `script_path` (the command line the agent runs), `status`, `created_at`, `output`, `created_by`. `exit_code` and `dispatched_at` (migration `0014`). Statuses used by the code: `Pending`, `Running`, `Completed`, `Failed` (non-zero exit code), `Completed (Rebooted)` (a restart command, agent restarted), `Interrupted` (the agent restarted while the command ran), `Unknown` (the connection dropped and the agent cannot resend the result), `Timed Out` (no result 35 minutes after it was sent; a late result still completes it), `Denied` (the agent refused it: terminal turned off on that PC; also set when the result itself starts with `[REDDEDİLDİ]` and has no exit code), `Expired` (a scheduled run that could not be sent before `expires_at`), `Paused`, `Cancelled`. `retry_of` (migration `0016`): a retry is a new row pointing at the task it repeats; the old row keeps its result. Migration `0017`: `expires_at` (scheduled runs only), `schedule_id` (the scheduled task that queued it; a schedule does not queue a second copy for a PC that still has one pending) and `agent_started_at` (the agent's service start time when the task was sent, used to tell a restart from a reconnect). Migration `0019`: `title` (the readable name, normally the name of the step), `source` (the panel page the request came from, for example `devices`, `labs`, `terminal`, `deploy`, `tasks` for a retry, or `schedule`), `reason` (the reason typed by the admin), `client_ip` (the address the request came from) and `batch_id` (a 16-character job ID shared by the tasks created by one request); all are empty on tasks created before the migration. |
| `packages` | Package and script definitions of the **Dağıtım** page (`id`, `name`, `type`, `meta`, `command`, `icon`, `color`). The uploaded files themselves are on disk in `Backend/storage`. |
| `scheduled_tasks` | Scheduled commands: `name`, `command`, `target_mode` (`ALL` / `LAB` / `PC`), `targets` (JSON list of labs or hardware IDs), `schedule_type` (`once` / `daily` / `weekly`), `run_at` (once), `time_of_day` (`HH:MM`, server time zone), `weekdays` (`1`–`7`, 1 = Monday), `enabled`, `next_run`, `last_run`, `last_result`, `created_by`, `created_at`. When due, the scheduler inserts normal rows into `tasks`. |

### Software and Windows updates

Filled by agents from 0.1.5-alpha on; see [`agent.md`](agent.md#software-inventory-and-windows-updates).

| Table | Contents |
| --- | --- |
| `device_software` | Installed programs per device: `pc_name`, `name`, `version`, `publisher`, `install_date`, `updated_at`. Primary key (`pc_name`, `name`, `version`). Each report from an agent replaces that device's whole list. |
| `device_patch_status` | One row per device with its last Windows Update scan: `pending_count`, `pending_security`, `pending_critical`, `reboot_required`, `last_search`, `last_install`, `updates` (JSON list: KB, title, severity, categories, security flag), `last_result`, `updated_at`. |

Deleting a device also deletes its rows in both tables.

### Licences and helpdesk

| Table | Contents |
| --- | --- |
| `licenses` | Licence definitions: `name`, `match_pattern` (text searched in installed program names, case-insensitive), `publisher` (optional filter), `seats` (`NULL` = unlimited), `license_type` (`per_device` / `site` / `subscription`), `expires_at` (date), `notes`, `created_by`, `created_at`. Usage is not stored; it is counted from `device_software` when read. |
| `tickets` | Helpdesk tickets: `source` (`agent` / `panel`), `pc_name` (optional), `reporter`, `category`, `subject`, `body`, `status` (`open` / `in_progress` / `waiting` / `resolved` / `closed`), `priority` (`low` / `normal` / `high`), `assignee`, `created_at`, `updated_at`, `resolved_at`. |
| `ticket_messages` | Thread of a ticket: `ticket_id` (foreign key, deleted with the ticket), `author`, `body`, `internal` (panel-only note; never returned to agents), `created_at`. Status, priority and assignee changes are recorded here as internal notes. |

### Panel users

| Table | Contents |
| --- | --- |
| `api_tokens` | API tokens for automation (migration `0022`): `name` (unique, revoked tokens included; actions are recorded as `token:<name>`), `token_hash` (SHA-256; the token itself is shown once and never stored), `token_prefix` (first 8 characters after `pops_`), `role` (`viewer` / `admin`, enforced by a `CHECK`), `created_by`, `created_at`, `expires_at` (`NULL` = no expiry), `last_used_at` (written at most once a minute), `revoked_at` (revoked tokens are kept). See [`api.md`](api.md#api-tokens-automation). |
| `users` | `username`, `password_hash` (bcrypt only), `role` (`superadmin` / `admin` / `viewer`), `permissions` (JSON array of dashboard page names, stored as text), `last_login`, `totp_secret` (encrypted, `v1:` prefix; see [`security.md`](security.md#two-factor-authentication)), `totp_enabled`, `totp_last_step` (time step of the last accepted code), `token_version`. |

### Agent authentication

| Table | Contents |
| --- | --- |
| `enroll_tokens` | Enrollment tokens: `token_hash` (SHA-256; since migration `0014` the token itself is not stored and is shown only once, at creation), `token_hint` (first 6 characters), `lab_name`, `note`, `created_at`, `expires_at`, `max_uses`, `use_count`, `is_used`, `used_by`, `used_at`. A use is consumed in the same transaction that stores the device secret. |
| `agent_secrets` | Per-device secret as a **SHA-256 hash** (`secret_hash`); the plaintext is never stored. Moved with the device when its identity is reconciled, deleted when the device is deleted. |
| `agent_bypass_keys` | Per-device offline bypass key (migration `0013`): `secret` (32 bytes, base64url), `fingerprint`, `issued_at`, `confirmed_at` (set when the agent acknowledges the fingerprint). The server needs the key itself (codes are generated while the device is offline), so it is stored encrypted with the same key as the 2FA secrets (`v1:` prefix, from 0.1.14; older plain values are encrypted at startup). Moved with the device and deleted with it. |

### Logs and audit

| Table | Contents |
| --- | --- |
| `agent_logs_v2` | Event log shown on the **Kayıtlar** page: `pc_name`, `actor_id`, `event_type`, `category`, `action`, `risk_level`, `reason`, `message`, `meta_data` (JSONB), `timestamp`. Written by the server and by agents (`POST /api/logs/{pc}`). |
| `device_audit_logs` | Security audit log that agents **cannot** write: enrollment, authentication rejections, identity changes, remote-control session starts, lockdown/unlock, bypass codes, SYSTEM command execution, queue flushes, update results, releases, enforcement, capability and re-enrollment changes, scheduled-task changes and runs, Windows Update scan/install requests, licence changes, agent self-quarantine and offline-bypass events, auto-enrollment and notification settings. Each row has `prev_hash` and `entry_hash` (SHA-256 chain). |
| `enterprise_audit_logs` | Remote-control (Vision) sessions: session id, admin, role, target, start/end time, reason, mandatory flag, status. |
| `notifications` | Entries under **Bildirimler** in the panel: `created_at`, `event`, `severity` (`info` / `medium` / `high` / `critical`), `pc_name`, `title`, `detail`, `channels` (where it was sent: `email`, `webhook`), `delivery_error`, `is_read`. Written only by the server. |
| `agent_logs` | Old log table from before `agent_logs_v2`. Kept, no longer written. |
| `bypass_tokens` | Created by the baseline; not used by the current code. |

The hash chain makes changes to `device_audit_logs` **detectable**, not impossible: the application's database
role can still update or delete rows. Check the chain with `GET /api/system/audit-verify` (superadmin), which
returns the first broken entry. Rows written before migration `0004` have no hash and are skipped.

### Settings

`global_settings` is a key/value table. Main keys:

| Key | Set by | Meaning |
| --- | --- | --- |
| `concurrent_limit` | **Ayarlar** / **Dağıtım** / **İşlemler** pages | How many devices may run a task at the same time (default `5`; `0` = unlimited). |
| `enforce_agent_auth` | **Sistem** page | `1` = agents without valid credentials are rejected; anything else = accept-both. |
| `agent_policies` | **Politikalar** page / API | JSON: fair-use text, DNS categories, `auto_quarantine`, `quarantine_threshold`, `dns_domains`. |
| `verified_release_version` | release upload / GitHub fetch | Version of the staged, signature-verified agent release. |
| `verified_release_manifest` | release upload / GitHub fetch | The staged release's manifest (JSON); `deploy-update` sends this release. |
| `auto_enroll_lab` | **Sınıflar** page ("Otomatik kayıt…") | JSON `{"lab": ..., "until": "YYYY-MM-DD"}`: lab for devices connecting for the first time up to that date. |
| `notify_enabled`, `notify_min_severity`, `notify_email_to`, `notify_webhook_url` | **Sistem** → Bildirimler | Whether and where notifications are sent out. |
| `license_check_date` | scheduler | Date (`YYYY-MM-DD`, server date) of the last daily licence check, so the check and its notifications run once a day. |

See [`configuration.md`](configuration.md#runtime-settings-database) for how to change them.

### Timestamps

Most timestamp columns of the older tables are `TEXT` in the form `YYYY-MM-DD HH:MM:SS`, in the server's local
time. The newer tables (`enroll_tokens`, `agent_secrets`, `schema_migrations` and the tables of migrations
`0009` and `0010`) use `TIMESTAMPTZ` (`licenses.expires_at` is a `DATE`).

## Useful queries

Run these with `psql` as the POps database role.

```sql
-- Which migrations are applied
SELECT version, applied_at FROM schema_migrations ORDER BY version;

-- Devices that have (not) enrolled
SELECT c.pc_name, c.hostname, c.status, (s.pc_name IS NOT NULL) AS enrolled
FROM clients c LEFT JOIN agent_secrets s ON s.pc_name = c.pc_name ORDER BY c.hostname;

-- Current enforcement state
SELECT value FROM global_settings WHERE key = 'enforce_agent_auth';

-- Last security audit entries
SELECT id, hw_id, action, reason, "timestamp" FROM device_audit_logs ORDER BY id DESC LIMIT 20;

-- Recover an account whose authenticator was lost (turns 2FA off for that user)
UPDATE users SET totp_enabled = false, totp_secret = NULL WHERE username = '<user>';
```

## Backups

Everything the panel shows is in the database. `pops-backup` (nightly, installed by `install.sh`) dumps it together
with the backend `.env`, uploaded files and staged releases, test-restores every backup into a temporary database
and verifies the audit chain; `pops-restore` brings a backup back. Setup, off-site copies and the recovery runbook:
[`backup.md`](backup.md).

The backend deploy script backs up **code** before each deploy; it does not back up the database. Run
`sudo pops-backup` before applying a release whose migrations change existing data.
