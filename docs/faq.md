# FAQ

## General

**Is POps a classroom teaching tool like Veyon?**
No. POps is for the people who run the labs: inventory, deployment, updates, remote support and audit. It can run
next to a classroom tool. See [`positioning.md`](positioning.md).

**Which systems are supported?**
The server runs on Linux with systemd (`install.sh` is tested on AlmaLinux/RHEL/Rocky and Debian/Ubuntu) with
PostgreSQL and Python 3.9 or newer. The agent runs on 64-bit Windows with the .NET 8 Desktop Runtime.

**How many PCs can one server handle?**
The measured baseline is in [`BENCHMARKS.md`](../BENCHMARKS.md): one backend worker comfortably serves a lab to a
few hundred agents. The backend runs as a single worker.

**Is the panel available in English?**
No. The panel and most server messages are in Turkish.

**Does POps work without internet access?**
Mostly. The server checks GitHub for new versions but works without it; agent releases can be uploaded by hand on
**Sistem & Sürüm**; the self-update redeploys the local checkout when it cannot fetch. The 2FA QR code is generated
locally. The panel pages load charts, drag-and-drop, icons and fonts from public CDNs, so those parts need
internet access in the administrator's browser.

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
Only with signed MSI releases, from **Sistem & Sürüm**: download and verify the release from GitHub (or upload it),
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
regenerates the secrets. See [`deployment.md`](deployment.md#updating-the-server).

**Where is the API documentation?**
In [`api.md`](api.md). The interactive `/docs` page is disabled on purpose.

**What should I back up?**
The PostgreSQL database, the backend `.env` and `Backend/storage/` ([`database.md`](database.md#backups)).
