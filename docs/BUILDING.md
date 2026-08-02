# Building and forking Vita Moonlight

This repository contains two products that are released together:

- the PlayStation Vita client (`.vpk`); and
- the self-contained Windows x64 host companion, installer, and portable ZIP.

Build both from the same commit. Do not combine a VPK from one revision with a
host package from another.

## Fork and clone

Create a GitHub fork, then clone it with all submodules:

```sh
git clone --recurse-submodules https://github.com/YOUR-NAME/vita-moonlight.git
cd vita-moonlight
git remote add upstream https://github.com/xyzz/vita-moonlight.git
```

If the repository was cloned without `--recurse-submodules`, run:

```sh
git submodule update --init --recursive
```

Keep fork-specific work on a feature branch and merge it into your fork's
`vita` branch only after CI and hardware testing pass. There is no requirement
to submit this host integration to the upstream project.

## Windows host

The supported developer toolchain is Windows x64 with the .NET 8 SDK. CI pins
.NET SDK `8.0.423`.

From the repository root:

```powershell
dotnet format host\VitaMoonlight.Host\VitaMoonlight.Host.csproj --verify-no-changes
dotnet build host\VitaMoonlight.Host\VitaMoonlight.Host.csproj -c Release
dotnet run --project host\VitaMoonlight.Host\VitaMoonlight.Host.csproj `
  -c Release --no-build -- self-test
```

Create the self-contained executable with:

```powershell
dotnet publish host\VitaMoonlight.Host\VitaMoonlight.Host.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false `
  -o artifacts\host
```

The installer also needs the pinned DisplayWizard VDD, Visual C++ runtime,
ViGEmBus, Sunshine, and Inno Setup version recorded in
`.github/workflows/windows-host.yml`. The three preparation scripts under
`host/installer` download and hash-check those third-party packages. The
Windows workflow is the canonical, reproducible packaging recipe and produces
both the setup EXE and portable ZIP.

Branch and pull-request packages are deliberately unsigned developer builds.
Tagged public releases fail unless release-signing credentials are configured,
except for the exact, explicitly labeled `v0.14.7-beta.1` unsigned preview;
see [RELEASING.md](RELEASING.md).

## Vita client

The Vita build requires VitaSDK, CMake, Make, and the packages used by this
project. With `VITASDK` set and its `bin` directory on `PATH`:

```sh
cmake -S . -B build \
  -DCMAKE_TOOLCHAIN_FILE="$VITASDK/share/vita.toolchain.cmake"
cmake --build build
```

The VPK is written under `build/`. `./makepsv` is the existing convenience
wrapper for local VitaSDK builds.

For reproducibility, `.github/workflows/cmake-psvita.yml` pins the VitaSDK
archive, package assets, and their SHA-256 hashes. Use that workflow as the
source of truth if a rolling local VitaSDK behaves differently.

## Version and generated metadata

Do not edit version fields independently. To begin a new release:

```powershell
python release.py 0.14.7
python tools/check-version-consistency.py
```

`release.py` updates the Vita package, Windows assembly, installer, manual,
LiveArea, changelog, and release-checklist identities together. Review the
generated changelog section before committing.

## What can be tested without hardware

Desktop CI can compile both products, enforce version consistency, exercise
the portable companion's diagnostics-only boundary on Windows 10/11 runners,
validate the privacy-safe support-log summarizer, and assemble the packages.
It cannot prove Vita rendering, hardware decoding, controller timing, gyro,
Wi-Fi behavior, or Windows display recovery on a real GPU and monitor.

The source-only Windows upgrade check verifies that setup never sends a
current-only command to a known older installed host before replacing it. It
also verifies that the installer-embedded current helper restores and proves a
physical-only topology before publishing the protected maintenance fence. It
does not mutate the computer running the check:

```powershell
python tools\check-windows-upgrade-contract.py
```

The support-log summarizer has no third-party Python dependencies:

```powershell
python tools\summarize-vita-log.py --self-test
python tools\summarize-vita-log.py "C:\path\to\moonlight.log"
python tools\summarize-vita-log.py --json "C:\path\to\moonlight.log"
```

Its self-test covers structured sessions, interval/cumulative network
aggregation, malformed or foreign records, sequence gaps, and privacy
filtering. The release packages place the script at
`tools\SupportLog\summarize-vita-log.py`.

Before publishing a beta, run `host\BETA_SMOKE_TEST.md`. Final releases use
`host\END_TO_END_TEST.md` and `host\FINAL_RELEASE_CHECKLIST.md`. Packaged
Windows builds preserve the same layout:

- the simple `README.md` is at the package root;
- acceptance, compatibility, host, and release-checklist documents are under
  `host\`;
- community, logging, Vita settings, build, and release guides are under
  `docs\`; and
- the optional support-log summarizer is under `tools\SupportLog\`.
