# Troubleshooting

Start with the logs (next section), then find the symptom below. Log messages are quoted as the code writes
them, most of them in Turkish.

## Where the logs are

### Server

| Log | How to read it |
| --- | --- |
| Backend | `journalctl -u pops -n 100 --no-pager` (`pops` is the unit `install.sh` creates; use your unit name otherwise) |
| Web server / PHP | the nginx or Apache error log |
| Self-update | `/var/lib/pops/deploy.log`, `/var/lib/pops/deploy-status.json` |
| Deploy script | its console output; code backups in `<backend dir>/.deploy-backups/` |
| Agent events | panel **Log & Envanter**, or the `agent_logs_v2` table |
| Security audit | `device_audit_logs` table; chain check with `GET /api/system/audit-verify` |

### Windows PC

| Log | Contents |
| --- | --- |
| `C:\POpsLogs\POps_<yyyyMMdd>.log` | Service and updater. Readable by administrators only. |
| `C:\POpsLogs\msi-install.log` | MSI log, if you installed with `/l*v C:\POpsLogs\msi-install.log` as in the docs. |
| `C:\POpsLogs\deploy_trace.txt` | Progress of packages installed from the Deployment page. |
| `%LOCALAPPDATA%\POps\Logs\` | Per user: `TrayLog.txt` (tray, message types only) and `POpsWatchdog_<yyyyMMdd>.log`. |
| `C:\POpsData\update-result.json` | Result of the last update, until the agent has reported it (then renamed to `update-result.reported.json`). |

Useful checks in an elevated command prompt:

```
sc query POpsAgent
sc qc POpsAgent
type C:\POpsData\identity.key
icacls C:\POpsData\secure
```

On each connection attempt the service logs `[POps V4] DUAL-SOCKET MİMARİSİ BAŞLATILDI (<version>, kimlik: …)`,
where `kimlik` names the credentials it sent (never their values), then `[+] Ana Komut Tüneli Kuruldu.` on
success or `[!] Santralle bağlantı koptu: <reason>` on failure.

## A PC does not appear or shows Offline

Work through these in order:

1. **Is the service running?** `sc query POpsAgent`. Windows restarts it after a crash, and while someone is
   signed in the watchdog starts it when it is stopped.
2. **Is the server address valid?**
   - `[GÜVENLİK] ServerUrl şifresiz http ve yerel değil (…); … sunucuya bağlanılmıyor.` The address is plain
     `http://`. The agent only talks to `https://` servers (or `http://` on `127.0.0.1` / `localhost`). Reinstall or
     repair with `SERVER_URL=https://…`.
   - `ServerUrl tanımlı değil (…); http://127.0.0.1:8000 kullanılıyor.` No address is configured. Install with
     `SERVER_URL=`.
3. **Can the PC reach the server?** Open `https://<server>/api/health` in a browser on the PC. It should return
   `{"status": "ok", …}`. If the browser warns about the certificate, the agent will fail too: it uses the Windows
   certificate checks, so the certificate must be valid for the host name and trusted by the PC.
