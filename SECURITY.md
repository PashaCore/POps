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
1. **Agent Authentication:** Agents authenticate using an immutable Hardware ID (HWID). The server strictly verifies connections to prevent unauthorized agents from joining a lab.
2. **Dashboard Access:** The Web Panel is secured via JWT (JSON Web Tokens) with strictly enforced Role-Based Access Control (RBAC). 
3. **No Keyloggers:** POps is designed for transparency. We explicitly avoid implementing keystroke logging mechanisms.
4. **Transparent Sessions:** Active `POpsVision` (Remote Desktop) sessions will inherently trigger a visual notification to the active user to ensure privacy and compliance.

## Reporting a Vulnerability

If you discover a security vulnerability within POps, please **DO NOT** open a public issue. 

Instead, please send an email to **security@pashacore.com.tr**. We will triage your report within 48 hours and work with you to understand and mitigate the issue before public disclosure.

When reporting, please include:
* A description of the vulnerability.
* Steps to reproduce the issue.
* Potential impact.

Thank you for helping keep the POps platform secure!
