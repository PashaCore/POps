# Package downloads: resume and a lab-local peer cache

Status: step 1 done (agent 0.1.23, BITS); step 2 decided here, not built.

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

1. **Staged dispatch (server).**
   - For each lab the server first sends `update_agent` to one online PC.
   - When that PC reports `update_progress` `verified`, the server sends the command to the rest of the lab, with
     `"peers": [{"hw_id", "url"}]` listing up to three online PCs in the lab that have verified the package.
   - Old agents ignore the field.
2. **Cache (agent).**
   - After `verified` the package is kept in `C:\POpsData\cache\<sha256>` (SYSTEM/Administrators only).
   - It is kept for 2 hours or until the next update, whichever is sooner, and at most two packages.
3. **Peer server (agent).**
   - While it holds a cached package, the service listens on one port (HttpListener) and answers only
     `GET /pops-cache/<sha256>` for a package it has.
   - The server is read-only, with no listing and at most 4 concurrent transfers.
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

### Work, when scheduled
- **Agent:** cache directory and cleanup, the peer server with its firewall rule, `peers` handling in
  `DownloadVerifiedAsync`, and tests with a fake peer.
- **Server:** staged dispatch per lab, `peers` in `update_agent`, the protocol schema, and panel display of
  "via peer".
