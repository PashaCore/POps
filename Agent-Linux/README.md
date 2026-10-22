# POps Linux agent (Pardus / Debian)

The Linux agent connects a lab PC running Pardus or Debian to the POps server. It is the first version: inventory
and remote commands. It speaks the same `/ws/agent` protocol as the Windows agent, uses the same enrollment tokens,
capability policy and signed releases, and appears in the panel as a **Linux** device. Why it is written in Python:
[`docs/decisions.md` D-22](../docs/decisions.md#d-22-the-linux-agent-is-python-3-on-the-distributions-own-packages).

## What works and what does not

| Works | Not in this version |
| --- | --- |
| Enrollment with a token, per-device secret, clone detection after disk imaging | Screen view and remote control (Vision) |
| Status, health summary, signed-in user (systemd-logind) | Quarantine (lock screen, network isolation), offline bypass codes |
| Hardware inventory (CPU, RAM, board, GPU, OS, IP, MAC, disks) | Tray icon, notices to the user, fair-use notice, help desk |
| Installed packages from `dpkg` (libraries left out) | Windows Update (the panel shows "—") |
| Remote commands as root with `/bin/sh`, timeout, cancel, exit code | DNS policy alerts |
| The panel's restart and shut-down buttons, also from schedules | The panel's **Mesaj gönder**, **Oturumu kapat** and **Kilitle** (refused with -8: the agent does not announce `message` or `power`) |
| Capability policy (terminal on/off, the server can only switch off) | |
| Wake-on-LAN relay for other PCs | |
| Signed self-update with rollback | |
| Local append-only audit log | |

The server does not quarantine a Linux PC: **Karantinaya al** answers `409` ("Linux ajanında karantina henüz yok")
and nothing is marked as locked. Other requests for something that is not in this version (screen view, Windows
Update scan) are answered by the agent with `capability_denied` and reason `not_supported`: the panel shows them as
refused and a notification says the agent does not support them. A `lockdown` that reaches the agent anyway (sent
before the server knew the platform) is refused the same way, and the server then clears the quarantine flag.

Known limitations of this version:

- It has run in Debian 12 containers and on CI (Ubuntu 24.04), not yet on a Pardus lab PC.
- A dual-boot PC whose Windows side is already enrolled has the same DMI identity: the server recognises the
  hardware and refuses the Linux agent's enrollment token (a superadmin would have to allow re-enrollment, which
  moves the identity). Use one agent per physical PC for now.
- The heartbeat carries a `system` block (CPU, memory and root disk use, uptime, load) that the server does not store
  yet; the active window is not reported (`-`).
- The panel's **Mesaj gönder**, **Oturumu kapat** and **Kilitle** are refused by the server with -8, and nothing is
  sent to the PC: this agent does not announce `message` or `power` in `X-Agent-Features`. **Kapat** and
  **Yeniden başlat** still work through the old shutdown command.

## Requirements

- Debian 12 or Pardus 23 and later, or Ubuntu 24.04: `python3` (3.9+), `python3-websockets` (10 or newer) and
  `python3-cryptography`, all from the distribution. No virtual environment and no `pip`. Ubuntu 22.04 is not
  supported (its `python3-websockets` 9.1 does not work with its Python 3.10).
- systemd (the service and the self-update use it).
- Outbound HTTPS to the POps server, with the WebSocket upgrade allowed.

The package is `Architecture: all` (about 45 KB): pure Python, tested on amd64.

## Install

1. In the panel, **Sistem** → "Ajan kaydı ve kimlik": create an enrollment token (**Jeton üret**) for the lab.
2. On the PC (or through your deployment tool), with the `.deb` from the GitHub release:

   ```
   sudo apt install ./pops-agent_<version>_all.deb
   sudo pops-agent configure --server https://pops.okul.local --token <token>
   ```

   If the server uses the school's own certificate authority (`pops-tls`, see [`docs/tls.md`](../docs/tls.md)),
   copy its `pops-ca.pem` to the PC and add `--ca /etc/pops-agent/server-ca.pem`. The agent then trusts **only**
   that CA for the server, not the system store. The file must be owned by root and not writable by others.

3. The PC appears on **Cihazlar** within seconds as **Linux**, in the token's lab. `sudo pops-agent status` shows
   the hardware ID, whether it is enrolled, the capabilities and the last update.

`configure` writes `/etc/pops-agent/agent.conf` (root, `0600`) and restarts the service. You can also write the file
yourself:

```
SERVER_URL=https://pops.okul.local
ENROLL_TOKEN=<token>
SERVER_CA_CERT=/etc/pops-agent/server-ca.pem
```