4. **Are WebSockets proxied?** `/ws/` must be forwarded with the WebSocket upgrade headers ([`deployment.md`](deployment.md#web-server-and-tls)).
   `Santralle bağlantı koptu` right after each start usually points at the proxy or the certificate.
5. **Rejected by the server?** `[GÜVENLİK] Sunucu ajan kimliğini reddetti (4401): …`. Agent-auth enforcement is on
   and the PC has no valid secret or token. The agent retries every 60 seconds. On the server the rejection is
   recorded as `auth_reject` in `device_audit_logs`. See the next section.

After a backend restart every device is shown Offline until its agent reconnects, which takes a few seconds.

## Enrollment and 4401 rejections

| Situation | What happens | Fix |
| --- | --- | --- |
| Token expired, used up or revoked | The token is ignored. With enforcement off the PC connects but does not enroll; with enforcement on it is rejected (`4401`). | Check **Sistem & Sürüm** → "Kayıt jetonu" (uses, expiry). Create a new token and run the MSI again with `ENROLL_TOKEN=` (other settings are kept), or put `EnrollToken` into `appsettings.json` and restart the service. |
| PC is already enrolled but lost its secret (freeze software, restored image) | The server refuses to issue a second secret: close `4401` "Cihaz zaten kayıtlı", a critical `enroll_denied` entry in the audit log. | A superadmin allows one re-enrollment for that device, then the PC connects with a valid token: `POST /api/system/allow-reenroll` with `{"pc_name": "<HW-…>", "allow": true}` (API only, no panel button). For freeze software, see `PERSIST_DIR` in [`agent.md`](agent.md#machines-with-freeze-software). |
| Device was deleted in the panel | Deleting a device also deletes its secret. With enforcement on it is rejected at its next connection. | Give it a new enrollment token. |
| Enforcement was turned on before all PCs enrolled | Every unenrolled PC is rejected. | Turn it off on **Sistem & Sürüm** (or `UPDATE global_settings SET value='0' WHERE key='enforce_agent_auth';`), enroll the remaining PCs, turn it on again. The card shows how many agents are enrolled. |

Each successful enrollment uses up one use of the token; a token created with several uses enrolls that many PCs.

## Commands and deployments

- **Task stays "Sırada" (Pending).** The PC is offline (tasks wait until it connects), the task is paused, or the
  concurrency limit is reached: at most `concurrent_limit` PCs run a task at once (**Ayarlar**), and each PC runs one
  task at a time.
- **Task stays "İşleniyor" (Running).** A command may run for up to 30 minutes before the agent stops it. If the PC
  restarts meanwhile, the task is marked `Completed (Rebooted)` when the agent reconnects.
- **Output is `[REDDEDİLDİ]`.** The terminal capability is disabled on that PC; the refusal is logged as
  `capability_denied`. It can only be re-enabled locally (MSI with `TERMINAL_ENABLED=1`).
- **A package does not install.** Read `C:\POpsLogs\deploy_trace.txt` on the PC. The PC must be able to download
  `https://<server>/download/<file>`. A ZIP package must contain an `install.bat`; an MSI is installed with
  `/qn /norestart` plus your parameters; only exit codes 0 and 3010 count as success.
- **Scripts using the API get `403 CSRF doğrulaması başarısız`.** A request authenticated by the `pops_jwt` cookie
  must send `X-Requested-With: XMLHttpRequest`. Scripts should use `Authorization: Bearer <token>` instead.

## POpsVision

- **No previews or frames.** Viewers never receive them. Check that the device is online and someone is signed in
  (the tray captures the screen). If Vision is disabled on the PC, the agent refuses and the panel receives
  `capability_denied`.
- **"Rejected" right after starting.** The user declined the consent dialog (`vision_rejected`). Use a mandatory
  session with a reason if the situation requires it.
- **Mouse and keyboard do nothing.** Input is accepted only from an admin with an open session for that device,
  and only while the session the tray started is active. A session grant expires after 30 minutes without
  activity; start a new one.

## Agent updates

| Message or state | Meaning |
| --- | --- |
| `400 Önce imzalı bir release yükleyin …` | No release is staged. Download it from GitHub or upload it first. |
| `502 GitHub'a ulaşılamadı …` / `GitHub'dan indirilemedi …` | The server has no internet access. Upload `manifest.json`, `manifest.json.sig` and the MSI instead. |
| `400 İmza doğrulanamadı: …` / `SHA-256 uyuşmuyor` | The files are not an unmodified signed release. |
| `409 Yüklenen sürüm mevcut doğrulanmış sürümden … yeni değil` | The release is not newer than the staged one. Tick "aynı ya da eski sürümü zorla" (`force`) if intended. |
| `503 Açık anahtar bulunamadı` | `keys/pops_release_ed25519.pub.pem` is missing next to the backend. |
| `409 MSI staged klasörde yok …` | The staged release has no MSI; upload the MSI together with the manifest. |
| `skipped_offline` in the reply | Those agents were offline and did not receive the update. Send it again later. |

On the PC the outcome is in `C:\POpsData\update-result.json` and the service log:

- `rolled_back`: the new version failed (installer error, or not healthy within 90 seconds) and the previous one
  is running again; `rollback` says how.
- `pending_reboot`: the install completes at the next restart.
- `install_failed`: msiexec did not start (for example another installation was running); nothing changed.
- `rejected`: the downloaded MSI did not match the signed size or SHA-256.
- `rollback_failed`: check `agent_state` and `running_version`; the updater tries to make sure the service exists
  and runs, and logs `[KRİTİK]` if it had to repair it.

The agent never installs a version that is not newer than its own. Agents older than 0.1.3-alpha cannot apply
signed updates; install the MSI on them once.

## Server

- **Backend does not start:** `RuntimeError: Ortam değişkeni tanımlı değil: …`. A required variable
  (`JWT_SECRET`, `DB_USER`, `DB_PASS`, `DB_NAME`) is missing from `.env` or the environment.
- **`Veritabanı bağlantı hatası (deneme N/5)`.** The backend cannot reach PostgreSQL. After five attempts it keeps
  running without a database and `/api/health` reports `"status": "degraded"`. Fix the `DB_*` settings or the
  database, then restart the service.
- **No admin account after the first start:** `PANEL_ADMIN_PASS tanımlı değil, '<user>' hesabı oluşturulmadı`. Set
  `PANEL_ADMIN_PASS` and restart; the account is created if it does not exist.
- **Login page: `API Sunucusuna Ulaşılamıyor!`.** PHP cannot reach the backend at `POPS_API_INTERNAL_URL`
  (default `http://localhost:8000`). Check the service and the variable.
- **Login page: PHP error about `includes/config.php`.** Create it from `Dashboard/includes/config.example.php`.
- **Pages load but show no data.** The browser's `/api/` calls do not reach the backend. Open
  `https://<server>/api/health`; it must return JSON. Proxy `/api/` and `/ws/` on the panel's own host name.
- **You are sent back to the login page.** The API answered `401`: the token expired, `JWT_SECRET` changed, or
  your account was edited or deleted. Sign in again.
- **`429` on login.** More than 10 attempts in a minute from that address; wait a minute.
- **An old account cannot log in.** Only bcrypt password hashes are accepted. A superadmin sets a new password on
  **Ayarlar**.
- **Lost authenticator (2FA):**

  ```sql
  UPDATE users SET totp_enabled = false, totp_secret = NULL WHERE username = '<user>';
  ```

- **Forgotten superadmin password:** create a bcrypt hash with the backend's Python and store it:

  ```bash
  /opt/pops/venv/bin/python -c "import bcrypt, getpass; print(bcrypt.hashpw(getpass.getpass().encode(), bcrypt.gensalt()).decode())"
  ```

  ```sql
  UPDATE users SET password_hash = '<hash>', token_version = token_version + 1 WHERE username = '<user>';
  ```

- **Self-update button shows "Kurulu değil" / `503 Self-update kurulu değil`.** The systemd path unit is not
  installed or `/var/lib/pops` is not writable by the backend user. See [`self-update.md`](self-update.md).
- **Self-update state `failed`.** Read `/var/lib/pops/deploy.log`. The deploy script already restored the previous
  code; the service keeps running the old version.
- **`pops-deploy-backend`: `Backend/ altında commit'lenmemiş ya da izlenmeyen dosya var`.** Only committed code is
  deployed; commit or remove the changes first.
- **`pops-deploy-backend`: `Sağlık kontrolü başarısız, önceki kod seti geri yükleniyor`.** The new code did not
  pass the health check and was rolled back; the last journal lines of the service are printed after that
  message.
- **`/api/system/audit-verify` returns `ok: false`.** An entry of the security audit log was changed or deleted
  after it was written; `first_broken_id` tells where. Find out who has write access to the database.
- **The panel shows no GitHub version.** The server cannot reach GitHub; the check is skipped silently. Offline
  servers use the manual release upload.

## Quarantine and offline bypass

- **Panel: `BYPASS_SECRET tanımlı değil`.** Set `BYPASS_SECRET` in the backend `.env` and restart.
- **The code is rejected on the PC.**
  - The agent has no bypass secret (log: `Offline Bypass devre dışı: BypassSecret tanımlı değil`): install with
    `BYPASS_SECRET=` equal to the server's value.
  - The secrets differ, or the server and the PC are on different dates (the code changes daily by local date).
  - After 5 wrong codes the bypass is locked for 15 minutes, doubling up to 24 hours (log:
    `[GÜVENLİK] … Offline Bypass …`).
- A valid code removes the network isolation. Use **Karantinayı Kaldır** in the panel once the PC is back online
  to clear the quarantine state and the lock screen.

## Wake-on-LAN

- `MAC adresi bulunamadı.` The device has no hardware inventory yet. The server asks for it when the agent
  connects and none is stored.
- Packets to `255.255.255.255` stay in the server's own subnet. For routed lab networks set `WOL_BROADCAST_ADDR` to
  the lab's broadcast address. For devices in a lab, an online agent in the same lab also sends the packet.
- Wake-on-LAN must be enabled in the PC's firmware and network adapter settings.

## Policies

- **DNS policy violations never appear.** DNS monitoring is not active in the current agent build.
- **Domain lists disappeared.** Saving the **Politikalar** page stores the policy without `dns_domains`, which
  clears them. Set them through `POST /api/agent_policies` ([`configuration.md`](configuration.md#agent-policy-object)).
