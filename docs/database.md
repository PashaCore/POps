# Database

POps stores everything in one PostgreSQL database, accessed by the backend through an `asyncpg` pool
(`min_size=5`, `max_size=100`). There is no SQLite or other storage backend. Connection settings are the
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

### Core schema migrations

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

## Tables

Device-related tables are keyed by `pc_name`, which holds the device's hardware ID (`HW-…`), not its Windows
host name. The host name is a separate column. There are no foreign keys between tables; consistency (for
example when a lab is renamed or a device deleted) is kept by the application code.

### Devices and labs

| Table | Contents |
| --- | --- |
| `clients` | One row per device: host name, display name, lab, `status` (`Online` / `Offline` / last heartbeat status), `last_seen`, active window, boot count, signed-in user (`logged_user`), IP, hardware fingerprint (`dna_uuid`, `dna_bios`, `dna_disk`, `dna_mac`, `dna_ram`, `cap_ram_readable`), `is_quarantined`, capability state (`cap_terminal_enabled`, `cap_vision_enabled`, `cap_terminal_disable_requested`, `cap_vision_disable_requested`), `running_version`, `allow_reenroll`. New devices land in lab `Atanmamis_Cihazlar` (unassigned). |
| `hw_inventory` | Hardware inventory per device (CPU, RAM, motherboard, GPU, OS, IP, MAC, disks, last update). |
| `agent_versions` | Agent version per device, from the `X-Agent-Version` header at connect. |
| `custom_labs` | Lab names created in the panel. |
| `lab_settings` | Per lab: main PC and seating layout (`layout_json`). |

At startup the backend marks every device `Offline`; agents that reconnect are written `Online` again.

### Tasks and packages

| Table | Contents |
| --- | --- |
| `tasks` | Command queue: `target_pc`, `target_lab`, `script_path` (the command line the agent runs), `status`, `created_at`, `output`, `created_by`. Statuses used by the code: `Pending`, `Running`, `Completed`, `Completed (Rebooted)`, `Paused`, `Cancelled`. |
| `packages` | Package and script definitions of the Deployment page (`id`, `name`, `type`, `meta`, `command`, `icon`, `color`). The uploaded files themselves are on disk in `Backend/storage`. |

### Panel users

| Table | Contents |
| --- | --- |
| `users` | `username`, `password_hash` (bcrypt only), `role` (`superadmin` / `admin` / `viewer`), `permissions` (JSON array of dashboard page names, stored as text), `last_login`, `totp_secret`, `totp_enabled`, `token_version`. |

### Agent authentication

| Table | Contents |
| --- | --- |
| `enroll_tokens` | Enrollment tokens: `token`, `lab_name`, `note`, `created_at`, `expires_at`, `max_uses`, `use_count`, `is_used`, `used_by`, `used_at`. |
| `agent_secrets` | Per-device secret as a **SHA-256 hash** (`secret_hash`); the plaintext is never stored. Moved with the device when its identity is reconciled, deleted when the device is deleted. |

### Logs and audit

| Table | Contents |
| --- | --- |
| `agent_logs_v2` | Event log shown on the Log pages: `pc_name`, `actor_id`, `event_type`, `category`, `action`, `risk_level`, `reason`, `message`, `meta_data` (JSONB), `timestamp`. Written by the server and by agents (`POST /api/logs/{pc}`). |
| `device_audit_logs` | Security audit log that agents **cannot** write: enrollment, authentication rejections, remote-control session starts, lockdown/unlock, bypass codes, SYSTEM command execution, update results, releases, enforcement and capability changes. Each row has `prev_hash` and `entry_hash` (SHA-256 chain). |
| `enterprise_audit_logs` | Remote-control (Vision) sessions: session id, admin, role, target, start/end time, reason, mandatory flag, status. |
| `agent_logs` | Old log table from before `agent_logs_v2`. Kept, no longer written. |
| `bypass_tokens` | Created by the baseline; not used by the current code. |

The hash chain makes changes to `device_audit_logs` **detectable**, not impossible: the application's database
role can still update or delete rows. Check the chain with `GET /api/system/audit-verify` (superadmin), which
returns the first broken entry. Rows written before migration `0004` have no hash and are skipped.

### Settings

`global_settings` is a key/value table. Main keys:

| Key | Set by | Meaning |
| --- | --- | --- |
| `concurrent_limit` | **Ayarlar** page | How many devices may run a task at the same time (default `5`; `0` = unlimited). |
| `enforce_agent_auth` | **Sistem & Sürüm** page | `1` = agents without valid credentials are rejected; anything else = accept-both. |
| `agent_policies` | **Politikalar** page / API | JSON: fair-use text, DNS categories, `auto_quarantine`, `quarantine_threshold`, `dns_domains`. |
| `verified_release_version` | release upload / GitHub fetch | Version of the staged, signature-verified agent release. |
| `verified_release_manifest` | release upload / GitHub fetch | The staged release's manifest (JSON); `deploy-update` sends this release. |
| `auto_enroll_lab` | Labs page ("Oto-Kayıt") | Stored but not read by the backend. |

See [`configuration.md`](configuration.md#runtime-settings-database) for how to change them.

### Timestamps

Most timestamp columns are `TEXT` in the form `YYYY-MM-DD HH:MM:SS`, in the server's local time. The newer
tables (`enroll_tokens`, `agent_secrets`, `schema_migrations`) use `TIMESTAMPTZ`.

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

Everything the panel shows is in the database, so back it up regularly with the standard PostgreSQL tools, for
example `pg_dump -Fc <DB_NAME> > pops-$(date +%F).dump` as a role that can read the database, and restore with
`pg_restore`. Also keep:

- the backend `.env` (JWT and bypass secrets, database password),
- `Backend/storage/` (files uploaded on the Deployment page).

`Backend/releases/` and `Backend/updates/` can be recreated by staging the signed release again. The backend
deploy script backs up **code** before each deploy; it does not back up the database. Take a database dump before
applying a release whose migrations change existing data.