`SERVER_URL` must be `https://`; plain `http://` is accepted only for a server on the same PC (`127.0.0.1`,
`localhost`), as on Windows, because the device secret and the commands would otherwise cross the network in clear
text. Once the server has issued the device secret the agent removes the token from `agent.conf`.

### Lab images (Clonezilla and similar)

Prepare the image after installing the package and before enrolling, or run `sudo pops-agent generalize` on the
reference PC before taking the image: it stops the service and removes the hardware ID, the device secret and the
other per-device files. Put a lab token in `ENROLL_TOKEN` so every PC enrolls itself when the image first boots.
If an image was taken from an enrolled PC anyway, each copy notices at start-up that the secret belongs to other
hardware, moves it to `/var/lib/pops-agent/clone-<time>/` and enrolls as a new device with the token (the local audit
log records it).

## Files

| Path | Contents |
| --- | --- |
| `/usr/lib/pops-agent/pops_agent/` | The agent's code (from the package). |
| `/usr/bin/pops-agent` | Command and service entry point (`python3 -I`). |
| `/lib/systemd/system/pops-agent.service` | The systemd unit. |
| `/etc/pops-agent/agent.conf` | Server, enrollment token, CA file (root, `0600`; the directory is `0700`). |
| `/etc/pops-agent/capabilities.conf` | The PC's capability policy (`TERMINAL_ENABLED=1` or `0`); a package conffile, kept on upgrade. |
| `/var/lib/pops-agent/` | Hardware ID, device secret, hardware binding, results waiting for the server's acknowledgement, the server's capability switch-off, update state and `packages/` (root, `0700`; files `0600`). |
| `/var/log/pops-agent/agent.log` | The log, rotated daily and at 10 MB; files older than 30 days, and the oldest beyond 200 MB, are deleted. The same lines go to journald (`journalctl -u pops-agent`). |
| `/var/log/pops-agent/audit.log` | The local audit log (below). |
| `/var/log/pops-agent/updater.log` | What the last self-updates did. |

Secrets (device secret, enrollment token) and command texts are never written to the logs.

## Commands, limits and the capability policy

Commands from **Uzak komut**, **Dağıtım** and schedules run with `/bin/sh -c` as root, in `/`, with a clean
environment (`PATH`, `HOME=/root`, `LANG=C.UTF-8`, `DEBIAN_FRONTEND=noninteractive`) and no input. The limits are the
Windows agent's: 30 minutes (then the whole process group is killed and the task is **Zaman aşımı**), 512 K
characters of output and 128 K of error output, read with the cap so a chatty command cannot fill the memory. The
exit code is returned; a non-zero code makes the task **Başarısız**. **İptal** kills the process group. Results stay
in `/var/lib/pops-agent/pending-results.json` until the server confirms it stored them, so a result is not lost when
the connection drops.

The panel's restart and shut-down buttons (and schedules) send the Windows commands `shutdown /r /f /t N` and
`shutdown /s /f /t N`; the Linux agent runs exactly these two as `systemctl reboot` / `systemctl poweroff` after
N seconds, so the result reaches the server first. Any other Windows command fails as it would in `sh`.

Capability policy, as on Windows: the PC decides, the server can only switch off.

- `TERMINAL_ENABLED=0` in `/etc/pops-agent/capabilities.conf` (then `sudo systemctl restart pops-agent`) switches
  remote commands off on this PC. A missing file means on; a value the agent does not understand means off.
- The server's switch-off (**Sistem** → "Cihaz yetenekleri", `POST /api/system/set-capabilities`) is stored in
  `/var/lib/pops-agent/capabilities.state.json` and survives restarts and updates. The server cannot switch it on
  again; a local administrator can: `sudo pops-agent capabilities --reset`.
- A refused command returns `[REDDEDİLDİ] …` with exit code `-5` and the task becomes **Reddedildi**.
- The lab's module settings are honoured as on Windows (for example **Uzak komut** off for a lab).

## Signed self-update and rollback

A superadmin stages a signed release on **Sistem** (download from GitHub, or upload `manifest.json`,
`manifest.json.sig` and the packages) and sends it to all agents, a lab or selected PCs. Each agent takes its own
package from the signed manifest (Windows the MSI, Linux `pops-agent_<version>_all.deb`); PCs whose platform has no
package in the release are skipped (`skipped_no_package`). The Linux agent:

1. verifies the manifest's ed25519 signature with the release key built into the agent
   (`keys/pops_release_ed25519.pub.pem`) and refuses a version that is not newer than the installed one;
