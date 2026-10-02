# Backup and disaster recovery

What to do so that "the server died" is an inconvenience, not a loss.

## What is backed up

`pops-backup` (root) writes one folder per run under `BACKUP_DIR` (default `/var/backups/pops/<UTC time>/`):

| File | Contents |
|---|---|
| `db.dump` | The whole PostgreSQL database (`pg_dump -Fc`): devices, agent secrets, audit chain, users, tasks, licences, tickets, notifications, settings. |
| `files.tgz` | Backend `.env` (database password, `JWT_SECRET`, SMTP…), `VERSION`, `keys/` (release **public** key), `releases/` (verified signed agent packages), `storage/` (files uploaded for distribution), `updates/` (agent MSIs served to agents; skip with `BACKUP_EXCLUDE_UPDATES=1`), the panel `.env` if `POPS_PANEL_ENV` is set, `/etc/pops`, `/var/lib/pops-state/deploy-status.json`. |
| `meta.json` | Version, host, database name, `pg_dump` version. |
| `SHA256SUMS` | Checksums of the three files; `pops-restore` refuses a folder whose checksums do not match. |

The backup contains secrets (`.env`). Folders are `700` and files `600`, owned by root; keep off-site copies
equally protected.

Not in the backup, and not needed: the code (it is in git and in the release packages) and the release **signing**
key (it never lives on the server; it is a GitHub Actions secret).

## Every backup is test-restored

A backup that cannot be restored is worse than none, because it gives false confidence. After each backup,
`pops-backup` runs `pops-restore --check`: it verifies the checksums, restores the dump into a temporary database
(`<db>_restorecheck`), runs `audit_verify.py` on it (the hash-chained audit log must be intact and the migration
history present) and drops the temporary database. Only then is the backup marked `verified`.

The result is written to `/var/lib/pops-state/backup-status.json` (a root-owned directory; up to 0.1.12 it was `/var/lib/pops`). The panel shows it on **Sistem → Yedekler**
("Son veritabanı yedeği") and warns when there is no backup, the last one failed, or it is older than two days.
`/metrics` exposes `pops_backup_last_age_seconds` and `pops_backup_last_ok`.

## Setup

`Installer/server/install.sh` installs and enables it (nightly around 02:30, 14 days kept, at least the newest 3).
On an existing server:

```bash
sudo install -m 755 Installer/server/pops-backup Installer/server/pops-restore /usr/local/sbin/
sudo install -m 644 Installer/server/pops-backup.service Installer/server/pops-backup.timer /etc/systemd/system/
sudo install -d -m 755 /etc/pops
sudo install -m 600 Installer/server/backup.conf.example /etc/pops/backup.conf
sudo systemctl daemon-reload && sudo systemctl enable --now pops-backup.timer
sudo pops-backup    # first run now
```

Edit `/etc/pops/backup.conf`: `POPS_APP` (backend folder), `POPS_SERVICE` and `POPS_SERVICE_USER` (systemd unit and
the user it runs as), `POPS_PANEL_ENV`, `KEEP_DAYS`. The temporary restore database needs the same `pg_hba.conf`
access as the real one (`install.sh` adds a line for `<db>_restorecheck`). PostgreSQL 13 or later is required
(`DROP DATABASE … WITH (FORCE)`).

**Consistency.** `pg_dump` reads the whole database in one snapshot, so devices, secrets, tasks and the audit chain
in `db.dump` always belong together, even while agents keep writing. The files are archived just after the dump:
a file uploaded or an agent release staged during those seconds may be in one and not the other, which only means
that upload has to be repeated after a restore. Nothing has to be stopped for a backup.

**Keep a copy on another machine.** A backup on the same disk dies with the disk. Set `RSYNC_TARGET`
(for example `backup@nas.school.local:/backup/pops`, with an SSH key for root and no password); each new backup
folder is copied there after it is verified. Any other off-site method that copies `BACKUP_DIR` works too.

## Restore

List backups: `ls /var/backups/pops/`. Check one without touching anything: `sudo pops-restore --check <folder>`.

### Wrong change on a working server (for example deleted devices)

```bash
sudo pops-restore --db-only /var/backups/pops/20260929T023000Z
```

It asks you to type the database name, stops the service, drops and recreates the database from the dump, verifies
the audit chain, starts the service and waits for `/api/health`. Everything written after that backup is lost.

### The server is gone (new machine)

1. Install the same POps version on the new server: clone the repository, check out the tag in the backup's
   `meta.json` (`version`), run `sudo Installer/server/install.sh` with the same `APP_DIR`, `SVC_USER`, `DB_NAME` and
   `DB_USER` as before. The service user must have the **same name** as on the old server (file ownership in the
   archive is restored by name).
2. Copy the backup folder to the new server (for example from `RSYNC_TARGET`) and run
   `sudo pops-restore <folder>`. It restores the files (the old `.env` replaces the one `install.sh` wrote), sets the
   database role's password to the one in that `.env`, recreates the database from the dump, verifies the audit
   chain and starts the service.
3. Point the server's DNS name at the new machine (agents connect to the `SERVER_URL` they were installed with) and
   set up the web server and TLS certificate as before. Agents reconnect by themselves with their existing
   secrets; nothing needs to be done on the PCs.
4. Log in to the panel and check **Sistem**: devices come back online, the **Sağlık** card says "Sağlıklı".
5. Re-enable the nightly backup if you did not reuse `/etc/pops/backup.conf`.

If the agents' server address changes (new DNS name), they must be reinstalled or re-pointed with the new
`SERVER_URL`; keep the name stable.

### What each piece restores

| Lost | Consequence without backup | With backup |
|---|---|---|
| Database | Every agent must be re-enrolled; audit history, users, tasks, licences, tickets gone. | Full restore; agents keep their secrets. |
| Backend `.env` | New `JWT_SECRET` (everyone logs in again), database password must be reset. | Restored as it was. |
| `releases/`, `updates/` | Re-fetch the release from GitHub on **Sistem**. | Restored. |
| `storage/` | Uploaded distribution files must be uploaded again. | Restored. |

## Tested in CI

The `backup-restore` job in `.github/workflows/ci.yml` migrates an empty database, writes audit entries, runs
`pops-backup` (with the test restore), tampers with an audit row and checks that a new backup is **refused**, then
restores the first backup with `pops-restore --db-only` and checks the chain is intact again.
