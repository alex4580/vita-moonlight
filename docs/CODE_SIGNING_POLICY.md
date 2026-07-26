# Code signing policy

This policy governs public Windows releases built from
<https://github.com/alex4580/vita-moonlight>.

For Windows artifacts that are accepted into and signed through the SignPath
Foundation open-source program:

> Free code signing provided by SignPath.io, certificate by SignPath Foundation.

This statement applies only when Windows reports a valid Authenticode
signature whose signer is **SignPath Foundation**. It does not claim that an
unsigned build, a developer build, or a release made before SignPath
Foundation enrollment is covered by that program.

## Covered artifacts

The SignPath-backed release policy is limited to project-owned Windows
artifacts built from this repository:

- `VitaMoonlight.Host.exe`;
- `Vita-Moonlight-Host-Setup-win-x64.exe`; and
- the uninstaller generated from the source-controlled Inno Setup script.

The portable ZIP is not itself an Authenticode format, but its project-owned
Windows executable may be signed. `moonlight.vpk` is a PlayStation Vita
homebrew package and is not Authenticode-signed; its release identity is
established separately through the release tag, SHA-256 manifest, and GitHub
build-provenance attestation.

Third-party drivers, redistributables, and installers inside the Windows
package are not re-signed with the Vita Moonlight signing identity. Their
upstream signatures remain intact. Their source, licenses, pinned versions,
and hashes are documented in
[Third-party notices](../host/THIRD_PARTY_NOTICES.md).

The repository publishes its project source under the GPL-3.0 license in the
[public LICENSE](https://github.com/alex4580/vita-moonlight/blob/vita/LICENSE);
no alternative project license is included. Bundled open-source dependencies
keep their upstream licenses. The Microsoft Visual C++ Redistributable is
included only as the virtual-display driver's required system runtime, keeps
Microsoft's signature and terms, and is not signed with the Vita Moonlight
identity.

Every project-owned signed binary must identify the product as **Vita
Moonlight Host** and use the same release product version. The generated Inno
Setup uninstaller retains Inno's own file version, so the artifact rule must
enforce its product version rather than requiring every `FileVersion` field to
be identical. SignPath artifact rules must reject an unexpected product name,
product version, file type, or package layout.

## Team roles

Vita Moonlight currently uses a one-maintainer signing team:

| Role | Member | Responsibility |
|---|---|---|
| Authors / committers | [alex4580](https://github.com/alex4580) | Maintains this fork and may modify its source code, build scripts, and repository configuration. |
| Reviewers | [alex4580](https://github.com/alex4580) | Reviews and approves changes proposed by contributors who do not have direct write access. |
| Approvers | [alex4580](https://github.com/alex4580) | Manually decides whether a verified release candidate may be submitted for public code signing. |

A contribution from anyone who is not listed as an Author must be made through
a pull request and approved by a listed Reviewer before merge. Adding or
changing a role requires updating this policy before that person exercises
code-signing authority.

Every person in one of these roles must use multi-factor authentication for
both GitHub and SignPath access. Signing credentials, tokens, certificates,
and approval access must never be shared.

## Release and approval controls

A public SignPath signing request is permitted only when all of the following
are true:

1. The artifact was built by the repository's GitHub Actions release process
   on GitHub-hosted runners from the exact commit identified by the release
   tag.
2. The trusted-build-system and origin checks identify this repository,
   commit, workflow run, and expected release ref.
3. The source, build scripts, dependency pins, and workflow configuration in
   that commit are the complete inputs to the build.
4. Required automated builds and tests passed, and the release checklist
   records the hardware testing that cannot run in CI.
5. The artifact names, product metadata, version, checksums, upstream
   dependency hashes/signatures, and GitHub provenance match the candidate.
6. A listed Approver manually approves that specific signing request.

No locally built, manually substituted, or post-build-modified binary may be
submitted as an ordinary public release. A failed origin, metadata, approval,
or signature check stops publication.

The repository documentation establishes these rules before provider
integration. A release must not be described as SignPath-signed until the
SignPath project, trusted build system, artifact configuration, signing
policy, and manual approval path are active and the resulting signature
verifies successfully.

## Privacy and user control

The project privacy disclosure is in the
[Vita Moonlight privacy policy](../PRIVACY.md). Vita Moonlight does not operate
a telemetry, advertising, crash-upload, or analytics service. Its intended
network activity is the user-directed discovery, pairing, wake, and streaming
communication described in that policy.

The Windows installer warns before making system changes. The
[installation guide](../README.md#install-for-the-first-time) describes those
changes, and the [uninstall guide](../README.md#uninstall) explains how to
remove the project while preserving or explicitly removing shared
dependencies.

## Policy violations

The project will assist SignPath Foundation with investigation and root-cause
analysis of any credible signing-policy complaint. Report a non-sensitive
project concern through
[GitHub issues](https://github.com/alex4580/vita-moonlight/issues). Report a
suspected violation involving a SignPath Foundation signature to
`support@signpath.io` as described in the
[SignPath Foundation conditions](https://signpath.org/terms.html).

This policy must be reviewed whenever signing roles, release artifacts,
provider integration, build infrastructure, network behavior, or privacy
behavior changes.
