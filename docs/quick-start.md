# Quick Start

From an empty Linux server to a Windows PC that runs a command from the panel. Each step links to the page with
the details. Replace `pops.example.com` with your server's host name.

## 1. Install the server

```bash
git clone https://github.com/PashaCore/POps.git
cd POps
sudo Installer/server/install.sh
```

The script installs PostgreSQL and Python if needed, creates the database, the `.env` with generated secrets and
the `pops.service` unit, and prints the panel **admin password** at the end. Keep it, and keep the clone: the
panel is served from its `Dashboard` folder. Details: [`installation.md`](installation.md).

## 2. Web server and TLS (installed by `install.sh`; this section is for a manual setup)

The backend listens on `127.0.0.1:8000`. Serve the panel and proxy the backend under one HTTPS host name. The
script does not install the web server or PHP; install nginx and PHP 8 with PHP-FPM and the `curl` extension
from your distribution first.

1. Start from [`Installer/server/nginx.example.conf`](../Installer/server/nginx.example.conf): set `server_name`,
   set `root` to the `Dashboard` folder of your clone, and set the PHP-FPM socket. (Apache: see
   [`deployment.md`](deployment.md#apache).)
2. Get a certificate, for example `sudo certbot --nginx -d pops.example.com`.
3. Create the panel configuration:

   ```bash
   cp Dashboard/includes/config.example.php Dashboard/includes/config.php
   ```

4. Check from any machine: `curl https://pops.example.com/api/health` should return `{"status":"ok",…}`.

## 3. Sign in

Open `https://pops.example.com/`, sign in as `admin` with the password from step 1, then on **Ayarlar**:

- change the password (**Kullanıcılar** → click your user → **Şifreyi sıfırla**; you are signed out and sign in
  again with the new password),
- set up **İki adımlı doğrulama**.

## 4. Create an enrollment token

1. Optional: on **Sınıflar** open **Sınıf işlemleri** → **Yeni sınıf…** and create the lab, for example `Lab-1`.
2. On **Sistem** → "Ajan kaydı ve kimlik" click **Jeton üret**, enter the lab (**Sınıf**), a note (**Not**), how many
   PCs may use the token (**Kullanım sayısı**) and its lifetime in hours (**Geçerlilik (saat)**, default 72), then
   confirm with **Jeton üret**.
3. Copy the `ENROLL_TOKEN=…` value.

## 5. Install the agent on a PC

1. Download `POps-Agent-<version>-win-x64.msi` from the
   [GitHub releases](https://github.com/PashaCore/POps/releases). Nothing has to be installed first: the .NET runtime
   comes with the agent (versions before 0.1.15 needed the .NET 8 Desktop Runtime).
2. In an elevated command prompt:

   ```
   msiexec /i POps-Agent-<version>-win-x64.msi /qn /l*v C:\POpsLogs\msi-install.log SERVER_URL=https://pops.example.com ENROLL_TOKEN=<token>
   ```

Optional properties: `BYPASS_SECRET=<the BYPASS_SECRET from the server's .env>` for offline quarantine bypass,
`TERMINAL_ENABLED=0` / `VISION_ENABLED=0` to forbid remote commands or screen view on this PC. All properties:
[`configuration.md`](configuration.md#msi-properties).

## 6. Check that it arrived

- **Cihazlar** lists the PC as online within a few seconds, in the token's lab.
- The signed-in user sees the POps shield icon in the system tray.
- **Sistem** → "Kimlik zorlaması" counts the PC as enrolled.

If the PC does not appear, see [`troubleshooting.md`](troubleshooting.md#a-pc-does-not-appear-or-shows-offline).

## 7. Try it

- **Uzak komut:** choose the PC, type `hostname` and press Enter. The command runs as SYSTEM on the PC and its output
  appears below.
- **Uzak ekran:** pick the lab to see a screen preview; click the PC, turn on **Canlı izle**, choose
  **Kullanıcıya sor**, give a reason and **Oturumu başlat**. The user on the PC is asked to accept.
- **Kayıtlar:** open **Donanım** and find the PC for its hardware inventory.

## 8. Enforce agent authentication

When every PC is enrolled, open **Sistem** → "Kimlik zorlaması", turn the switch on and confirm with **Zorlamayı aç**. From then on
the server rejects agents without a valid device secret or enrollment token.

## Next steps

- Distribute software: [`dashboard.md`](dashboard.md#dağıtım).
- Get notified of failed updates and other problems by e-mail or webhook:
  [`configuration.md`](configuration.md#notification-settings).
- Update agents with signed releases: [`agent.md`](agent.md#updates).
- Enable server updates from the panel: [`self-update.md`](self-update.md).
- Review the security checklist: [`security.md`](security.md#operator-checklist).
