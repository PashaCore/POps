# Dashboard (web panel)

The panel is a set of PHP pages in `Dashboard/`. PHP handles sign-in and page access; everything else is done in
the browser with calls to the backend API (`/api/…`) and the panel WebSocket (`/ws/panel`) on the panel's own
origin. The interface is in Turkish; page and button names below are quoted as they appear.

## Signing in

`login.php` asks for **Kullanıcı Adı** and **Şifre**. If the account has 2FA enabled, a second form asks for the
**Doğrulama Kodu** from the authenticator app. PHP sends the login to the backend (`POPS_API_INTERNAL_URL`), keeps
the session server-side and sets the `httpOnly` `pops_jwt` cookie that the browser then uses for API and
WebSocket calls. Sessions end when the browser session ends, when the token expires (`JWT_EXPIRE_HOURS`) or when
the account is changed or deleted; any `401` from the API returns you to the login page.

## Who sees which page

| Page (sidebar) | File | Access |
| --- | --- | --- |
| Dashboard | `index.php` | every signed-in user |
| Cihaz Yönetimi | `devices.php` | page permission `devices` |
| Laboratuvarlar | `labs.php` | `labs` |
| Görev Kuyruğu | `tasks.php` | `tasks` |
| Yardım Masası | `helpdesk.php` | `helpdesk` (the ticket API needs `admin` or `superadmin`) |
| POpsVision | `vision.php` | `vision` |
| Dosya Dağıtımı | `deploy.php` | `deploy`; never for `viewer` |
| Terminal | `terminal.php` | `terminal`; never for `viewer` |
| Sistem & Sürüm | `system.php` | superadmin |
| Log & Envanter | `logger.php` | `logger` |
| Raporlar | `reports.php` | `reports` |
| Politikalar | `policies.php` | `policies` |
| Ayarlar | `settings.php` | `settings`; never for `viewer` |

