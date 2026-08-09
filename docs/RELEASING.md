# Releasing a fork

This fork can be released independently. A merge into the original upstream
repository is not required.

## Code signing policy

Every public download or GitHub release page must use the term **Code signing
policy** and link to the repository's
[Code signing policy](CODE_SIGNING_POLICY.md).

The matching disclosure is the repository's [Privacy policy](../PRIVACY.md).

For a Windows artifact that actually verifies with SignPath Foundation as its
Authenticode signer:

> Free code signing provided by SignPath.io, certificate by SignPath Foundation.

Do not use that statement to describe unsigned developer builds or a release
made before SignPath enrollment and provider integration are complete. Team
roles, approval rules, artifact scope, third-party boundaries, and incident
handling are defined in the linked policy.

## Release trust model

A public Windows package can be the exact explicitly unsigned beta preview or
an Authenticode-signed release. It has these independent trust layers:

1. **Authenticode**, when configured, signs `VitaMoonlight.Host.exe`, the
   setup EXE, and the Inno-generated uninstaller. Stable tagged builds fail
   closed if signing is unavailable or verification fails. The exact
   `v0.14.8-beta.1` preview is the only exception: it may publish without
   Authenticode only when the workflow verifies that the project-owned
   executables are actually unsigned and labels the release prominently.
2. The bundled display driver and third-party installers retain their vendor
   signatures and are downloaded at pinned SHA-256 hashes.
3. GitHub generates a signed build-provenance attestation for the VPK, setup
   EXE, portable ZIP, complete Vita source archive, dependency/signing
   manifests, and checksum manifest. This covers artifacts that do not carry
   Windows Authenticode signatures.
4. The VPK workflow starts from a pinned, hash-checked VitaSDK and rebuilds
   every linked client dependency from official source archives or exact Git
   commits with the GPL-3.0-or-later project recipe and patches. The single
   `vitasdk-dependencies.json` manifest is bound to the SHA-256 and schema of
   `tools/vita-corresponding-source.lock.json`. Release staging compares its
   complete dependency recipe with that lock and requires a nonempty source
   record plus valid hashes and sizes for every required installed output.
5. The tag workflow must build and attest a `*-Vita-Source.tar.gz` asset. It
   exports the exact tag and every submodule, verifies all upstream archives,
   and includes the pinned newlib, pthread, GCC runtime, Vita headers, and Vita
   toolchain sources recorded by the SDK. `--require-complete` fails before a
   draft is created if any source input, project recipe, patch, runtime source,
   submodule, or redistribution status is incomplete. Do not publish the VPK
   without this matching archive or substitute GitHub's generic source ZIP.

The Vita VPK is a homebrew package, not a Windows PE file, so Authenticode does
not apply to it. Its release identity is established by the release tag,
published SHA-256 checksum, and GitHub provenance attestation. Stable releases
also require GitHub to verify the cryptographic signature on the annotated
tag. Only the exact `v0.14.8-beta.1` preview may use an unsigned annotated tag,
and its tag and Windows status are labeled as unsigned.

## Configure Windows code signing

The current workflow accepts a password-protected PFX through these repository
secrets:

- `WINDOWS_CERTIFICATE_BASE64`
- `WINDOWS_CERTIFICATE_PASSWORD`
- `WINDOWS_EXPECTED_SIGNER_SUBJECT`

The certificate must contain a code-signing key and a chain suitable for the
intended audience. Set the expected signer subject to the exact
`SignerCertificate.Subject` value that the verified release certificate must
produce. A self-signed certificate is useful only for private testing; it
does not make a public download trusted by Windows.

Modern publicly trusted certificates are commonly held in hardware or a
managed signing service and may not be exportable as a PFX. If that is how the
publisher receives its certificate, replace the PFX steps in
`.github/workflows/windows-host.yml` with that provider's supported signing
action before creating a tag. Microsoft documents Azure Artifact Signing as
its recommended managed option for non-Store Windows distribution:

