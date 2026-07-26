# Releasing a fork

This fork can be released independently. A merge into the original upstream
repository is not required.

## Release trust model

A public Windows package has three separate trust layers:

1. **Authenticode** signs `VitaMoonlight.Host.exe`, the setup EXE, and the
   Inno-generated uninstaller. Tagged builds fail closed if the configured
   signing step cannot sign or verify them.
2. The bundled display driver and third-party installers retain their vendor
   signatures and are downloaded at pinned SHA-256 hashes.
3. GitHub generates a signed build-provenance attestation for the VPK, setup
   EXE, portable ZIP, and checksum manifest. This covers artifacts such as the
   VPK and ZIP that do not carry Windows Authenticode signatures.

The Vita VPK is a homebrew package, not a Windows PE file, so Authenticode does
not apply to it. Its release identity is established by the signed Git tag,
published SHA-256 checksum, and GitHub provenance attestation.

## Configure Windows code signing

The current workflow accepts a password-protected PFX through these repository
secrets:

- `WINDOWS_CERTIFICATE_BASE64`
- `WINDOWS_CERTIFICATE_PASSWORD`

The certificate must contain a code-signing key and a chain suitable for the
intended audience. A self-signed certificate is useful only for private
testing; it does not make a public download trusted by Windows.

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

1. Update the version with `release.py`, finish `CHANGELOG.md`, and commit.
2. Push the candidate branch and require both Vita and Windows workflows to
   pass.
3. Run `host/BETA_SMOKE_TEST.md` on the primary PC/Vita and then on the laptop.
   Record the exact commit and workflow URLs.
4. For a final release, complete `host/END_TO_END_TEST.md` and
   `host/FINAL_RELEASE_CHECKLIST.md`.
5. Confirm the publisher signing secrets or managed-provider integration is
   ready.
6. Create a signed annotated tag on the tested commit. A beta suffix causes
   GitHub to publish a prerelease:

   ```sh
   git tag -s v0.14.6-beta.1 TESTED_COMMIT_SHA
   git tag --verify v0.14.6-beta.1
   git push fork v0.14.6-beta.1
   ```

7. The tag workflow rebuilds both products, verifies Authenticode, creates
   `SHA256SUMS`, attests the assets, and publishes the release. Preflight
   rejects lightweight tags and any annotated tag whose signature GitHub does
   not verify. It also refuses to overwrite an existing draft or published
   release for the same tag.

Use the publisher's own signing key for the Git tag. If no GPG or SSH signing
identity is configured, configure and verify one before tagging. Register the
corresponding public key as a GitHub signing key so the tag is shown as
**Verified**; do not create an unsigned public-release tag merely to make the
workflow run.

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
with `Get-AuthenticodeSignature` as well. The generated uninstaller is signed
during the Inno build and verified before packaging.

Do not publish the release if the expected legal publisher name, a valid
timestamp, the checksums, or either provenance verification is missing.
