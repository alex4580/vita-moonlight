# Contributing

Thanks for improving Vita Moonlight. This fork includes both the Vita client
and the Windows host companion, so changes should preserve the full
connect/stream/disconnect/recovery lifecycle.

## Before filing an issue

Read `README.md`, `docs/VITA_SETTINGS_GUIDE.md`, and
`host/END_TO_END_TEST.md`. Search open and closed issues, then use
`ISSUE_TEMPLATE.md` so reports include the Vita, Windows, host, GPU, network,
and diagnostic context needed to reproduce the problem.

Optional Vita file logging is off by default. Enable it only for the
reproduction, disable it afterward, and redact private host or network values
before posting a log.

## Pull requests

- Base work on the current `vita` branch and keep changes focused.
- Do not commit build output, downloaded installers, local configuration,
  pairing data, logs, IP addresses, certificates, or credentials.
- Preserve unrelated Sunshine applications and Windows display state.
- Keep the Vita client usable without diagnostics or file logging enabled.
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
