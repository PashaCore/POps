# FAQ

## General

**Is POps a classroom teaching tool like Veyon?**
No. POps is for the people who run the labs: inventory, deployment, updates, remote support and audit. It can run
next to a classroom tool. See [`positioning.md`](positioning.md).

**Which systems are supported?**
The server runs on Linux with systemd (`install.sh` is tested on AlmaLinux/RHEL/Rocky and Debian/Ubuntu) with
PostgreSQL and Python 3.10 or newer (3.12 recommended; the installer adds it on AlmaLinux/RHEL 9). The agent runs on 64-bit Windows 10 or 11 and brings its own .NET runtime; nothing has to be installed first.

**How many PCs can one server handle?**
The measured numbers are in [`BENCHMARKS.md`](../BENCHMARKS.md) and the [capacity report](kapasite/README.md): one
backend process brought 5,000 simulated agents back within 11 seconds of a restart, at about 40 % of one CPU core and
69 MB + 0.16 MB per agent of memory. Hundreds of agents sending full software lists at the same moment raise latency
above a second. The backend runs as a single process by default; several workers or servers are possible with
Redis ([`ha.md`](ha.md)).

**Is the panel available in English?**
Yes, every page: **Türkçe / English** at the bottom of the sidebar or under the sign-in form switches the interface
for your browser. Until you choose, the sign-in page follows the browser's language. Names people typed (labs,
devices, packages) stay as they are, and server messages without an English entry stay Turkish
([`i18n.md`](i18n.md)).

**Can I try POps without installing it?**
Yes. A public, read-only demo runs at [demo.pashacore.com.tr](https://demo.pashacore.com.tr) (user `demo`,
password `demo`): a made-up school with 50 PCs that report like real agents but never run anything. It is reset
every night. How it is built: [`deploy/demo/`](../deploy/demo/README.md).

**Does POps work without internet access?**
Yes. The server checks GitHub for new versions but works without it; agent releases can be uploaded by hand on
**Sistem**; the self-update redeploys the local checkout when it cannot fetch. The panel loads nothing from third
parties: fonts, icons, charts and the 2FA QR code all come from the POps server, so the administrator's browser
needs no internet access and does not contact a CDN (Google, Cloudflare) while the panel is open.

