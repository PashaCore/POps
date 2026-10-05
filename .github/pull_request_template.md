## Description

What does this change do, and why? Link the issue it fixes.

Fixes # (issue)

## Type of change

- [ ] Bug fix (non-breaking change that fixes an issue)
- [ ] New feature (non-breaking change that adds functionality)
- [ ] Breaking change (existing setups need an upgrade step)
- [ ] Documentation only
- [ ] CI, tooling or tests only

## How it was tested

Which test scripts or commands did you run? For agent changes, on which Windows version? For changes to the updater
or the MSI, was the rollback drill run ([`Agent/README.md`](https://github.com/PashaCore/POps/blob/main/Agent/README.md#rollback-drill))?

## Checklist

- [ ] **Tests:** new behaviour has a test in `Backend/tests/` or `Agent/POps.Tests` where possible, and the existing
      suites pass ([`CONTRIBUTING.md`](https://github.com/PashaCore/POps/blob/main/CONTRIBUTING.md#checks-and-tests)).
- [ ] **Lint:** `flake8 Backend/ tools/ assets/readme/` reports nothing; PHP files pass `php -l`.
- [ ] **Docs:** endpoints, settings, tables and security behaviour are updated in `docs/` (and `.env.example` for a
      new setting).
- [ ] **CHANGELOG:** a user-visible change has an entry under `## [Unreleased]` in `CHANGELOG.md`.
- [ ] **No secrets:** no passwords, tokens, keys, real server addresses or data from real devices in the code,
      tests, logs or screenshots; examples use `pops.example.com`.
- [ ] **Security-sensitive area** ([list](https://github.com/PashaCore/POps/blob/main/CONTRIBUTING.md#security-sensitive-areas)): if touched, the change says
      so above and has a `### Security` CHANGELOG entry when behaviour changes.
- [ ] **Transparency:** nothing becomes hidden from the person at the PC (no stealth mode, no keystroke logging).