A superadmin sees every page. Other users see the pages ticked under **Erişebileceği Sayfalar** when their account
is created or edited (the **Terminal** permission is labelled "Orkestratör" there). These page permissions only
control the panel; what a user may do through the API depends on the role ([`security.md`](security.md#roles)).
Within pages, some controls are hidden by role, for example device deletion (superadmin only), the offline
bypass key (admin and superadmin), the Windows Update buttons and licence editing on **Raporlar** and the
scheduled tasks on **Görev Kuyruğu** (admin and superadmin).

## Notifications (bell)

For `admin` and `superadmin` the top bar has a bell with the number of unread notifications. It shows the latest
30 entries (time, device, detail; "gönderilemedi" when sending them out by e-mail or webhook failed), refreshes
every minute and has **Tümünü okundu say** (mark all as read). Which events create notifications is described in
[`security.md`](security.md#notifications); where they are sent is set on **Sistem & Sürüm** → **Bildirimler**.

## Pages

### Dashboard

"Sistem Özeti": device totals and online count, active tasks, backend reachability, quick links (hidden for
viewers), recent administrator operations, recent log signals, agent version distribution, task queue and
recently seen devices, a live chart of connected PCs, log volume for the last 7 days, devices per lab, disk use
of uploaded files and update packages, and the latest packages and scripts. Refreshes every 5 seconds while the tab is visible; nothing is requested while the tab is in the background, the interval grows four-fold after five minutes without keyboard or mouse input, and it backs off (up to 60 s) while requests fail. Every page that polls uses the same helper (`popsPoll` in `includes/header.php`).

### Cihaz Yönetimi

The device inventory as a flat list ("Düz Liste") or grouped by lab ("Sınıfa Göre"), searchable by name, IP, MAC,
lab or CPU. Per device: status, name (with **İsmi Değiştir** to set a display name), lab, IP/MAC, CPU/RAM/OS,
and actions to wake, restart or shut down, the key button (**Çevrimdışı Bypass Kodu**, the day's offline bypass
code for a quarantined PC) and delete. Selecting several devices enables bulk wake, restart, shut down and
**Taşı** (move to a lab). The header has **Ağı Komple Kapat** (shut down all online devices) and
**Ağı Uyandır (WOL)**.

Restart and shut down are queued as commands (`shutdown /r` / `shutdown /s`) through the task queue. Deleting a
device also deletes its device secret; see [`troubleshooting.md`](troubleshooting.md).

### Laboratuvarlar

- **Bekleme Odası** lists devices not yet assigned to a lab (`Atanmamis_Cihazlar`) and assigns them to an existing
  or new lab.
- **Lab Ekle** creates a lab; each lab card has a rename field, **Labı Sil** (its devices go back to the waiting
  room) and a device list with **Ana PC yap** (main PC), **Taşı** and **Çıkar**.
- **Cihaz Haritası** is a seating map with three columns; drag devices to arrange them (saved per lab) and use the
  lab-wide **Aç (WOL)**, **Yeniden Başlat** and **Kapat** buttons.
- **Toplu Taşı** moves many devices at once.

- **Oto-Kayıt** ("Toplu Oto-Kayıt"): choose a lab and an end date. Devices that connect to the server for the
  first time up to and including that date (server date) are put into that lab. Devices that are already known
  keep their lab, and an enrollment token created for a lab takes precedence. Setting the rule is written to the
  audit log; only one rule is active at a time.

Devices can also be placed in a lab at enrollment by creating the enrollment token for that lab
(**Sistem & Sürüm**).

### Görev Kuyruğu

Tasks grouped by lab and command, with a per-PC table showing the state (Sırada, İşleniyor, Durduruldu,
Tamamlandı, Hata Alındı, İptal Edildi). Pause, resume, cancel and retry act on one task, a lab or everything;
**Detay** shows the command sent and the agent's output; **Geçmişi Temizle** deletes the task history (recorded in
the audit log). The "Ajan Aktivite Radarı" lists recent log entries with filters.

How the queue works: each task targets one PC. A PC runs one task at a time, and at most `concurrent_limit` PCs
(default 5, set on **Ayarlar** or **Dosya Dağıtımı**) run tasks at once. Tasks for offline PCs stay pending until
the PC connects. A task that was running when its PC restarted is marked `Completed (Rebooted)`.

**Zamanlanmış görevler** (admin and superadmin) runs a command at set times: **Bir kez** (a date and time),
**Her gün** (a time) or **Seçili günler** (a time on chosen weekdays), on all devices, one lab or selected
devices. Times are in the server's time zone; the form shows the current server time. Each entry shows the next
and the last run with its result and has **Şimdi** (run once now), **Durdur** / **Başlat** and delete. When a
schedule is due, the server queues its command as normal tasks, so the concurrency limit, the terminal capability
of each PC and the audit log apply as for any other command. Creating, pausing, running and deleting schedules is
recorded with the user who did it.

### Yardım Masası

The helpdesk: tickets opened from the panel (**Yeni talep**) or sent by an agent from the PC.

- Filter chips by status with counts (the default shows the active ones: open, in progress, waiting) and a search
  over subject, text, reporter and computer name. The list refreshes every 30 seconds.
- A ticket shows the reporter (and whether it came from the tray or the panel), the device with its lab and
  online state, the foreground program when known, the category, the opening time, the text and the thread. Change **status**, **priority** and **assignee** (**Bana ata** assigns it to you) with
  **Güncelle**; each change is added to the thread as an internal note.
- Write a reply, or tick **İç not (yalnızca panelde görünür)** for an internal note. For a ticket from a PC, the
  replies (never the internal notes) are what the agent can show to the user. Replying to an `open` ticket sets it
  to "Yanıt bekleniyor".
- Categories: Donanım, Yazılım, Ağ / İnternet, Yazıcı, Hesap / Şifre, Diğer.

A new ticket from an agent raises a notification. The server side accepts tickets only from enrolled agents,
with at most 5 open tickets per PC and 10 new tickets per hour; agents up to 0.1.4-alpha have no ticket function
in the tray yet.

### POpsVision

Lab cards, a wall of screen previews per lab, and a focus view for one PC with a live stream, remote control,
quarantine and the **Teşhis** (diagnostics) dialog. The session and consent rules and the diagnostic commands
are described in [`vision.md`](vision.md).

### Dosya Dağıtımı

- **Depo Merkezi** is a library of modules. **Modül Yükle** offers two types:
  - **Uygulama Paketi (.exe, .msi, .zip)**: the file is uploaded to the server (`/api/upload`) and the module
    stores a PowerShell command that downloads it from `/download/<file>` into `C:\POpsLogs\` on the PC and
    installs it: `.msi` with `msiexec /i … /qn /norestart` plus your parameters, `.zip` by extracting it and
    running the `install.bat` it contains, anything else by running the file with your parameters (for example
    `/S`). Exit codes 0 and 3010 count as success. Progress is written to `C:\POpsLogs\deploy_trace.txt` on the PC.
  - **Sistem Betiği (PowerShell, CMD)**: the text is run as is.
  - Both can reboot the PC afterwards ("İşlem bitince PC'yi yeniden başlat").
- **Görev Zinciri**: drag modules into an ordered sequence.
- **Hedefleme & Kurallar**: target the whole network, selected labs or selected PCs, then **Dağıtımı Başlat**. The
  panel asks for a reason, and every step becomes one queued task per PC (see Görev Kuyruğu).

Commands run as SYSTEM on the PCs. Files uploaded here are downloadable without a login only through the signed link the upload returns (`/download/<file>?sig=…`); the deployment script also checks the file's SHA-256 before running it.

### Terminal

Runs command lines on one PC ("Tekil Cihaz") or on every PC of a lab ("Toplu (Lab)"). Each command is queued as a
task and its output appears in the terminal as the agents answer (`terminal_output` on `/ws/panel`). Quick
buttons: **DNS Temizle**, **Ağı Yenile**, **Yazıcı Kuyruğu**, **Temp Temizle**, **GPUpdate**, **Görev Sonlandır**;
they ask for a reason. `/setname NAME` (one PC) and `/otorename PREFIX` (a lab) rename the computer, its first
enabled local user and its network adapters, then restart the PC. `cls` clears the screen.

### Sistem & Sürüm

Superadmin page in five parts:

1. **Sunucu**: running version, the state of GitHub `main` compared with the last self-update, and
   **Sunucuyu güncelle** (panel self-update, see [`self-update.md`](self-update.md)). When an update is available
   the card lists what it brings ("Güncellemeyle gelecek yenilikler", the `CHANGELOG.md` entries on GitHub `main`
   that the installed code does not have); otherwise "Bu sunucuda neler var" shows the notes of the running
   version. The notes come from GitHub (in English) and are hidden when the server is offline. The commit list is
   kept as a collapsed technical detail.
2. **Ajan güncelleme**: step 1, the package: download and verify the latest GitHub release, or upload
   `manifest.json`, `manifest.json.sig` and the MSI on an offline server; the release notes of the latest agent
   release are shown here. Step 2, the targets: all agents, one lab or selected online devices (with a shortcut to
   select outdated ones), then send.
3. **Cihaz yetenekleri (terminal / Vision)**: per device, turn the terminal or Vision off, or clear a standing
   "off" request ("izin ver"; the agent itself is re-enabled only locally).
4. **Bildirimler**: whether notifications are also sent out (**Dışarıya gönder**), the lowest severity to send
   (**En az önem**), e-mail recipients and a webhook address, **Kaydet** and **Test gönder** (sends a test with the
   values in the form, saved or not, and shows the result). E-mail needs `SMTP_HOST` and a sender address
   (`SMTP_FROM`, or `SMTP_USER`) in the server's `.env`; the card says whether SMTP is configured. See [`configuration.md`](configuration.md#notification-settings).
5. **Ajan kaydı ve kimlik**: create enrollment tokens (lab, note, number of uses, lifetime in hours), list and
   delete them, and turn agent-auth enforcement on or off. The card shows how many agents are enrolled.

### Log & Envanter

Choose a device on the left (grouped by lab, searchable). **Aktivite Logları** shows its event log with filters and
the raw JSON of each entry; **Donanım** shows its hardware inventory. **Tüm Cihazları Uyandır (WOL)** asks you to
type `TÜMÜ` to confirm.

### Raporlar

Fleet reports for the last 7, 30 or 90 days, in four tabs:

- **Özet**: devices (total, online, quarantined, enrolled), devices missing security updates, software coverage,
  high and critical events per day, agent versions, agent update results, the most-violated domains and the
  devices with the most high-risk events.
- **Yazılım**: search installed programs across the fleet by name or publisher; open a program to see which
  devices have it and in which version.
- **Windows güncellemeleri**: each device's Windows Update state (pending, security, critical, restart needed,
  last scan, last result; "bildirmedi" if it never reported). Admins can select online devices and use **Tara**,
  **Güvenlik güncellemelerini kur** or **Tümünü kur**; the PC is not restarted automatically.
- **Lisanslar**: licence definitions compared with the software inventory: installed against allowed seats, free
  seats, and the state (Uygun, Aşım, Bitiyor within 30 days, Süresi doldu), with a summary of problems. Open a
  licence to see the devices that have it. Admins add (**Lisans ekle**), edit and delete licences: name, the text
  matched in program names (**Eşleşme ifadesi**, plain text; while typing, the form lists the programs it matches),
  an optional publisher filter, seats (empty = unlimited), type (Cihaz başına, Site / kampüs, Abonelik), end date
  and notes. Licences over their seats, expired or ending within 30 days raise a notification once a day.

**CSV indir** exports devices, software, Windows updates, licences or the events of the chosen period.

Software, Windows Update and licence data come from agents 0.1.5-alpha and later; older agents show no data and
ignore the scan and install commands.

### Politikalar

Edits the agent policy, in four parts:

- **Ajanlarda durum**: whether a fair-use text is set, whether DNS detection is on and how many agents can run it
  (0.1.5-alpha and later), and whether automatic quarantine is on.
- **Aydınlatma ve adil kullanım metni**: the text the tray shows to users until they acknowledge it; empty means
  none. **Örnek metni kullan** fills in an example to adapt (see also [`kvkk-aydinlatma.md`](kvkk-aydinlatma.md)).
- **Yasaklı alan adı tespiti (DNS)**: a switch and a domain list (one per line) for each category: pornography,
  illegal betting, terror propaganda, malware and phishing, and the school's own list (`okul_ozel`). Only listed
  domains and their subdomains are matched; an empty list matches nothing. Matches are detected and reported, not
  blocked; use the school's network filter to block.
- **Otomatik karantina**: on or off, with the violation threshold. A PC that reaches it isolates itself from the
  network except for the POps server, DNS and DHCP.

**Kaydet** is enabled when there are unsaved changes. Viewers see the page read-only.

Agents up to 0.1.4-alpha do not start their DNS monitoring, so DNS detection and automatic quarantine have no
effect on them. See [`configuration.md`](configuration.md#agent-policy-object).

### Ayarlar

- **Merkez API Bağlantısı**: the API and WebSocket addresses in use and a connection test.
- **Orkestrasyon Performansı**: the task concurrency limit (`concurrent_limit`, 1–200).
- **İki Adımlı Doğrulama (2FA)**: set up (QR code and manual key), enable with a code, or disable with a code, for
  your own account.
- **Kullanıcı Yönetimi**: list users; a superadmin can add, edit and delete them and set their page permissions.
  Roles: "İzleyici (yalnızca görüntüler)" (`viewer`), "Standart Yönetici" (`admin`) and "Süper Admin"
  (`superadmin`).

## Shared behaviour

- Most pages refresh their data from the API every 3–5 seconds.
- Every API call from the panel sends `X-Requested-With: XMLHttpRequest`, which the backend requires for
  cookie-authenticated changes (CSRF protection).
- Values coming from agents (device names, logs, command output) are HTML-escaped before display.
- The panel uses a single light theme.
