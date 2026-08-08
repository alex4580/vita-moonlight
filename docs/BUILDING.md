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
except for the exact, explicitly labeled `v0.14.8-beta.1` unsigned preview;
see [RELEASING.md](RELEASING.md).

## Vita client

The Vita build requires the pinned VitaSDK, CMake, Make, Git, Python 3,
`pkg-config`, and an initial network connection for the official dependency
sources. With `VITASDK` set and its `bin` directory on `PATH`, stage and build
the locked dependency chain before configuring the client:

```sh
python3 tools/stage-vita-dependency-sources.py \
  --lock tools/vita-corresponding-source.lock.json \
  --output-dir .tools/vita-dependency-sources \
  --cache-dir .tools/vita-dependency-cache \
  --manifest .tools/vitasdk-dependencies.json
VITA_BUILD_JOBS=2 bash tools/build-vita-dependencies.sh \
  .tools/vita-dependency-sources .tools/vita-dependency-build
python3 tools/stage-vita-dependency-sources.py \
  --lock tools/vita-corresponding-source.lock.json \
  --manifest .tools/vitasdk-dependencies.json \
  --finalize-install "$VITASDK"
cmake -S . -B build \
  -DCMAKE_BUILD_TYPE=Release \
  -DCMAKE_TOOLCHAIN_FILE="$VITASDK/share/vita.toolchain.cmake"
cmake --build build
```

The VPK is written under `build/`. `./makepsv` is the existing convenience
wrapper for local VitaSDK builds.

For reproducibility, `.github/workflows/cmake-psvita.yml` pins and hash-checks
the base VitaSDK archive. Every linked client library is then rebuilt from
official upstream source archives or exact Git commits with the
project-maintained, GPL-3.0-or-later recipe in
`tools/build-vita-dependencies.sh`. The source identities, byte counts,
SHA-256 hashes, build order, project patch hashes, and required installed
outputs are bound by `tools/vita-corresponding-source.lock.json`. This chain
builds zlib, bzip2, zstd, libpng, libjpeg-turbo, FreeType, libvita2d, Expat,
Opus, Mbed TLS, and curl; it does not install mutable VitaSDK package-release
binaries.

A first build needs network access to download the official archives and fetch
the two pinned Git commits. Each later fresh staging run also re-fetches those
immutable Git commits. An existing archive cache is accepted only when both
its exact byte count and SHA-256 still match the lock; corrupt or substituted
cache entries fail closed. Git inputs are verified at the locked commit before
export. Mbed TLS's official archive and reviewed Vita patch remain committed
under `vendor/vita-source/` so the pairing crypto input can also be checked
offline.

Choose new, empty staging and build directories for every run. The stager and
builder deliberately refuse reused directories, safely inspect archive paths,
apply the reviewed patches with exact-context `git apply`, and install only
under `$VITASDK/arm-vita-eabi`. The finalization command hashes every required
installed output and changes `vitasdk-dependencies.json` from `pending` to
`complete`. CI and release staging reject a missing, pending, or lock-mismatched
manifest. Treat recipe, patches, source lock, and dependency version changes
as one reviewed update; never copy a new live hash into CI without inspecting
the corresponding source and rebuilding the full chain.

Use the pinned SDK identity, source lock, stager, and build recipe together as
the source of truth if a rolling local VitaSDK behaves differently.
The parent repository also pins `moonlight-common-c` to
`07c32c80f98bb0d7214c577bd080eea3ce64a856`; initialize submodules recursively
and do not replace that revision without a Vita hardware regression pass. The
build applies the hash-locked RTSP hardening backport documented in
`patches/moonlight-common-c/README.md` to a generated build copy; it does not
dirty the submodule.

Before building, the Vita workflow runs the same source-contract checks that
fork maintainers can run locally:

```sh
python3 tools/check-version-consistency.py
python3 tools/check-release-contract.py
python3 tools/check-host-client-contract.py
python3 tools/check-vita-media-contract.py
python3 tools/check-vita-security-contract.py
python3 tools/check-vita-device-contract.py
python3 tools/check-vita-host-scan-contract.py
python3 tools/check-vita-mdns-parser.py
python3 tools/check-vita-client-resilience.py
python3 tools/check-vita-stream-reliability.py
python3 tools/check-xml-hardening.py
python3 tools/check-moonlight-common-backport.py
python3 tools/summarize-vita-log.py --self-test
python3 tools/stage-vita-dependency-sources.py \
  --lock tools/vita-corresponding-source.lock.json --self-test
python3 tools/stage-vita-mbedtls.py \
  --lock tools/vita-mbedtls-source.lock.json \
  --vendor-dir vendor/vita-source --self-test
python3 tools/build-vita-source-bundle.py \
  --lock tools/vita-corresponding-source.lock.json \
  --self-test --require-complete
```

The dependency stager self-test validates the project-owned recipe, patches,
source directories, and required output contract. The source-bundle self-test
validates every dependency, SDK-runtime, and submodule pin. A tagged release
additionally passes `--require-complete` and builds the complete source archive
beside the VPK; that gate must never be bypassed.

The Windows workflow additionally runs
`python tools/check-windows-upgrade-contract.py`, compiles on its oldest and
newest supported GitHub runner images, and executes the host self-test. Every
`tools/check-*.py` contract is referenced by at least one required workflow;
`check-release-contract.py` enforces that coverage.

## Version and generated metadata

Do not edit version fields independently. To begin a new release:

```powershell
python release.py 0.14.8
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
