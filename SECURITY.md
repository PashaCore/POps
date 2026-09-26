# Security Policy

## Supported Versions

Currently, POps is in active development. Security updates are applied to the `main` branch and the latest tagged release.

| Version | Supported          |
| ------- | ------------------ |
| 0.1.x   | :white_check_mark: |
| < 0.1   | :x:                |

## Security Architecture & Threat Model

POps handles privileged access on endpoints (running as SYSTEM/Administrator) and must be treated with the highest security standards. 

Key security guarantees in POps:
1. **Agent Identity:** Agents identify themselves with a hardware ID (HWID) stored in `C:\POpsData`, which only SYSTEM and Administrators can modify. Agent connections are not yet authenticated with per-device credentials (planned for v0.2, device enrollment). Until then, allow the agent port or the `/ws/agent` endpoint only from your lab networks.
2. **Dashboard Access:** The Web Panel is secured via JWT (JSON Web Tokens) with strictly enforced Role-Based Access Control (RBAC). 
3. **No Keyloggers:** POps is designed for transparency. We explicitly avoid implementing keystroke logging mechanisms.
4. **Transparent Sessions:** A `POpsVision` (remote screen) session asks the logged-in user for consent, or, for a mandatory session started by an admin with a written reason, shows a countdown and a notice. Every screen preview updates the tray icon's tooltip with the time and shows a notification at most every five minutes. The tray icon is always visible; there is no hidden mode.

## Reporting a Vulnerability

If you discover a security vulnerability within POps, please **DO NOT** open a public issue. 

Instead, please send an email to **security@pashacore.com.tr**. We will triage your report within 48 hours and work with you to understand and mitigate the issue before public disclosure.

When reporting, please include:
* A description of the vulnerability.
* Steps to reproduce the issue.
* Potential impact.

Thank you for helping keep the POps platform secure!
