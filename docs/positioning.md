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
- **A capability policy** (shipped) that lets a site hard-disable the remote terminal so
  that even a compromised server cannot run code on their PCs — a security *and* sales
  argument a general RMM cannot make.

## Licence: open source, not source-available

POps is licensed under [Apache 2.0](../LICENSE), an OSI-approved open-source licence. A school, a district or an IT
company may run it, change it and pass it on, also as part of a paid service.

Tactical RMM, often named as an open alternative, uses its own Tactical RMM License 1.0. Its source is public, but
the licence says it is not an open-source licence and forbids offering the software as part of a commercial
service (hosting, SaaS, installation services) without written permission. That matters to an IT company or a
district that wants to run the tool for several schools.

## GLPI: integrate, do not compete

Many schools and public institutions keep their asset register and service desk in GLPI. POps does not try to
replace it. POps knows the live state of each Windows PC because its agent reports it; GLPI keeps the register,
loans, contracts and the wider service desk. The plan is an export of POps inventory and helpdesk tickets to GLPI,
so a school does not need a second inventory agent. It is a design note, not a feature yet:
[`integrations/glpi.md`](integrations/glpi.md).

## Pardus, ETAP and Lider Ahenk

Many Turkish state schools run Pardus, and many classroom interactive boards run Pardus ETAP. POps manages only
Windows 10 and 11 today, so a Windows-only POps misses those machines. A Linux agent (Pardus first) is planned.
Integrating with Lider Ahenk, which already manages Pardus machines and ETAP boards centrally, is the other option;
it has not been evaluated yet. See the [roadmap](../ROADMAP.md#linux-agent-pardus-first).

## Do not claim

- Do not claim POps "replaces" classroom-management software; it is not a lesson tool.
- Do not put a device/agent count in marketing until it is measured — see `BENCHMARKS.md`.
- Do not present transparency as only a slogan; it is a concrete, demoable feature set
  (consent banner, per-user activity history, capability policy).
- Do not claim a GLPI export, a Linux or Pardus agent or a Lider Ahenk integration exists; none of
  them does yet.
