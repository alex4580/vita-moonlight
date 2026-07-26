# Contributing

Thanks for improving Vita Moonlight. This fork includes both the Vita client
and the Windows host companion, so changes should preserve the full
connect/stream/disconnect/recovery lifecycle.

## Before filing an issue

Read `README.md`, `docs/VITA_SETTINGS_GUIDE.md`, and
`host/BETA_SMOKE_TEST.md`. Search open and closed issues, then use the GitHub
bug-report form so reports include the Vita, Windows, host, GPU, network, and
diagnostic context needed to reproduce the problem.

Optional Vita support logging is off by default. Choose **Start support log**
only immediately before a reproduction, choose **Stop and save support log**
afterward, and review the file before posting it. It records structured
system, configuration, connection, decoder, motion-state, and periodic network
events rather than every touch or input sample.

## Pull requests

- Base work on the current `vita` branch and keep changes focused.
- Do not commit build output, downloaded installers, local configuration,
  pairing data, logs, IP addresses, certificates, or credentials.
- Preserve unrelated Sunshine applications and Windows display state.
- Keep the Vita client usable without the overlay, diagnostics screen, or
  support logging enabled.
- Update user documentation and the acceptance test when behavior changes.
- Explain hardware-only validation that remains outstanding.

For Windows-host changes, run:

```powershell
dotnet format host\VitaMoonlight.Host\VitaMoonlight.Host.csproj --verify-no-changes
dotnet build host\VitaMoonlight.Host\VitaMoonlight.Host.csproj -c Release
dotnet run --project host\VitaMoonlight.Host\VitaMoonlight.Host.csproj -c Release --no-build -- self-test
```

Vita changes require VitaSDK and the dependencies listed in
`.github/workflows/cmake-psvita.yml`. The GitHub Vita workflow is the canonical
clean build, but it does not replace testing on a physical Vita. Complete the
relevant sections of `host/END_TO_END_TEST.md`; release candidates must also
pass `host/FINAL_RELEASE_CHECKLIST.md`.

Full local toolchain and fork instructions are in `docs/BUILDING.md`. Release
versioning, code signing, checksums, and provenance are documented in
`docs/RELEASING.md`.