- <https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/code-signing-options>
- <https://learn.microsoft.com/en-us/azure/artifact-signing/how-to-signing-integrations>

Open-source projects can also evaluate a managed program such as SignPath
Foundation. Provider enrollment and identity validation belong to the
publisher and cannot be embedded in this repository.

Encode a PFX locally without printing it:

```powershell
[Convert]::ToBase64String(
  [IO.File]::ReadAllBytes("C:\secure\publisher-certificate.pfx")
) | Set-Clipboard
```

Paste the clipboard value directly into the GitHub secret. Never commit a
certificate, password, token, or generated signing wrapper.

## Candidate process

1. Update the version with `release.py`, finish `CHANGELOG.md`, and include a
   **Code signing policy** link in that version's notes so the generated GitHub
   release page preserves the required disclosure.
2. Push the candidate branch and require both Vita and Windows workflows to
   pass.
3. Run `host/BETA_SMOKE_TEST.md` on the primary PC/Vita and then on the laptop.
   Record the exact commit and workflow URLs.
4. For a final release, complete `host/END_TO_END_TEST.md` and
   `host/FINAL_RELEASE_CHECKLIST.md`.
5. Confirm the publisher signing secrets, expected signer subject, and
   managed-provider integration are ready. The only exception is the exact
   `v0.14.8-beta.1` unsigned preview.
6. Ordinarily, create a signed annotated tag on the tested commit and confirm
   GitHub shows it as **Verified**. For that exact preview only, create
   this unsigned annotated tag:

   ```sh
   git tag -a v0.14.8-beta.1 TESTED_COMMIT_SHA \
     -m "Vita Moonlight 0.14.8 beta 1"
   git push fork v0.14.8-beta.1
   ```

   Every other beta, release-candidate, and stable tag must instead use
   `git tag -s`, and its signature must be registered and shown as
   **Verified** by GitHub.

7. The tag workflow rebuilds both products, verifies the selected signing
   state, builds the complete Vita source archive, creates `SHA256SUMS`, attests
   every release asset, and creates a draft release. Preflight rejects
   lightweight tags, every other unsigned tag, incomplete signing or source
   configuration, and any existing draft or release for the same tag.

If final draft verification fails, inspect the retained draft and workflow
logs. After correcting the cause, delete only that draft with
`gh release delete TAG --yes` and rerun the workflow. Do not use
`--cleanup-tag`; the annotated tag must remain attached to the reviewed
commit.

## Verify a published release

Download every asset from the same release. In PowerShell:

```powershell
Get-FileHash .\Vita-Moonlight-Host-Setup-win-x64.exe -Algorithm SHA256
Get-AuthenticodeSignature .\Vita-Moonlight-Host-Setup-win-x64.exe |
  Format-List Status,StatusMessage,SignerCertificate,TimeStamperCertificate
```

Verify `SHA256SUMS`, then verify GitHub provenance with an authenticated GitHub
CLI:

```powershell
gh attestation verify .\Vita-Moonlight-Host-Setup-win-x64.exe `
  --repo OWNER/vita-moonlight
gh attestation verify .\moonlight.vpk --repo OWNER/vita-moonlight
```

Install the downloaded setup package, open
`C:\Program Files\Vita Moonlight Host`, and verify the embedded host executable
with `Get-AuthenticodeSignature` as well. For a signed release, the expected
status is `Valid`, and the generated uninstaller is signed during the Inno
build. For the explicitly unsigned `v0.14.8-beta.1` preview, the expected
status is `NotSigned`; the release page and `windows-signing-status.json` must
say `unsigned-beta-preview` and identify the same tag and commit.

Do not publish when the observed signing state differs from the declared
state, checksums differ, or provenance verification is missing. A signed
release must also have the expected legal publisher name and a valid
timestamp. For a SignPath-covered release, the expected Authenticode
publisher is **SignPath Foundation**.