2. takes exactly one artifact named `pops-agent_<manifest version>_all.deb` with its size and SHA-256 from the
   signed manifest, downloads it only from its own server's `/updates/`, and checks size and SHA-256;
3. keeps the installed version's `.deb` in `/var/lib/pops-agent/packages/` for the rollback (if it is not there, it
   rebuilds it from the installed files with `dpkg-deb`, after `dpkg --verify` shows they are unchanged);
4. starts the installer in a transient systemd unit (`systemd-run --unit pops-agent-update`), so the service restart
   during `dpkg -i` does not kill it. The installer runs `dpkg -i` (local conffiles are kept), waits up to 90 seconds
   for the new version to report health (`/var/lib/pops-agent/health.json`), and if it does not, installs the
   previous `.deb` again and waits for that. It then makes sure the agent is installed and running;
5. the new (or restored) agent sends the result to the server as `update_result` in the Windows schema (`status`
   `success`, `rolled_back`, `rollback_failed`, `install_failed`, `rejected`; `rollback`, `detail`, `agent_state`,
   `running_version`) and keeps it until the server acknowledges it.

Nothing is installed before every check has passed. The rollback can be rehearsed like on Windows: create
`/var/lib/pops-agent/rollback-drill` (root) before sending an update; the new version then does not report health
and the installer rolls back. `packages/` keeps the installed and the previous version.

The Debian package version uses `~` for pre-releases (`0.1.22~alpha`), so dpkg orders `0.1.22~alpha` before
`0.1.22`; the file name keeps the POps version (`pops-agent_0.1.22-alpha_all.deb`).

## Local audit log

`/var/log/pops-agent/audit.log` records the administrative actions on this PC independently of the server: commands
started and finished (task, SHA-256 and length of the command, who requested it, exit code, duration), refusals,
capability changes, enrollment, identity changes, update start and result, authentication and clone rejections.
Each line is a JSON object that carries the hash of the previous line; `sudo pops-agent audit-verify` reports a
deleted or edited line. The file gets the append-only attribute (`chattr +a`), so even root has to remove the
attribute before lines can be removed. Every entry also goes to journald.

## The systemd service: what is and is not hardened

The service exists to run administrators' commands as root, so the sandboxing options that would break `apt`,
`systemctl`, user management or file changes are **not** used: no `ProtectSystem`, `ProtectHome`, `PrivateTmp`,
`NoNewPrivileges`, system call filter or capability bounding. What is hardened:

- `Restart=always`; `KillMode=mixed` gives the agent time to stop running commands and save their results;
- `LimitCORE=0` (no core dump with the device secret in it), `LockPersonality`, `RestrictRealtime`,
  `KeyringMode=private`;
- the state, log and configuration directories are created by systemd with modes `0700`, `0750` and `0700`;
- the code is package files owned by root; the agent runs `python3 -I` (no `PYTHON*` variables, no user
  site-packages) and does not write byte-code;
- TLS 1.2+ with host name checks, optionally pinned to the school's CA; HTTP requests do not follow redirects;
- commands get a clean environment and no input; the server can switch remote commands off but never on.

## Uninstall

```
sudo apt remove pops-agent      # stops the service; keeps /etc/pops-agent, /var/lib/pops-agent and the logs
sudo apt purge pops-agent       # also removes the configuration, the device secret and the logs
```

Delete the device in the panel as well; a purged PC that is installed again needs a new enrollment token.

## Building and testing

```
python3 Agent-Linux/build_deb.py                 # dist/pops-agent_<VERSION>_all.deb, without dpkg
SOURCE_DATE_EPOCH=… python3 Agent-Linux/build_deb.py --version 0.1.22-alpha --out /tmp/out
python3 -m pytest Agent-Linux/tests              # no root needed; python3-pytest on Debian
```

The build is reproducible: the same source and `SOURCE_DATE_EPOCH` (default: the last commit's time) give the same
bytes. The unit tests use fake `/sys` and `/proc` trees, a mock server for the protocol and fake `dpkg` / `systemctl`
for the installer. `tests/test_integration.py` runs the agent from the source tree against a running backend
(`POPS_IT_URL`, `POPS_IT_CA`, `POPS_IT_ADMIN_PASS`); CI runs it against a backend over TLS with a `pops-tls` CA. For
development the agent can run from the source tree with its own directories:

```
./Agent-Linux/pops-agent --config-dir /tmp/pa/etc --state-dir /tmp/pa/state --log-dir /tmp/pa/log run --verbose
```