**Can POps tell me when something goes wrong?**
Yes. Failed or rolled-back agent updates, takeover attempts, DNS policy violations, quarantines and similar events
appear under **Bildirimler** in the panel, and a superadmin can have them sent by e-mail or to a webhook
(**Sistem** → **Bildirimler**). See [`security.md`](security.md#notifications) and
[`configuration.md`](configuration.md#notification-settings).

**Can I run a command every night or on certain weekdays?**
Yes, with a scheduled task on **İşlemler** (once, every day, or on chosen weekdays, in the server's time
zone). It runs through the normal task queue. See [`dashboard.md`](dashboard.md#zamanlanmış).

**Does POps list installed software and missing Windows updates?**
The server and the **Raporlar** page support it, and admins can ask PCs to scan for or install updates. The data
comes from agents 0.1.5-alpha and later; older agents do not report it. See
[`agent.md`](agent.md#software-inventory-and-windows-updates).

**Can I run the server in Docker?**
Yes, optionally: [`docker.md`](docker.md). The native installation is the primary path.

## Privacy and transparency

**Does POps log keystrokes or record screens?**
No keystroke logging exists. Live screen frames are forwarded to the admin who opened the session and are not
stored. Remote keystrokes are not written to any log; the audit log records that a session was opened, by whom and
why.

**What does the user at the PC see?**
The POps icon in the system tray at all times; a notice when a screen preview is taken (at most every five
minutes, and the tooltip shows the time of the last one); a consent dialog, or for a mandatory session a
full-screen countdown with the reason; the fair-use text if the school sets one; and the lock screen when the PC
is quarantined. There is no hidden mode.

**Can a read-only account watch screens?**
No. Accounts with the `viewer` role receive no previews or live frames and cannot send input.

**Where do I start with KVKK?**
[`kvkk-aydinlatma.md`](kvkk-aydinlatma.md) lists the data POps processes and contains a notice template (in
Turkish) to adapt with your legal adviser.

## Security

**What if the server is compromised?**
An attacker could run commands and open sessions on PCs where those capabilities are enabled, but could not push
a modified agent, because every update must carry a signature the server cannot produce. Install the MSI with
`TERMINAL_ENABLED=0` and/or `VISION_ENABLED=0` where those features are not needed; the server cannot switch them
back on. See [`security.md`](security.md) and [`../SECURITY.md`](../SECURITY.md).

**Can I use a self-signed certificate?**
The agent validates the server certificate with the standard Windows checks and has no option to skip them. A
certificate from a private CA works if that CA is trusted on the PCs.

**Why can't the agent use `http://`?**
The device secret, the enrollment token and the commands the agent runs as SYSTEM would cross the network in clear
text. Plain `http://` is accepted only for a server on the same machine.

**Can page permissions restrict what an admin can do?**
Only in the panel. The API checks roles, so any `admin` can use every admin endpoint. Use the `viewer` role for
read-only people ([`security.md`](security.md#roles)).

**Someone lost their authenticator. How do I reset 2FA?**
Run `UPDATE users SET totp_enabled = false, totp_secret = NULL WHERE username = '<user>';` on the database. The
user can then sign in with the password and set up 2FA again.

## Agents

**Do I need one enrollment token per PC?**
No. A token can be created with several uses (up to 10000) and a lab, so one MSI command line can enroll a whole
lab. Each successful enrollment uses up one use.

**What about machines with Deep Freeze or other reboot-to-restore software?**
Install and enroll while the machine is thawed, then freeze it, so the device secret is part of the frozen image.
Alternatively set `PERSIST_DIR` to a folder that is not rolled back. See [`agent.md`](agent.md#machines-with-freeze-software).

**Does the agent work when nobody is signed in?**
The service does: it stays connected, runs commands and installs updates. The tray, and with it screen view,
remote input and the user notices, runs only in a signed-in session.

**How are agents updated?**
Only with signed MSI releases, from **Sistem**: download and verify the release from GitHub (or upload it),
then send it to all agents, a lab or selected PCs. Each agent verifies the signature again and rolls back if the
new version does not start correctly. See [`agent.md`](agent.md#updates).

**A PC was reinstalled. Will it come back as a new device?**
Usually not. Uninstalling keeps `C:\POpsData` (identity and secret). If the identity file is gone, the server
compares the hardware fingerprint with known devices and gives the agent its previous ID when it matches. If the
secret was lost, enrolling the device again needs a new enrollment token and a one-time re-enrollment
permission ([`troubleshooting.md`](troubleshooting.md#enrollment-and-4401-rejections)).

**Can a student remove the agent?**
Removing it needs administrator rights: the MSI is a per-machine installation with a LocalSystem service, and the
agent's secrets are in a folder only SYSTEM and Administrators can open. The tray has no exit menu item.

## Server

**How do I update the server?**
Run `pops-deploy-backend` from an up-to-date checkout, or enable the panel's one-click self-update. Both check the
new code and restore the previous code if it fails. Do not re-run `install.sh` on an existing server: it
regenerates the secrets. See [`deployment.md`](deployment.md#updating-the-server). The self-update follows the
stable channel (release tags) by default; a test server can follow `main` instead
([`self-update.md`](self-update.md)).

**Where is the API documentation?**
In [`api.md`](api.md), and as an OpenAPI file for `/api/v1` in [`openapi.json`](openapi.json). Scripts and other
systems use `/api/v1` with an API token (**Ayarlar → Güvenlik → API jetonları**). The interactive `/docs` page is
disabled on purpose.

**What should I back up?**
The PostgreSQL database, the backend `.env` and `Backend/storage/` ([`database.md`](database.md#backups)).

**Can I export data to a spreadsheet?**
Yes. **Raporlar** → the download icon (**CSV olarak indir**) exports devices, software, Windows update state, licences or events as CSV
(semicolon-separated, UTF-8 with a byte-order mark so that Excel shows Turkish characters correctly).
