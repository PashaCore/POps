# Troubleshooting

Start with the logs (next section), then find the symptom below. Log messages are quoted as the code writes
them, most of them in Turkish.

## Where the logs are

### Server

| Log | How to read it |
| --- | --- |
| Backend | `journalctl -u pops -n 100 --no-pager` (`pops` is the unit `install.sh` creates; use your unit name otherwise) |
| Web server / PHP | the nginx or Apache error log |
| Self-update | `/var/lib/pops-state/deploy.log`, `/var/lib/pops-state/deploy-status.json` |
| Deploy script | its console output; code backups and venv snapshots in `<backend dir>/.deploy-backups/`; settings in `/etc/pops/deploy.conf` |
| Agent events | panel **Kayıtlar**, or the `agent_logs_v2` table |
| Security audit | `device_audit_logs` table; chain check with `GET /api/system/audit-verify` |

### Windows PC

| Log | Contents |
| --- | --- |
| `C:\POpsLogs\POps_<yyyyMMdd>.log` | Service and updater. Readable by administrators only. |
| `C:\POpsLogs\msi-install.log` | MSI log, if you installed with `/l*v C:\POpsLogs\msi-install.log` as in the docs. |
| `C:\POpsLogs\deploy_trace.txt` | Progress of packages installed from the **Dağıtım** page. |
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
| Token expired, used up or revoked | The token is ignored. With enforcement off the PC connects but does not enroll; with enforcement on it is rejected (`4401`). | Check **Sistem** → "Kayıt jetonları" (uses, expiry). Create a new token and run the MSI again with `ENROLL_TOKEN=` (other settings are kept), or put `EnrollToken` into `appsettings.json` and restart the service. |
| PC is already enrolled but lost its secret (freeze software, restored image) | The server refuses to issue a second secret: close `4401` "Cihaz zaten kayıtlı", a critical `enroll_denied` entry in the audit log. | A superadmin allows one re-enrollment for that device, then the PC connects with a valid token: `POST /api/system/allow-reenroll` with `{"pc_name": "<HW-…>", "allow": true}` (API only, no panel button). For freeze software, see `PERSIST_DIR` in [`agent.md`](agent.md#machines-with-freeze-software). |
| Device was deleted in the panel | Deleting a device also deletes its secret. With enforcement on it is rejected at its next connection. | Give it a new enrollment token. |
| Enforcement was turned on before all PCs enrolled | Every unenrolled PC is rejected. | Turn it off on **Sistem** (or `UPDATE global_settings SET value='0' WHERE key='enforce_agent_auth';`), enroll the remaining PCs, turn it on again. The card shows how many agents are enrolled. |

Each successful enrollment uses up one use of the token; a token created with several uses enrolls that many PCs.

**A new PC did not land in the lab of the Otomatik kayıt rule.** The rule applies only to devices connecting for the
first time on or before its end date (server date). A device the server already knows keeps its lab, and an
enrollment token created for a lab takes precedence.

## Commands and deployments

- **Task stays "Sırada" (Pending).** The PC is offline (tasks wait until it connects), the task is paused, or the
  concurrency limit is reached: at most `concurrent_limit` PCs run a task at once (**Ayarlar**,
  **Dağıtım** or **İşlemler**), and each PC runs one task at a time.
- **Task stays "Çalışıyor" (Running).** A command may run for up to 30 minutes before the agent stops it. If the PC
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
- **Self-update state `failed`.** Read `/var/lib/pops-state/deploy.log`. The deploy script already restored the previous
  code; the service keeps running the old version. `etiket imzasi dogrulanamadi`: the newest release tag is not
  signed by a key in `/etc/pops/allowed_signers`, so nothing was merged or deployed
  ([`self-update.md`](self-update.md#sürüm-etiketlerinin-imzası)). `ayar dosyasi guvenli degil`: fix the owner and
  mode of the named file (root, not writable by group or others).
- **`pops-deploy-backend`: `Backend/ altında commit'lenmemiş ya da izlenmeyen dosya var`.** Only committed code is
  deployed; commit or remove the changes first.
- **`pops-deploy-backend`: `venv'de <OWNER> kullanıcısına ait olmayan N dosya var`.** Some packages in the backend's
  virtual environment were installed by another user (usually root), so `pip` running as the service user cannot
  remove their old versions. Nothing was changed. Run the `chown -R` command from the message as root and update
  again. Do not run `pip` in the backend's venv as root.
- **`pops-deploy-backend`: `Sağlık kontrolü başarısız, önceki kod seti geri yükleniyor`.** The new code did not
  pass the health check and was rolled back; the last journal lines of the service are printed after that
  message.
- **`/api/system/audit-verify` returns `ok: false`.** An entry of the security audit log was changed or deleted
  after it was written; `first_broken_id` tells where. Find out who has write access to the database.
- **The panel shows no GitHub version.** The server cannot reach GitHub; the check is skipped silently. Offline
  servers use the manual release upload.
- **No release notes on Sistem.** They are read from `CHANGELOG.md` on GitHub (`raw.githubusercontent.com`)
  and are hidden when the server cannot reach it. The list of what an update brings also needs the installed
  commit, which is known only after a successful panel self-update; until then the card shows the notes of the
  running version (and the unreleased entries) as they are on GitHub `main`.

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

- **DNS policy violations never appear.**
  - Agents up to 0.1.4-alpha do not start their DNS monitoring, so they never report violations.
  - A category only matches the domains listed for it on **Politikalar** (and their subdomains). An empty list
    matches nothing, and a category that is not ticked is not checked.

## Notifications

- **Nothing appears under Bildirimler.** **Bildirimler** is shown to admins and superadmins only. Only events the server
  decides on create notifications ([`security.md`](security.md#notifications)); the risk level of ordinary log
  entries never does. An identical notification (same event, device and title) is recorded at most once per
  10 minutes.
- **No e-mail or webhook message.**
  - **Sistem** → **Bildirimler**: **Dışarıya gönder** must be on, and the event's severity must be at least
    **En az önem** (default: high). Everything is still shown under **Bildirimler**.
  - E-mail also needs `SMTP_HOST` and a sender (`SMTP_FROM` or `SMTP_USER`) in the backend `.env`, then a restart
    of the backend. The card shows whether SMTP is configured.
  - Use **Test gönder**: it sends with the values in the form and shows the error text, for example a refused
    login or an unreachable host.
  - A failed delivery is marked "gönderilemedi" under **Bildirimler**; the error text is in the `delivery_error` field of
    `GET /api/notifications` and in the `notifications` table.
  - At most 30 notifications are sent out per 10 minutes; further ones are only shown under **Bildirimler**.
- **Saving or testing the webhook fails with `Webhook adresi kabul edilmedi: adres iç ağa ya da yerel bir adrese
  çıkıyor (…)`.** The webhook host resolves to a loopback, private, link-local or otherwise non-public address,
  which is refused by default. For a receiver inside the school network set `NOTIFY_WEBHOOK_ALLOW_PRIVATE=1` in the
  backend `.env` and restart the backend. `adres çözülemedi` means the server cannot resolve the host name.
- **The webhook test reports `HTTP 3xx (yönlendirme izlenmez)`.** The receiver answered with a redirect, which is
  not followed. Use the final URL (for example with `https://` instead of `http://`).

## Scheduled tasks

- **A scheduled task did not run.**
  - It is paused (**Durdur**), or it is a one-time task that has already run; the list shows "Durduruldu".
  - If the backend was not running at the scheduled time, the missed run is queued once when it starts again.
  - Times are in the **server's** time zone. The form shows the server's current time; compare it with yours.
  - The scheduler checks every 30 seconds, so a run can start up to about half a minute late. It runs only while
    the backend is running.
  - The last result shows how many devices the command was queued for, or the error; if queuing fails, a
    notification is created too.
- **It ran, but a PC did nothing.** A due schedule only queues tasks. They then behave like any other task: an
  offline PC runs it when it connects, the concurrency limit applies, and a PC whose terminal capability is off
  refuses it with `[REDDEDİLDİ]`. Check the tasks on **İşlemler**.

## Software inventory and Windows updates

- **No software or Windows Update data for a device.** These are reported by agents 0.1.5-alpha and later; older
  agents show "bildirmedi" and ignore the scan and install buttons.
- **The agent's reports are rejected with `401`.** The software and Windows Update endpoints accept only enrolled
  agents with a valid device secret, even while enforcement is off. Enroll the device (see
  [Enrollment and 4401 rejections](#enrollment-and-4401-rejections)).
- **Scan or install did nothing.** Commands go only to devices that are online at that moment; the reply lists the
  skipped ones (`skipped_offline`). Results arrive when the agent reports them; the page reloads the list 15 seconds
  after sending.

## Licences

- **A licence shows 0 installed.**
  - Installed programs are reported by agents 0.1.5-alpha and later only. Check **Raporlar** → **Yazılım**: if no
    device reports software yet, every licence counts 0.
  - The **Eşleşme ifadesi** must appear in the program name as the inventory shows it (case does not matter). Type
    it in the licence form and look at the programs it matches, or search the **Yazılım** tab. A publisher filter
    that does not match the program's publisher also gives 0.
- **`Eşleşme ifadesinde %, _ ve \ kullanılamaz`.** Patterns are plain text; write the part of the name without
  wildcards.
- **No licence notifications.** The check runs once a day (on the first scheduler run of each day, server date),
  and only licences that are over their seats, expired or ending within 30 days notify. Sending them out by e-mail
  or webhook also depends on **En az önem**: expiring licences are `medium`, the default minimum is `high`.

## Helpdesk

- **An agent's ticket is refused with `429`.** That PC already has 5 open tickets (open, in progress or waiting)
  or opened 10 in the last hour. Resolve or close some tickets on **Destek talepleri**.
- **An agent's ticket is refused with `401`.** Only enrolled agents with a valid device secret can open tickets,
  even while enforcement is off.
- **Viewers see errors on Destek talepleri.** The ticket API needs the `admin` role; give the page only to admins.
- **A user does not see a reply.** Internal notes (**İç not**) are never sent to the PC; write a normal reply.
  Agents up to 0.1.4-alpha have no ticket function in the tray.
