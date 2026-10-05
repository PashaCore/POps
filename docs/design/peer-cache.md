# Package downloads: resume and a lab-local peer cache

Status: step 1 done (agent 0.1.23, BITS); step 2 built: server side after 0.1.22-alpha, Windows agent side
after 0.1.22-alpha. The agent's part is specified in [`../agent.md`, "Peer cache contract"](../agent.md#peer-cache-contract).

## Problem

An agent update is a 41 MB self-contained MSI. A lab of 40 PCs downloads it 40 times from the POps server
(1.6 GB per lab, about 80 GB for a 2000-PC school), usually at the same time, over the school's uplink if the
server is off site. An interrupted download started from zero.

## Step 1 (done): BITS with resume

The service downloads the update MSI with BITS (Windows' BitsTransfer module, LocalSystem job named after the
package's SHA-256).
- BITS resumes after network drops and service restarts, using HTTP Range requests.
- A job that does not finish in 15 minutes keeps running, and the next `update_agent` for the same package
  picks it up.
- If BITS cannot be used (module or service missing, the organisation's private CA trusted only by the agent and
  not by Windows, a server without Range support), the agent downloads with HttpClient as before.
- The signed manifest's size and SHA-256 are checked on both paths.

This fixes the restart-from-zero problem. It does not reduce the 40× load.

## Options for step 2

### A. Lab-local cache, served by a PC in the lab
One PC per lab downloads the package from the server, and the others fetch it from that PC over the LAN.
Content is addressed by the SHA-256 in the signed manifest, so a downloader trusts the bytes, not the peer.

- **Works with the current server.** POps runs on Linux behind nginx; nothing on the PCs' side needs Windows
  Server.
- **Savings:** 40 → 1 download from the server per lab.
- **Cost:** The agent gets a small read-only file server, and the server gets staged dispatch and peer hints.

### B. BranchCache
Windows' own peer caching. Rejected:
- **Server side:** It needs a content server that produces BranchCache content information (IIS or a Windows
  file server with BranchCache). nginx on Linux does not.
- **Client side:** Distributed cache mode is not available on every Windows edition, and the school PCs vary.
- **Effort:** It would also need Group Policy configuration in each school.

### C. HTTP Range + ETag only
This is resume support, which BITS already uses (nginx serves Range requests for static files). It does not
reduce the number of downloads.

## Decision: A, server-coordinated

1. **Staged dispatch (server, built).**
   - Only agents that announce `peer_cache` in `X-Agent-Features` take part. For each lab with at least two such
     online targets the server first sends `update_agent` to one of them, the seed: it needs a usable LAN address
     (`X-Agent-Peer-Cache: ip=…`, else the inventory address, else the connection address when it is private and
     not shared), must not already run the target version, and wired PCs (`link=wired`) and the most recently
     seen come first.
   - When the seed reports `update_result` `success` on its new version, the server sends the command to the rest
     of the lab with `"peers": [{"hw_id", "url"}]` (up to three online PCs of the lab that reported success for the
     package less than 110 minutes ago, the seed first, rotated per PC). The trigger was `verified` in the first
     draft; it is the result because the seed's service, and so its cache server, is stopped while `POpsUpdater`
     installs, which is exactly when the rest of the lab would download. `verified` is shown in the panel and
     restarts the seed's time limit.
   - A seed that does not reach `verified` within 10 minutes (`PEER_CACHE_SEED_TIMEOUT_SECONDS`), or a result
     within 10 minutes after it, or that fails, is replaced by the next candidate; after three seeds or with no
     candidate left the rest gets `update_agent` without `peers`.
   - Agents without the feature, PCs without a lab and labs without a candidate are sent the update at once, as
     before. Old agents ignore the field anyway.
   - The setting `update_peer_cache` (Sistem → Ajanlar → "Sınıf içinde eşten dağıt", off by default) switches it
     off; switching it off sends the update to the PCs still waiting for a seed.
   - The rollout state is kept in memory. After a server restart the PCs that were still waiting are not sent the
     update; the next deploy stages them again (PCs already sent are tracked in `pending_updates` as before).
2. **Cache (agent).**
   - After `verified` the package is kept in `C:\POpsData\cache\<sha256>` (SYSTEM/Administrators only).
   - It is kept for 2 hours or until the next update, whichever is sooner, and at most two packages.
   - It is copied there before the install, so the new version serves it after the restart.
3. **Peer server (agent).**
   - While it holds a cached package, the service listens on one port (HttpListener) and answers only
     `GET /pops-cache/<sha256>` for a package it has.
   - The server is read-only, with no listing and at most 4 concurrent transfers (further requests wait up to
     60 seconds for a slot).
   - Default port 8817; the agent may announce another with `X-Agent-Peer-Cache: port=…`.
   - A firewall rule allows the port inbound from the local subnet only.
   - It stops when the cache is empty.
4. **Download order (agent).**
   - Each peer from `peers` in turn, with a short connect timeout, then the POps server.
   - Every source is checked against the signed size and SHA-256. A bad peer costs one retry, never a bad install.
5. **Plain HTTP on the LAN, not HTTPS.**
   - Integrity comes from the signed SHA-256, and the package is public, so TLS would add nothing.
   - Each PC would need a certificate that no other PC could validate anyway.
   - If a school wants it, the URL scheme can be `https` with a self-signed certificate, checked by hash only.

### Risks and limits
- **Attack surface:** A listening port on PCs. It is bounded by the subnet-only firewall rule, the fixed path
  format and the short lifetime, and it is open only during a rollout.
- **Peer hints:** A peer that is switched off mid-transfer is handled by the next peer or the server. A lab where
  every peer is unreachable (client isolation on Wi-Fi) falls back to today's behaviour.
- **Rollout time:** Staged dispatch adds one download time per lab before the rest of the lab starts.

### Work
- **Agent (done):** the feature announcement, cache directory and cleanup, the peer server with its firewall rule,
  `peers` handling in `DownloadVerifiedAsync`, and tests with a fake peer (`PeerCache`, `PeerCacheServer`,
  `PeerDownload`, `PeerCacheTests`). Contract: [`../agent.md`](../agent.md#peer-cache-contract).
- **Server (done):** `Backend/pops/peer_cache.py` (staged dispatch per lab, seed choice and fallback), `peers` in
  `update_agent` from `POST /api/system/deploy-update`, peer state in `update-progress`, the setting
  (`POST /api/system/update-peer-cache`), the protocol schema and example, the panel's per-lab line ("tohum:
  PC-12, doğrulandı; 38 bilgisayara eşten dağıtılıyor"), and `Backend/tests/test_peer_cache.py`.
