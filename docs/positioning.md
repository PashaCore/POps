# POps vs. Veyon (and other tools) — Positioning

If you deploy POps in schools and computer labs, the first thing you will hear is
**"we already use Veyon."** This page exists so that answer becomes a conversation
instead of a wall. Read it before writing marketing copy or talking to a school.

## The one-sentence difference

> **Veyon is for the *teacher* running a *lesson*. POps is for the *IT/lab administrator* running the *lab*.**

They overlap on "see a student's screen," but they solve different jobs and can run
side by side on the same machine.

## Who does what

| | **Veyon** (open source) | **NetSupport School** (commercial) | **POps** |
|---|---|---|---|
| Primary user | Teacher, during class | Teacher, during class | IT / lab administrator |
| Core job | Manage a live lesson | Manage a live lesson | Operate the fleet |
| Screen view / broadcast / lock | ✅ (the whole point) | ✅ | ✅ (as an admin/support tool, with consent + audit) |
| Software deployment to a lab | ❌ | limited | ✅ (signed, staged, retriable) |
| Hardware/software inventory | ❌ | limited | ✅ |
| Signed over-the-air agent updates | ❌ | vendor-managed | ✅ (ed25519, verify-before-run) |
| Tamper-aware audit log | ❌ | ❌ | ✅ (and on the roadmap: append-only) |
| Reboot-to-restore (Deep Freeze) awareness | ❌ | ❌ | ✅ (designed for it) |
| Runs headless / all the time | as a service | as a service | as a service, fleet-wide |
| Transparency to the end user | minimal | minimal | **by design** (consent, notice, always-visible tray, per-user history) |

## What this means in practice

- A teacher opens Veyon **when a lesson starts** to watch/lock/broadcast, then closes it.
- An IT admin uses POps **all the time** to know what hardware is where, push "install
  Python to Lab B," update the agent fleet safely, prove who did what, and give remote
  support — including on machines wiped every reboot by Deep Freeze.

So the honest pitch is **not** "POps replaces Veyon." It is:

> **POps is the operations layer under the classroom.** Keep Veyon for teaching if you
> like it — POps handles deployment, inventory, updates, identity and audit, and it is
> transparent to the people being managed.

"Complements Veyon" is a legitimate and often *stronger* position than "beats Veyon,"
because it removes the reflexive objection and sells to a *different budget* (IT
operations, not classroom software).

## Why not just MeshCentral / general RMM?

General remote-management tools (MeshCentral, RustDesk, commercial RMM) are horizontal
and built for hidden administration. POps is **vertical for education/labs** and its
differentiators are exactly the things a general RMM does not care about:

- **Transparency by design** (a hidden RMM is a liability under KVKK when the subjects
  are minors — see `kvkk-aydinlatma.md`).
- **Reboot-to-restore awareness** — the assumption that breaks most tools in a Turkish
  school lab (frozen machines) is a first-class concept in POps.
- **A capability policy** (roadmap) that lets a site hard-disable the remote terminal so
  that even a compromised server cannot run code on their PCs — a security *and* sales
  argument a general RMM cannot make.

## Do not claim

- Do not claim POps "replaces" classroom-management software; it is not a lesson tool.
- Do not put a device/agent count in marketing until it is measured — see `BENCHMARKS.md`.
- Do not present transparency as only a slogan; it is a concrete, demoable feature set
  (consent banner, per-user activity history, capability policy).
