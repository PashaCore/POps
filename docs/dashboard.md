# Dashboard (web panel)

The panel is a set of PHP pages in `Dashboard/`. PHP handles sign-in and page access; everything else is done in
the browser with calls to the backend API (`/api/…`) and the panel WebSocket (`/ws/panel`) on the panel's own
origin. The interface is in Turkish; page and button names below are quoted as they appear.

## Signing in

`/login` asks for **Kullanıcı adı** and **Şifre**. If the account has 2FA enabled, a second form asks for the
**Doğrulama kodu** from the authenticator app. PHP sends the login to the backend (`POPS_API_INTERNAL_URL`), keeps
the session server-side and sets the `httpOnly` `pops_jwt` cookie that the browser then uses for API and
WebSocket calls. Sessions end when the browser session ends, when the token expires (`JWT_EXPIRE_HOURS`) or when
the account is changed or deleted; any `401` from the API returns you to the login page.

## Addresses

Pages have addresses without `.php`: `/devices`, `/labs?lab=…`, `/tasks?job=…`; the overview is `/`. The bundled
web server configurations map them to the PHP files: `Installer/server/apache-htaccess.example` (copied to
`Dashboard/.htaccess` on Apache, which is not tracked in the repository; see [deployment.md](deployment.md)),
`docker/apache-pops.conf` and the nginx templates in `Installer/server/`. Old `.php` addresses keep working; on Apache a `GET` to one is redirected to the address
without `.php`.

## Who sees which page

| Page (sidebar) | File | Access |
| --- | --- | --- |
| Kontrol merkezi | `index.php` (`/`) | every signed-in user |
| Cihazlar | `devices.php` | page permission `devices` |
| Sınıflar | `labs.php` | `labs` |
| İşlemler | `tasks.php` | `tasks` |
| Uzak komut | `terminal.php` | `terminal`; never for `viewer` |
| Uzak ekran | `vision.php` | `vision` |
| Dağıtım | `deploy.php` | `deploy`; never for `viewer` |
| Politikalar | `policies.php` | `policies` |
| Kayıtlar | `logger.php` | `logger` |
| Raporlar | `reports.php` | `reports` |
| Destek talepleri | `helpdesk.php` | `helpdesk` (the ticket API needs `admin` or `superadmin`) |
| Ayarlar | `settings.php` | `settings`; never for `viewer` |
| Sistem | `system.php` | superadmin |

