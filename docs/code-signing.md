# Code signing policy

**Status: policy for Authenticode signing through the SignPath Foundation. The application has not been made yet,
and no POps binary is Authenticode-signed today.** Windows may therefore show a SmartScreen warning for the MSI. The
release packages are already signed with the project's own ed25519 key, and agents verify that signature before
they install an update ([`keys/README.md`](../keys/README.md), [`docs/agent.md`](agent.md#updates)). This page
describes the rules that will apply once Authenticode signing starts.

<!-- When the SignPath Foundation accepts the project, add this line exactly as SignPath requires and remove the
     status paragraph above:
     Free code signing provided by SignPath.io, certificate by SignPath Foundation -->

## What gets signed

Only files that are built from this repository by the release workflow:

| Artifact | Contents |
| --- | --- |
| `POps-Agent-<version>-win-x64.msi` | The agent installer. |
| Executables in the MSI | `POpsAgent.exe` (service), `POpsTray.exe`, `POpsWatchdog.exe`, `POpsUpdater.exe`. |
| Libraries built from this repository | `POps.Shared.dll` and the other assemblies produced by the four agent projects. |
| MSI custom actions | `PopsInstallerActions` (built from `Installer/agent/CustomActions`). WiX wraps it in its own loader; whether the wrapped file is signed as a whole is agreed with SignPath during onboarding. |

The executables and libraries are signed before they are packed, so the MSI and the agent ZIP of the same release
contain the same signed files.

## What is never signed

- Files we did not build: the .NET runtime that ships inside the MSI (signed by Microsoft), NuGet packages such as
  BouncyCastle, and the WiX toolset. They are included as their publishers ship them.
- Anything built outside the release workflow: local builds, pull request builds and the `dev-<commit>` packages
  that `release.yml` builds when it changes on `main`.
- Anything from a tag that is not on `main`, or whose CI tests did not pass.
- The server package (`pops-server-<version>.tar.gz`) and the shell scripts. They are covered by the ed25519-signed
  `manifest.json`, by `SHA256SUMS` and, for server self-update, by SSH-signed release tags
  ([`docs/self-update.md`](self-update.md#sürüm-etiketlerinin-imzası)).
- The Docker images on GHCR. The release workflow pushes them with a build provenance attestation and an SBOM
  ([`docs/docker.md`](docker.md)).
- Packages that administrators upload on the **Dağıtım** page. POps distributes them with a SHA-256 check, but the
  project never signs third-party or school software.
- Tools designed to find or exploit vulnerabilities or to get around security measures. The project does not sign
  such tools.

## How a release is built and signed

1. The release commit (`chore(release): X.Y.Z-alpha`) is on `main`, and `main` is green.
2. The maintainer pushes a tag `vX.Y.Z-alpha`, signed with the release SSH key
   ([`CONTRIBUTING.md`](../CONTRIBUTING.md#release-process)).
3. GitHub Actions runs [`release.yml`](../.github/workflows/release.yml) on GitHub-hosted runners: it builds the
   agent, the MSI and the server package from that tag, and runs the full CI test suite on the same commit.
4. The unsigned agent files are submitted to SignPath as a signing request from that workflow run, so SignPath can
   check that they come from this repository's workflow and this tag.
5. The **approver** reviews the request in SignPath (see [Approval](#approval-of-each-release)) and approves or
   rejects it. Signing happens on SignPath's side.
6. The workflow continues with the signed files: it builds the release manifest, signs it with the ed25519 key,
   writes `SHA256SUMS` and publishes the release.

No one signs on a personal machine, and there is no way to sign outside this workflow.

## Roles

| Role | Who | Responsibility |
| --- | --- | --- |
| Author | Mehmet Ali Avcı ([@TheP4SHA](https://github.com/TheP4SHA)) | Changes the source code; maintains the release workflow. |
| Reviewer | Mehmet Ali Avcı ([@TheP4SHA](https://github.com/TheP4SHA)) | Reviews every pull request from people who are not committers ([`.github/CODEOWNERS`](../.github/CODEOWNERS)). |
| Approver | Mehmet Ali Avcı ([@TheP4SHA](https://github.com/TheP4SHA)), project owner | Approves each signing request. |

The project has one maintainer today. When another maintainer joins, this table is updated before that person gets
any role in SignPath.

## Multi-factor authentication

Everyone with a role above must use multi-factor authentication for GitHub and for SignPath. Before the first
signed release, the PashaCore GitHub organisation is set to require two-factor authentication for all members (see
the [checklist](#before-the-first-signed-release)).

## Approval of each release

Every release is approved by hand; nothing is signed automatically. Before approving, the approver checks that:

- the tag is `v<VERSION>`, is signed with the release SSH key and points at a commit on `main`;
- the request comes from the `release.yml` run of that tag, and its CI tests passed;
- `CHANGELOG.md` has the section for that version;
- the files in the request are the expected ones (names, product name, version).

Releases are planned weekly. A fix for a security problem may get its own release in between; it goes through the
same approval.

## Key custody

The Authenticode certificate and its private key belong to the SignPath Foundation and are kept in SignPath's
hardware security module. The project never receives or stores them.

The project's own keys are separate and unchanged: the ed25519 release key is a secret of the `release` GitHub
environment and never reaches a server or a PC; the SSH key that signs release tags (from 0.1.22-alpha on) is kept
outside the repository ([`keys/README.md`](../keys/README.md#sürüm-etiketlerinin-imzası-ssh)).

## How users verify a download

1. **Authenticode** (once signing has started): right-click the MSI → **Properties** → **Digital Signatures**; the
   signer is the SignPath Foundation. Or in PowerShell:

   ```powershell
   Get-AuthenticodeSignature .\POps-Agent-<version>-win-x64.msi | Format-List Status, SignerCertificate
   ```

   `Status` must be `Valid`.
2. **Release manifest:** compare the file's SHA-256 with `SHA256SUMS` and verify the ed25519 signature of
   `manifest.json` ([`keys/README.md`](../keys/README.md#doğrulama-elle)).
3. **On the PC:** the agent itself verifies every update against the public key compiled into it and installs
   only newer versions ([`docs/agent.md`](agent.md#updates)).
4. **Source:** release tags from 0.1.22-alpha on are SSH-signed:
   `git -c gpg.ssh.allowedSignersFile=keys/allowed_signers verify-tag v<version>`.

## Privacy

This program will not transfer any information to other networked systems unless specifically requested by the user
or the person installing or operating it.

The agent connects only to the POps server set at installation (`SERVER_URL`), which the school runs itself. What it
sends there is listed in [`docs/kvkk-aydinlatma.md`](kvkk-aydinlatma.md) (Turkish) and
[`Agent/README.md`](../Agent/README.md#what-the-agent-reports). Screen view needs the user's consent or shows a notice; there is
no hidden mode and no keystroke logging ([`docs/vision.md`](vision.md)).

## Installing and uninstalling

The MSI is a per-machine installation. It is removed with **Settings → Apps** or `msiexec /x`; the service and the
program files are deleted, while `C:\POpsData` (device identity) and `C:\POpsLogs` are kept on purpose
([`Installer/README.md`](../Installer/README.md)). Changes to the system configuration that an administrator starts
from the panel, such as the firewall rules and lock screen of a quarantine, are shown to the user and undone when
the quarantine ends ([`docs/agent.md`](agent.md#quarantine-and-offline-bypass)).

## Before the first signed release

The approver confirms each of these before submitting the first signing request:

- [ ] The SignPath Foundation accepted the project, and the SignPath line above is added to this page.
- [ ] Every person with a role uses multi-factor authentication, and the organisation requires it.
- [ ] The `release` GitHub environment requires the approver's review.
- [ ] `release.yml` refuses a tag that is not on `main` and submits the signing request.
- [ ] All agent binaries carry the product name and version (`Product`, `Version`) set in
      `Agent/Directory.Build.props`.
- [ ] The release notes stop mentioning the SmartScreen warning.

## References

- SignPath Foundation, conditions for free code signing: <https://signpath.org/terms> (read on 5 October 2026;
  check again before applying).
- [`ROADMAP.md`](../ROADMAP.md): "Code signing: apply to SignPath Foundation".
