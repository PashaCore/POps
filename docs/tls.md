# TLS: certificates for the server and the agents

POps runs only over HTTPS/WSS: the agent refuses an `http://` server address, and the installer sets up TLS
itself. This page explains the three ways to get a certificate, how agents decide whether to trust the server,
and how to renew and rotate.

## Three ways to get a certificate

| `TLS_MODE` | When | What happens |
|---|---|---|
| `internal` (default) | Server on the school network, no public name, or no internet. | `pops-tls` creates a **school-internal CA** on the server (`/etc/pops/ca/pops-ca.pem`, key root-only, 10 years) and issues the server certificate (825 days). `pops-tls-renew.timer` renews it 30 days before expiry. |
| `letsencrypt` | Public name that points at this server, ports 80/443 reachable from the internet. | `certbot --nginx` fetches a Let's Encrypt certificate; certbot's own timer renews it. If the challenge fails the install continues with the internal CA. Set `LE_EMAIL` for expiry warnings. |
| `existing` | You already have a certificate from your institution or a public CA. | `pops-tls import <cert.pem> <key.pem>` copies it into place. Renewal is yours; run `import` again with the new files. |

`TLS_MODE=none` installs only the backend (for a web server you manage yourself; the site template is
`Installer/server/nginx.pops.conf.in`).

Example: `sudo POPS_DOMAIN=pops.okul.local Installer/server/install.sh`

## How agents trust the server

The agent verifies the server certificate on every connection (command WebSocket, HTTP endpoints, Vision
tunnel, package download):

- **Pinned to the school CA** (`server_ca = custom`): install the agent with
  `SERVER_CA_CERT=<path to pops-ca.pem>`. The file is kept as `C:\POpsData\secure\server-ca.pem` (SYSTEM and
  Administrators only) and the agent accepts a server certificate **only** if it chains to that CA and the name
  matches. A certificate from any other CA, public ones included, is refused. This is the strongest setting: a
  stolen public certificate, or a spoofed DNS name, does not get a connection.
- **Windows trust store** (`server_ca = system`): no `SERVER_CA_CERT`. The agent trusts whatever Windows trusts:
  Let's Encrypt and other public CAs, and the school CA if it was distributed to the machines (for example by
  GPO). `SERVER_CA_CERT=system` on an upgrade removes a previously pinned CA.

The **Sistem** page shows which mode each agent reports (**Cihaz yetenekleri** → the device → "Sunucu sertifikası": "Kurum sertifikası" / "Sistem deposu").
Agents older than 0.1.10 do not report it.

Getting the CA file to the PCs: it is served at `https://<server>/pops-ca.pem` (public data, nothing secret);
check its fingerprint against `pops-tls show` before you trust it. Browsers need it too for the panel: import
`pops-ca.pem` into *Trusted Root Certification Authorities* (per machine, or for the whole school with a GPO).

With Let's Encrypt or a public certificate, do **not** pass `SERVER_CA_CERT`; the agent would reject the
public certificate.

## Day to day

```bash
sudo pops-tls show            # CA fingerprint, server certificate names, days left
sudo pops-tls renew           # renews when fewer than 30 days remain (the timer runs this weekly)
sudo pops-tls renew --force   # renew now
sudo pops-tls init pops.okul.local 10.10.0.5   # reissue with a new name or IP (same CA)
```

The server name must stay the same for the life of the agents: it is baked into every agent's `ServerUrl`.
Choose a DNS name, not an IP, when you install.

## Rotating the school CA

The CA is valid ten years. If its key must be replaced (compromise, or expiry):

1. Move the old `/etc/pops/ca` aside and run `sudo pops-tls init <name>`: a new CA and server certificate.
2. Agents pinned to the old CA will refuse the new server certificate. Before switching nginx to the new
   certificate, redistribute the new `pops-ca.pem`: reinstall or upgrade the agents with
   `SERVER_CA_CERT=<new pops-ca.pem>` while the old certificate is still served (`pops-tls import` the old files
   back if you already switched), then switch.
3. Update browsers and GPOs with the new CA.

Agents in `system` mode are unaffected as long as the new CA is in the Windows trust store.

## Where things are

| Path | Contents |
|---|---|
| `/etc/pops/ca/pops-ca.pem` | School CA certificate (public). Also at `https://<server>/pops-ca.pem`. |
| `/etc/pops/ca/ca.key` | CA private key, root only, never leaves the server. Backed up by `pops-backup` (`/etc/pops`). |
| `/etc/pops/tls/server.crt`, `server.key` | Server certificate and key used by nginx. |
| `/etc/pops/tls/san.txt` | Names the certificate was issued for; `renew` reuses them. |
| `/etc/nginx/conf.d/pops.conf` (`sites-available/pops` on Debian) | The site, rendered from `Installer/server/nginx.pops.conf.in`. |