A superadmin sees every page. Other users see the pages ticked under **Açabileceği sayfalar** when their account
is created or edited. These page permissions only control the panel; what a user may do through the API depends on
the role ([`security.md`](security.md#roles)). Within pages, some controls are hidden by role, for example the PC
actions on **Cihazlar** and in the PC detail panel (not shown to viewers), device deletion (superadmin only), the
offline unlock code (admin and superadmin), the Windows Update buttons and licence editing on **Raporlar** and the
scheduled tasks on **İşlemler** (admin and superadmin).

## Sidebar, search, notifications and job center

There is no top bar: everything that is the same on every page is in the sidebar (on a narrow screen it opens with
the menu button).

- **Ara** (Ctrl+K, or Cmd+K on a Mac) searches pages, devices and labs. Pages are listed at once; devices (name, ID,
  IP) and labs are searched as you type. Enter opens the highlighted result: a PC opens on **Cihazlar** with its
  detail panel, a lab opens on **Sınıflar**.
- The pages are in groups: **Kontrol merkezi** on top; **Cihazlar** (Cihazlar, Sınıflar); **İşlem** (İşlemler,
  Uzak komut, Uzak ekran, Dağıtım); **Yönetim** (Politikalar, Kayıtlar, Raporlar, Destek talepleri); **Sunucu**
  (Ayarlar, Sistem). A user sees only the pages the account may open. On **Sınıflar** the lab list is shown under
  the entry (online/total PCs per lab, a red dot when a PC of the lab is quarantined); **Atanmamış** appears there
  while new PCs have no lab.
- At the bottom: the job center, **Bildirimler** and the signed-in user with the sign-out button.

**Job center.** As soon as this browser tab has sent something to PCs (a power command, a message, a command, a
deployment, a retry), a card "N işlem sürüyor" with a progress bar appears. It opens a list with
one entry per job: title, number of PCs, live progress and, when something failed, the PCs that failed. **Bitenleri
temizle** clears finished jobs; **Bütün işlemler** opens **İşlemler**. The list is kept for the browser tab only;
finished jobs disappear from it after 15 minutes, while **İşlemler** keeps the full history. The progress is read
from `POST /api/tasks/status`.

**Bildirimler** (for `admin` and `superadmin`) shows the number of unread notifications. It lists the latest 30
entries (time, device, detail; "gönderilemedi" when sending them out by e-mail or webhook failed), refreshes every
minute and has **Tümü okundu** (mark all as read) and **Okunanları temizle** (delete the read ones; the events stay
in the audit records). Which events create notifications is described in [`security.md`](security.md#notifications);
where they are sent is set on **Sistem** → **Bildirimler**.

## Working with PCs

**Cihazlar** and **Sınıflar** (and the same buttons in the detail panel) work the same way:

- **One action bar per page.** With nothing selected the actions apply to the whole list or lab, and the scope
  label next to the buttons says so ("Listedeki 24" on **Cihazlar**, "Tüm sınıf 12" on **Sınıflar**; the tooltips
  repeat it, for example "Yeniden başlat · tüm sınıf"). Selecting PCs narrows the actions to them and the label
  becomes "3 seçili"; **Esc** or the × next to the label clears the selection.
- **Selecting.** On **Sınıflar** click the circle of a tile or Ctrl/Cmd+click a tile; **Ctrl+A** selects every PC of
  the lab. On **Cihazlar** tick the checkbox of a row (or the one in the header for the listed PCs) or Ctrl/Cmd+click
  a row.
- **Actions.** Wake, restart and shut down (restart and shut down ask for confirmation and skip PCs that are
  off), screen and command (these hand the online PCs over to **Uzak ekran** and **Uzak komut**), and a **Diğer
  işlemler** menu: message, move to another lab, quarantine and lift quarantine. Quarantine asks for a reason.
- **Detail panel.** Clicking a PC opens a panel on the right: status and signed-in user, round buttons for
  **Ekran**, **Komut**, **Güç** and **Diğer** (disabled when the PC is off or the capability is turned off for
  it), its issues (quarantine, outdated agent, errors reported by the agent, remote command or screen turned off on it),
  the facts (user, application, lab,
  IP, MAC, agent version, memory, last seen, reason of the last disconnect, ID) and **Son işlemler**: the latest
  tasks and remote-screen sessions on the PC with what was done, who did it, when, from which page, the reason
  and the result (a failed task says why). The list comes from `GET /api/devices/{pc}/activity`. **Diğer** has
  message, rename, move, make or unmake teacher PC, quarantine or lift it, the offline unlock code (only for a
  quarantined PC) and, for a superadmin, delete.
- **Who did it.** Every task records its title, the page it was sent from, the reason, the caller's IP address and a
  job ID shared by the tasks of one request. **İşlemler**, **Kontrol merkezi** and the detail panel show them.

## Pages

### Kontrol merkezi

The overview. A summary line (server reachable, number of devices and labs, time of the last update) and four
tiles that link to the matching list: **Çevrimiçi** (PCs on, with a bar; opens **Cihazlar**), **Sorunlu cihaz**
(quarantined PCs, outdated agents and PCs with errors; opens the **Sorunlu** filter), **Süren işlem** (running
jobs; opens **İşlemler**) and **Güncel ajan** (share of PCs on the newest agent version; opens **Sistem** for a
superadmin when agents are outdated). Below: **Son etkinlik**, one feed of the latest jobs (what, who, when, from
which page, target, result and why it failed; a click opens the job on **İşlemler**) and the latest events from the
PCs (logons, blocked sites, quarantine, update results, with the same readable titles and honest levels as
**Kayıtlar**; a click opens that PC's records), filtered with **Tümü / İşlemler / Olaylar**; **İlgilenmen
gerekenler** (quarantined PCs, jobs that failed today, outdated agents, new PCs waiting for a lab, PCs that have
been off for more than 7 days) and **Sınıflar** (online PCs per lab). The bottom row: **Sunucu** (running, database,
version, whether an update is available and how many agents have a device key; admins), **Ajan sürümleri** (how many
PCs run each agent version, with a bar) and **Bugün** (jobs sent, jobs that ended with a problem, logons, blocked
sites, warnings). Refreshes by itself, see [Shared behaviour](#shared-behaviour).

### Cihazlar

The device list as a table (**Bilgisayar**, **Sınıf**, **Durum**, **Ajan**, **IP**), sortable by column. A summary
line counts the PCs (on, idle, off, with issues); the filters are **Tümü**, **Açık**, **Kapalı** and **Sorunlu**, a
lab selector (**Bütün sınıflar**, **Atanmamış**) and a search over name, user, IP and MAC. The download icon
exports the list as CSV. Click a PC for its detail panel; the action bar and the selection work as described under
[Working with PCs](#working-with-pcs).

Restart and shut down are queued as commands (`shutdown /r /f /t 5` / `shutdown /s /f /t 5`) through the task
queue. Wake-on-LAN needs the MAC address from the hardware inventory. A **Mesaj gönder** note is shown to the
signed-in user with the Windows `msg` command. Deleting a device also deletes its device secret; see
[`troubleshooting.md`](troubleshooting.md).

### Sınıflar

One lab at a time. The labs are listed under **Sınıflar** in the sidebar, and **Atanmamış** lists the PCs that are
not in a lab yet (`Atanmamis_Cihazlar`; its action bar has **Sınıfa taşı**). A lab is shown as a seating map: the
teacher PC on top and three columns of PC tiles (name, user or state, a mark for problems). A search box dims the
tiles that do not match. Select tiles and use the action bar, or click a tile for the detail panel.

- **Yerleşimi düzenle** switches to layout editing: drag a tile to another column or to the teacher spot and drop
  it; every drop is saved at once (per lab). **Bitti** ends editing.
- The **Sınıf işlemleri** menu: **Yeni sınıf…**, **Yeniden adlandır…**, **Sınıfı sil** (its PCs go back to
  **Atanmamış**) and **Otomatik kayıt…**: choose an end date, and PCs that connect to the server for the first time
  up to and including that date (server date) are put into this lab. PCs that are already known keep their lab,
  and an enrollment token created for a lab takes precedence. Setting the rule is written to the audit log; only
  one rule is active at a time.
- The lab's teacher PC is set in the detail panel (**Diğer** → **Öğretmen bilgisayarı yap**).

Devices can also be placed in a lab at enrollment by creating the enrollment token for that lab (**Sistem**).

### İşlemler

Everything that was sent to PCs, in two tabs.

**İşler.** The tasks of one request form one job (they share a job ID; older records are grouped by time, user and
command). A row shows the title, who sent it, when, from which page, the target (a PC, a lab or the number of
PCs), the reason and the result with a progress bar. The filters are **Tümü**, **Sürüyor** and **Sorunlu**, with a
search over title, command, PC, user and reason. A click opens the job in the right-hand panel: sender, source
page, IP address, reason, target, time, the command and one row per PC with its state (Sırada, Çalışıyor,
Duraklatıldı, Tamamlandı, Başarısız, Hata, İptal edildi, Yarıda kaldı, Zaman aşımı, Reddedildi, Bilinmiyor, Süresi
doldu), the exit code and why it failed; click a PC to show the agent's output. Admins can **Başarısızları yeniden
dene** (each failed task opens a new one), **Duraklat**, **Devam ettir** and **İptal et** for the job. The header
has the limit of PCs that run tasks at once (the sliders button) and **Kuyruk işlemleri**: pause everything
pending, resume what is paused and **Bütün görev kayıtlarını sil** (the deletion is recorded in the audit log).

How the queue works: each task targets one PC. A PC runs one task at a time, and at most `concurrent_limit` PCs
(default 5, set on **Ayarlar**, **Dağıtım** or here) run tasks at once. Tasks for offline PCs stay pending until
the PC connects. A task that was running when its PC restarted is marked `Completed (Rebooted)`.

#### Zamanlanmış

The **Zamanlanmış** tab (admin and superadmin; **Zamanlanmış görev** adds one) runs a command at set times: **Bir
kez** (a date and time), **Her gün** (a time) or **Seçili günler** (a time on chosen weekdays), on all devices, one
lab or selected devices. Times are in the server's time zone; the list shows the current server time. Each entry
shows the next and the last run with its result; its panel has **Şimdi çalıştır**, **Durdur** / **Başlat** and
**Sil**. When a schedule is due, the server queues its command as normal tasks, so the concurrency limit, the
terminal capability of each PC and the audit log apply as for any other command. Creating, pausing, running and
deleting schedules is recorded with the user who did it.

### Destek talepleri

The helpdesk: tickets opened from the panel (**Yeni talep**) or sent by an agent from the PC.

- The filters **Açık** (the active ones: open, in progress, waiting), **Tümü** and **Kapalı** show the counts, and
  the search covers subject, text, reporter and computer name. The list refreshes every 30 seconds.
- A click on a ticket opens a panel on the right: the reporter (and whether it came from the tray or the panel),
  the PC with its lab and online state, the signed-in user and the foreground program when known, the category,
  priority, assignee, the times and the thread. Round buttons: the next step (**Üzerine al**, **Çözüldü** or
  **Yeniden aç**), **Durum**, **Ata** (**Bana ata**, **Başkasına ata…**, **Atamayı kaldır**) and **Diğer** (the
  priority, and **Ekranı izle** and **Bilgisayarı aç** for the PC). Each change is added to the thread as an
  internal note.
- Write a reply, or tick **İç not (kullanıcı görmez)** for an internal note. For a ticket from a PC, the
  replies (never the internal notes) are what the agent can show to the user. Replying to an `open` ticket sets it
  to "Yanıt bekleniyor".
- Categories: Donanım, Yazılım, Ağ / İnternet, Yazıcı, Hesap / şifre, Diğer.

A new ticket from an agent raises a notification. The server side accepts tickets only from enrolled agents,
with at most 5 open tickets per PC and 10 new tickets per hour; agents up to 0.1.4-alpha have no ticket function
in the tray yet.

### Uzak ekran

The page of the POpsVision feature. A wall of screen previews for one lab (the lab selector), for the PCs handed
over from **Cihazlar** or **Sınıflar** (**Seçili N bilgisayar**) or for a search; admins have the same action bar
as on the other pages. **Oturum geçmişi** lists the remote-screen sessions and **Ekranları tazele** requests new
previews. A click on a screen opens the focus view: **Canlı izle** starts a session (**Kullanıcıya sor** or
**Zorunlu müdahale**, with a reason), **Kontrol** turns on remote mouse and keyboard, the frame rate can be set to
1, 2 or 5 frames per second, and the **Diğer işlemler** menu has the PC's details, **Teşhis komutları** (the
**Teşhis** dialog), the session history and quarantine. The session and consent rules and the diagnostic commands
are described in [`vision.md`](vision.md). Remote keyboard input sends the key, the physical key code and the
Ctrl/Alt/Shift/Win/AltGr state, so the agent can type Turkish layouts and AltGr characters; keys still held
are released when control is turned off, the page loses focus or the tab is hidden.

### Dağıtım

A library of packages and scripts (a table with size, SHA-256, date added and last deployment; filter **Tümü** /
**Paketler** / **Betikler**, search) and the place where they are sent to PCs.

- **Paket yükle** adds an item, in two types:
  - **Kurulum paketi** (`.exe`, `.msi`, or a `.zip` that contains `install.bat`): the file is uploaded to the server
    (`/api/upload`) and the item stores a PowerShell command that downloads it from `/download/<file>` into
    `C:\POpsLogs\` on the PC and installs it: `.msi` with `msiexec /i … /qn /norestart` plus your parameters
    (**Sessiz kurulum parametreleri**), `.zip` by extracting it and running the `install.bat` it contains, anything
    else by running the file with your parameters (for example `/S`). Exit codes 0 and 3010 count as success.
    Progress is written to `C:\POpsLogs\deploy_trace.txt` on the PC.
  - **Betik** (PowerShell, CMD): the text is run as is.
  - Both can reboot the PC afterwards (**Bitince yeniden başlat**).
- Click an item for its panel: **Dağıt**, **Düzenle**, copy the hash, the download address or the command,
  **Görev zincirine ekle**, delete, and its latest deployments.
- **Dağıt** opens the deployment dialog: the steps (a chain of several items runs in the order shown; the
  **Görev zinciri oluştur** button starts with an empty chain), the target (**Bütün ağ**, selected labs under
  **Sınıflar**, or selected PCs under **Bilgisayarlar**), an optional reason and **Dağıt**. PCs that are off are
  skipped unless **Kapalılar açılınca kursun** is on. Every step becomes one queued task per PC (see **İşlemler**),
  with the page and the reason recorded.
- The sliders button sets how many PCs install at the same time (**Eşzamanlı kurulum sınırı**, 1–100).

Commands run as SYSTEM on the PCs. Files uploaded here are downloadable without a login only through the signed link the upload returns (`/download/<file>?sig=…`); the deployment script also checks the file's SHA-256 before running it.

### Uzak komut

Runs command lines on one PC (**Bilgisayar**), on every PC of a lab (**Sınıf**) or on the PCs selected on
**Cihazlar** or **Sınıflar** (**Seçili N**). Each command is queued as a task and its output appears in the
terminal as the agents answer (`terminal_output` on `/ws/panel`). The **Hızlı komutlar** menu has **DNS önbelleğini
temizle**, **Ağ bağlantısını yenile**, **Yazıcı kuyruğunu sıfırla**, **Geçici dosyaları temizle**, **Grup ilkesini
güncelle**, **Uygulamayı kapat…** (ends every process with the program name you type), a rename entry and **Hedefi
uyandır**; the quick commands and **Uygulamayı kapat…** ask for a reason. `/setname NAME` (one PC) and
`/otorename PREFIX` (a lab) rename the computer, its first enabled local user and its network adapters, then
restart the PC. `cls` clears the screen. The clock button, **Komut geçmişi**, lists what was sent from this page
(who, when, target, result, reason); a click puts the command back into the input line without sending it.

The agent writes every command into a `.bat` file and runs it with `cmd.exe /c` as SYSTEM, so each quick button
is the exact `cmd` line that ends up in that file (`QUICK_ACTIONS` in `terminal.php`), and a non-zero exit code
marks the task failed:

- **Ağ bağlantısını yenile**: `ipconfig /release`, `ipconfig /renew`; succeeds when the PC then has an IPv4 address outside
  `169.254.*` (adapters with a static or no address do not make it fail).
- **Yazıcı kuyruğunu sıfırla**: stops the spooler, deletes `%windir%\System32\spool\PRINTERS\*`, starts the spooler.
- **Geçici dosyaları temizle** (PowerShell): empties `C:\Windows\Temp` and every user's `AppData\Local\Temp`, skipping files
  in use and the running task's own `pops_task_*.bat`; junctions and symbolic links are not followed. It reports
  how many files it deleted and skipped.

### Sistem

Superadmin page in five tabs; the open tab is kept in the address (`/system?tab=health`). The header has **Sürüm
notları** and **Güncellemeleri denetle** (asks GitHub again).

- **Genel bakış**: one tile per card below with its current state (Sunucu, Ajanlar, Sağlık, Yedekler, Ajan kaydı ve
  kimlik, Cihaz yetenekleri, Kayıt bütünlüğü, Bildirimler); a click opens the tab that holds the card. Under the
  tiles, **Eğilimler** draws six charts for the last 24 hours, 7 days or 30 days (the choice is remembered in the
  browser): **Bağlı ajanlar** (with the agent updates of the period), **İşlemci ve bellek**, **API istekleri** (server
  errors in red), **İşlemler** (successful, failed, refused, other), **Olaylar** (by risk level) and **Veritabanı ve
  disk**. Hovering a point shows its time and values. Agents, processor, memory, requests and database size come
  from a sample the server takes every minute and keeps for 30 days, so after an update the charts start filling
  within a few minutes and a gap marks the time the server was down; tasks and events come from their records.
- **Güncellemeler**: cards 1 and 2. **Güvenlik**: cards 3 and 4 and **Kayıt bütünlüğü**. **Sağlık ve yedek**:
  **Sağlık** and **Yedekler**. **Bildirimler ve saklama**: cards 6 and 7.

An admin who is not a superadmin sees only the **Sunucu** and **Ajanlar** cards, without tabs.

1. **Sunucu**: running version, update channel, the latest version on GitHub and **Sunucuyu güncelle** (panel
   self-update, see [`self-update.md`](self-update.md)), with the result of the last attempt. **Sürüm notları**
   shows "Güncellemeyle gelecekler" (the `CHANGELOG.md` entries on GitHub `main` that the installed code does not
   have) when an update is available, otherwise "Bu sunucuda" (the notes of the running version), and the notes of
   the latest agent package. The notes come from GitHub (in English) and are hidden when the server is offline. On
   the `main` channel **Değişiklikleri göster** lists the commits that are coming.
2. **Ajanlar**: the version distribution and the agent package: download and verify the latest GitHub release, or
   **Paketi elle yükle…** (`manifest.json`, `manifest.json.sig` and the MSI) on an offline server. Then update the
   outdated online agents with one button, or choose a lab or selected PCs; the progress of the update is shown.
3. **Ajan kaydı ve kimlik**: the **Kimlik zorlaması** switch (agent-auth enforcement; the card shows how many
   agents are enrolled) and **Kayıt jetonları**: **Jeton üret** (**Sınıf**, **Not**, **Kullanım sayısı**,
   **Geçerlilik (saat)**), the list of tokens and revoking them.
4. **Cihaz yetenekleri**: per device, turn remote command or remote screen off, or clear a standing "off" request
   (**İzin ver**; the agent itself is re-enabled only locally).
5. **Sağlık**, **Yedekler** and **Kayıt bütünlüğü**: server health (uptime, connected agents, database pool,
   errors, scheduler, disk, certificate expiry), the result of the last nightly backup, and **Doğrula** for the
   audit chain (`GET /api/system/audit-verify`).
6. **Bildirimler**: whether notifications are also sent out (**Dışarıya gönder**), the lowest severity to send
   (**En az önem**), e-mail recipients and a webhook address, **Kaydet** and **Test gönder** (sends a test with the
   values in the form, saved or not, and shows the result). E-mail needs `SMTP_HOST` and a sender address
   (`SMTP_FROM`, or `SMTP_USER`) in the server's `.env`; the card says whether SMTP is configured. See [`configuration.md`](configuration.md#notification-settings).
7. **Saklama süreleri**: how many days to keep agent event records, finished tasks and read notifications (0 keeps
   them for ever). The audit chain and remote-screen sessions are never deleted.

### Kayıtlar

Two tabs. **Olaylar** is the event log of the PCs and the administrators, written as readable sentences, grouped by
day and shown in pages (**Sayfa başına** 25, 50, 75 or 100, remembered in the browser; page numbers and "1–25 / 312
kayıt" under the list). Filters: **Tümü**, **Uyarılar** and **Güvenlik**, a search over event, PC and person, and
**Süzgeçler** with the event type, lab, person and a date range; active filters appear as chips that remove them one
by one (**Hepsini temizle**). The latest 1000 entries are loaded; with a date range or a PC chosen, the server returns
every entry of that range or PC. A click on an entry shows its details (PC, lab, who, source, IP, reason, command, event type, risk
level, record number and any extra fields); from there you can show only that PC's records or open the PC.
**Donanım** shows the hardware inventory of every PC (processor, memory, disk, operating system, IP, last update),
with a lab filter and a search. The download icon exports the current list as CSV. The **Diğer işlemler** menu has
**Bütün bilgisayarları uyandır** (admins; it asks to confirm how many PCs are off) and, for a superadmin,
**Denetim zincirini doğrula**.

### Raporlar

Fleet reports for the last 7, 30 or 90 days, in four tabs:

- **Özet**: devices (total, online, quarantined, enrolled), devices missing security updates, software coverage,
  high and critical events per day, agent versions, agent update results, the most-violated domains and the
  devices with the most high-risk events.
- **Yazılım**: search installed programs across the fleet by name or publisher; open a program to see which
  devices have it and in which version.
- **Windows güncellemeleri**: each device's Windows Update state (pending, security, critical, restart needed,
  last scan, last result; "bildirmedi" if it never reported). Admins can select devices (or use the whole list, see
  [Working with PCs](#working-with-pcs)) and use **Güncellemeleri tara**, **Güvenlik güncellemelerini kur** or
  **Bütün güncellemeleri kur**; only online devices receive them and the PC is not restarted automatically.
- **Lisanslar**: licence definitions compared with the software inventory: installed against allowed seats, free
  seats, and the state (Uygun, Aşım, Bitiyor within 30 days, Süresi doldu), with a summary of problems. Open a
  licence to see the devices that have it. Admins add (**Lisans ekle**), edit and delete licences: name, the text
  matched in program names (**Eşleşme ifadesi**, plain text; while typing, the form lists the programs it matches),
  an optional publisher filter, seats (empty = unlimited), type (Cihaz başına, Site ya da kampüs, Abonelik), end date
  and notes. Licences over their seats, expired or ending within 30 days raise a notification once a day.

The download icon (**CSV olarak indir**) exports devices, software, Windows updates, licences or the events of the
chosen period.

Software, Windows Update and licence data come from agents 0.1.5-alpha and later; older agents show no data and
ignore the scan and install commands.

### Politikalar

Edits the agent policy for the whole organisation. A summary line shows the state (whether a fair-use text is
shown, how many categories and domains are watched, whether automatic quarantine is on and how many agents support
DNS detection), and the page has three parts:

- **Aydınlatma metni**: the fair-use text the tray shows to users until they acknowledge it; empty means none.
  **Örnek metni kullan** fills in an example to adapt (see also [`kvkk-aydinlatma.md`](kvkk-aydinlatma.md)).
- **Yasaklı alan adı tespiti**: a switch and a domain list (one per line) for each category: pornography,
  illegal betting, terror propaganda, malware and phishing, and the school's own list (`okul_ozel`). Only listed
  domains and their subdomains are matched; an empty list matches nothing. Matches are detected and reported, not
  blocked; use the school's network filter to block.
- **Otomatik karantina**: on or off, with the violation threshold (**Eşik**). A PC that reaches it isolates itself
  from the network except for the POps server, DNS and DHCP.

A bar with **Kaydet** and **Vazgeç** appears when there are unsaved changes. Viewers see the page read-only.

Agents up to 0.1.4-alpha do not start their DNS monitoring, so DNS detection and automatic quarantine have no
effect on them. See [`configuration.md`](configuration.md#agent-policy-object).

### Ayarlar

Three tabs, kept in the address like on **Sistem**: **Kullanıcılar**, **Güvenlik** (two-step verification) and
**Genel** (task queue and server connection).

- **Kullanıcılar**: the users with their role, page access and last sign-in; a click opens the user's panel. A
  superadmin can add users (**Kullanıcı ekle**), edit the role and the pages (**Rolü ve yetkileri düzenle**, the
  list **Açabileceği sayfalar**), reset a password (**Şifreyi sıfırla**) and delete a user. Roles: **İzleyici**
  (`viewer`; only looks at the chosen pages, **Dağıtım**, **Uzak komut** and **Ayarlar** stay closed),
  **Yönetici** (`admin`) and **Süper admin** (`superadmin`).
- **İki adımlı doğrulama**: set up (QR code and manual key), enable with a code (**Etkinleştir**), or disable with a
  code, for your own account. 2FA is optional but recommended: an admin or superadmin whose own 2FA is off sees a
  short notice under the title of this page and of **Sistem**; × hides it in that browser for 7 days.
- **Görev kuyruğu**: the task concurrency limit (**Eşzamanlı görev sınırı**, `concurrent_limit`, 1–200).
- **Sunucu bağlantısı**: the API and WebSocket addresses in use and a connection test. The addresses come from the
  server's `.env` and cannot be changed here.

## Shared behaviour

- Pages refresh their data from the API every few seconds (the device list every 5 s, **İşlemler** every 4 s,
  **Kontrol merkezi** 6 s and 15 s). Nothing is requested while the tab is in the background, the interval grows
  four-fold after five minutes without keyboard or mouse input, and it backs off (up to 60 s) while requests fail.
  Every page that polls uses the same helper (`popsPoll` in `includes/header.php`).
- Every API call from the panel sends `X-Requested-With: XMLHttpRequest`, which the backend requires for
  cookie-authenticated changes (CSRF protection).
- Values coming from agents and users (device names, logs, command output, tickets) are HTML-escaped before
  display; the CI job `Dashboard checks` enforces this (see [`security.md`](security.md#panel-output-xss)).
- The panel uses a single light theme.
