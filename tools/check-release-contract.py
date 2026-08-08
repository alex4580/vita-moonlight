#!/usr/bin/env python3
"""Verify the public-release identity, workflow, and documentation contract."""

from __future__ import annotations

import hashlib
import json
import re
import sys
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
ERRORS: list[str] = []


def read(relative: str) -> str:
    try:
        return (ROOT / relative).read_text(encoding="utf-8-sig")
    except OSError as exc:
        ERRORS.append(f"{relative}: could not read file: {exc}")
        return ""


def require(relative: str, needle: str, description: str) -> None:
    if needle not in read(relative):
        ERRORS.append(f"{relative}: missing {description}: {needle!r}")


def main() -> int:
    cmake = read("CMakeLists.txt")
    vita_workflow = read(".github/workflows/cmake-psvita.yml")
    dependency_stager = read("tools/stage-vita-dependency-sources.py")
    dependency_builder = read("tools/build-vita-dependencies.sh")
    mbedtls_stager = read("tools/stage-vita-mbedtls.py")
    mbedtls_builder = read("tools/build-vita-mbedtls.sh")
    mbedtls_lock_text = read("tools/vita-mbedtls-source.lock.json")
    vita_source_builder = read("tools/build-vita-source-bundle.py")
    vita_source_lock_text = read("tools/vita-corresponding-source.lock.json")
    vita_notices = read("THIRD_PARTY_NOTICES.txt")
    makepsv = read("makepsv")
    building = read("docs/BUILDING.md")

    for obsolete in (
        "tools/fetch-vitasdk-packages.py",
        "tools/vitasdk-packages.lock.json",
        "vendor/VITASDK_PACKAGES.md",
        "vendor/vitasdk-packages",
    ):
        if (ROOT / obsolete).exists():
            ERRORS.append(f"{obsolete}: obsolete opaque VitaSDK package input must be absent")
    for relative, text in (
        (".gitattributes", read(".gitattributes")),
        (".github/workflows/cmake-psvita.yml", vita_workflow),
        (".github/workflows/release.yml", read(".github/workflows/release.yml")),
        ("docs/BUILDING.md", building),
        ("docs/RELEASING.md", read("docs/RELEASING.md")),
    ):
        for obsolete_reference in (
            "fetch-vitasdk-packages.py",
            "vitasdk-packages.lock.json",
            "vendor/vitasdk-packages",
            "vita-moonlight/vitasdk-package-lock/v1",
            "vita-moonlight/vitasdk-dependency-manifest/v2",
        ):
            if obsolete_reference in text:
                ERRORS.append(
                    f"{relative}: obsolete VitaSDK package reference {obsolete_reference!r}"
                )

    if "assets/nerdfont.ttf" in cmake or (ROOT / "assets" / "nerdfont.ttf").exists():
        ERRORS.append("the ambiguously licensed Nerd Font must not be packaged or present")
    if (
        "--add ${CMAKE_SOURCE_DIR}/assets/mononoki-Regular.ttf="
        "assets/mononoki-Regular.ttf"
    ) not in cmake:
        ERRORS.append("CMakeLists.txt: official Mononoki font is not packaged")
    mononoki = ROOT / "assets" / "mononoki-Regular.ttf"
    if not mononoki.is_file() or hashlib.sha256(mononoki.read_bytes()).hexdigest() != (
        "ecefad2b6deec9ba448ba4635c4a596091b515b5b6e67e67bc0bec0b487868bd"
    ):
        ERRORS.append("assets/mononoki-Regular.ttf: provenance hash changed")

    packaged_vita_licenses = (
        "README.txt",
        "Bzip2.txt",
        "Curl.txt",
        "ENet.txt",
        "Expat.txt",
        "FreeType-FTL.txt",
        "GCC-Runtime-Exception.txt",
        "inih.txt",
        "Libjpeg-IJG.txt",
        "Libjpeg-turbo.txt",
        "Libpng.txt",
        "Libvita2d.txt",
        "libuuid.txt",
        "MbedTLS.txt",
        "Newlib.txt",
        "Opus.txt",
        "PSPSDK-Debug-Font.txt",
        "Pthread.txt",
        "Pthread-LGPL-2.1.txt",
        "Pthread-Vita-MIT.txt",
        "Reed-Solomon.txt",
        "Vita-Headers.txt",
        "Vita-Toolchain.txt",
        "Zlib.txt",
        "Zstd-BSD.txt",
    )
    for name in packaged_vita_licenses:
        relative = f"licenses/vita/{name}"
        path = ROOT / relative
        if not path.is_file() or path.stat().st_size == 0:
            ERRORS.append(f"{relative}: missing or empty exact license text")
        if f"--add ${{CMAKE_SOURCE_DIR}}/{relative}=licenses/{name}" not in cmake:
            ERRORS.append(f"CMakeLists.txt: VPK does not include {relative}")
    for needle, description in (
        ("--add ${CMAKE_SOURCE_DIR}/THIRD_PARTY_NOTICES.txt=licenses/THIRD_PARTY_NOTICES.txt", "notice index"),
        ("--add ${CMAKE_SOURCE_DIR}/LICENSE=licenses/GPL-3.0.txt", "project GPL-3.0 text"),
        ("--add ${CMAKE_SOURCE_DIR}/third_party/h264bitstream/LICENSE=licenses/LGPL-2.1-h264bitstream.txt", "h264bitstream LGPL text"),
        ("--add ${CMAKE_SOURCE_DIR}/assets/LICENSE-Mononoki.txt=licenses/OFL-Mononoki.txt", "Mononoki OFL text"),
    ):
        if needle not in cmake:
            ERRORS.append(f"CMakeLists.txt: VPK is missing {description}: {needle!r}")
    for component in (
        "moonlight-common-c",
        "Reed-Solomon",
        "h264bitstream",
        "Mbed TLS",
        "newlib",
        "pthread-embedded",
        "GCC runtime libraries",
        "FreeType Project",
        "completion_status",
    ):
        if component not in vita_notices:
            ERRORS.append(f"THIRD_PARTY_NOTICES.txt: missing {component} notice")

    if "if(NOT CMAKE_CONFIGURATION_TYPES AND NOT CMAKE_BUILD_TYPE)" not in cmake or \
            "set(CMAKE_BUILD_TYPE Release CACHE STRING" not in cmake:
        ERRORS.append("CMakeLists.txt: single-config Vita builds must default to Release")
    if "set(CMAKE_C_FLAGS" in cmake or "add_compile_options(-g3" in cmake:
        ERRORS.append("CMakeLists.txt: unconditional debug/global C flags would de-optimize release VPKs")
    for needle, description in (
        ("add_definitions(-D__vita__)", "global Vita platform definition"),
        ("$<$<CONFIG:Debug>:-g3>", "Debug-only symbols"),
        ("$<$<CONFIG:Release>:-O2>", "explicit release optimization"),
        ("C_EXTENSIONS OFF", "strict C99 mode"),
    ):
        if needle not in cmake:
            ERRORS.append(f"CMakeLists.txt: missing {description}: {needle!r}")
    if "-DCMAKE_BUILD_TYPE=Release" not in vita_workflow:
        ERRORS.append(".github/workflows/cmake-psvita.yml: Vita CI must configure Release explicitly")
    if "cmake -DCMAKE_BUILD_TYPE=Release .." not in makepsv:
        ERRORS.append("makepsv: local Vita helper must configure Release explicitly")
    if "-DCMAKE_BUILD_TYPE=Release" not in building:
        ERRORS.append("docs/BUILDING.md: Vita build example must configure Release explicitly")

    for needle, description in (
        ("python3 tools/stage-vita-dependency-sources.py", "source dependency stager"),
        ("--lock tools/vita-corresponding-source.lock.json", "complete dependency source lock"),
        ("bash tools/build-vita-dependencies.sh", "project-owned dependency source build"),
        ("--finalize-install \"$VITASDK\"", "installed-output manifest verification"),
        ("--manifest \"$dependency_manifest\"", "auditable source-build manifest"),
        ("--lock tools/vita-mbedtls-source.lock.json", "mbedTLS source lock"),
        ("--vendor-dir vendor/vita-source", "mbedTLS committed source vendor"),
        ("python3 tools/check-vita-mdns-parser.py", "native clean-room mDNS parser test"),
        ("python3 tools/build-vita-source-bundle.py", "Vita source-lock self-test"),
        ("--lock tools/vita-corresponding-source.lock.json", "Vita source lock"),
    ):
        if needle not in vita_workflow:
            ERRORS.append(
                f".github/workflows/cmake-psvita.yml: missing {description}: {needle!r}"
            )

    for needle, description in (
        ("safe_archive_members(", "safe official-source archive validation"),
        ("verify_patch_application(", "exact-context patch verification"),
        ('"apply", "--check"', "non-fuzzy git patch enforcement"),
        ("assert_patch_contract(", "targeted Vita behavior assertions"),
        ("SPDX-License-Identifier: GPL-3.0-or-later", "project patch license gate"),
        ("MBEDTLS_THREADING_C", "VitaSDK threading configuration"),
        ("vita-moonlight/vita-mbedtls-source-manifest/v1", "source manifest schema"),
        ("every modified mbedTLS file must carry", "Apache-2.0 changed-file notice gate"),
    ):
        if needle not in mbedtls_stager:
            ERRORS.append(
                f"tools/stage-vita-mbedtls.py: missing {description}: {needle!r}"
            )
    for needle, description in (
        ('git -C "$source_root" apply --check', "exact-context git patch preflight"),
        ('git -C "$source_root" apply "$vita_patch"', "exact-context git patch application"),
        ("MBEDTLS_THREADING_C", "threading core enable"),
        ("MBEDTLS_THREADING_PTHREAD", "pthread enable"),
        ("-DENABLE_PROGRAMS=OFF", "program build disable"),
        ("-DENABLE_TESTING=OFF", "test build disable"),
        ("-DMBEDTLS_FATAL_WARNINGS=OFF", "cross-build fatal-warning disable"),
        ('cmake --build "$build_dir" --parallel --target mbedtls', "mbedTLS target build"),
        ('cmake --install "$build_dir"', "SDK-prefix install"),
        ("Mbed TLS 3.6.7", "installed-version assertion"),
    ):
        if needle not in mbedtls_builder:
            ERRORS.append(
                f"tools/build-vita-mbedtls.sh: missing {description}: {needle!r}"
            )
    for needle, description in (
        ("download exceeded locked size", "bounded official-source downloads"),
        ("validate_tar_members(", "safe archive structure validation"),
        ("GIT_CEILING_DIRECTORIES", "fail-closed patch isolation"),
        ('"apply", "--check"', "exact-context patch enforcement"),
        ('"apply", "--reverse", "--check"', "post-application verification"),
        ("--finalize-install", "installed-output finalization"),
        ("vita-source-dependency-manifest/v1", "source-build manifest schema"),
    ):
        if needle not in dependency_stager:
            ERRORS.append(
                f"tools/stage-vita-dependency-sources.py: missing {description}: {needle!r}"
            )
    for needle, description in (
        ("SPDX-License-Identifier: GPL-3.0-or-later", "project recipe license"),
        ("Refusing to reuse dependency build directory", "clean build-root gate"),
        ("ZSTD_LEGACY_SUPPORT=OFF", "lean zstd configuration"),
        ("PNG_ARM_NEON=on", "Vita PNG NEON configuration"),
        ("OPUS_PRESUME_NEON=ON", "Vita Opus NEON configuration"),
        ("EXPAT_WITH_GETENTROPY=ON", "strong Expat entropy"),
        ("CURL_USE_MBEDTLS=ON", "single TLS backend"),
        ("ENABLE_THREADED_RESOLVER=OFF", "thread-free curl resolver"),
        ("HTTP_ONLY=ON", "HTTP/HTTPS-only curl"),
    ):
        if needle not in dependency_builder:
            ERRORS.append(
                f"tools/build-vita-dependencies.sh: missing {description}: {needle!r}"
            )

    try:
        mbedtls_lock = json.loads(mbedtls_lock_text)
    except json.JSONDecodeError as exc:
        ERRORS.append(f"tools/vita-mbedtls-source.lock.json: invalid JSON: {exc}")
        mbedtls_lock = {}
    if not isinstance(mbedtls_lock, dict):
        ERRORS.append("tools/vita-mbedtls-source.lock.json: lock must be an object")
        mbedtls_lock = {}
    if mbedtls_lock.get("schema") != "vita-moonlight/vita-mbedtls-source-lock/v1":
        ERRORS.append("tools/vita-mbedtls-source.lock.json: unexpected lock schema")
    if mbedtls_lock.get("component") != {
        "abi_line": "3.6-lts",
        "license_expression": "Apache-2.0 AND GPL-3.0-or-later",
        "name": "mbedtls",
        "version": "3.6.7",
    }:
        ERRORS.append("tools/vita-mbedtls-source.lock.json: component identity changed")
    mbedtls_inputs = mbedtls_lock.get("inputs")
    if not isinstance(mbedtls_inputs, list):
        ERRORS.append("tools/vita-mbedtls-source.lock.json: inputs must be an array")
        mbedtls_inputs = []
    expected_mbedtls_inputs = (
        "mbedtls-3.6.7.tar.bz2",
        "mbedtls-3.6.7-vita.patch",
    )
    if [item.get("name") for item in mbedtls_inputs if isinstance(item, dict)] != list(
        expected_mbedtls_inputs
    ):
        ERRORS.append(
            "tools/vita-mbedtls-source.lock.json: source input names/order changed"
        )
    source_vendor = ROOT / "vendor" / "vita-source"
    try:
        source_vendor_names = sorted(path.name for path in source_vendor.iterdir())
    except OSError as exc:
        ERRORS.append(f"vendor/vita-source: could not enumerate inputs: {exc}")
        source_vendor_names = []
    if source_vendor_names != sorted(expected_mbedtls_inputs):
        ERRORS.append("vendor/vita-source: files do not exactly match the source lock")
    source_inputs_by_name = {
        item.get("name"): item for item in mbedtls_inputs if isinstance(item, dict)
    }
    for name in expected_mbedtls_inputs:
        item = source_inputs_by_name.get(name)
        path = source_vendor / name
        if not isinstance(item, dict):
            continue
        if path.is_symlink() or not path.is_file():
            ERRORS.append(f"vendor/vita-source/{name}: missing regular input")
            continue
        try:
            size = path.stat().st_size
            digest = hashlib.sha256()
            with path.open("rb") as source:
                while chunk := source.read(1024 * 1024):
                    digest.update(chunk)
        except OSError as exc:
            ERRORS.append(f"vendor/vita-source/{name}: could not verify: {exc}")
            continue
        if size != item.get("size"):
            ERRORS.append(f"vendor/vita-source/{name}: size differs from source lock")
        if digest.hexdigest() != item.get("sha256"):
            ERRORS.append(f"vendor/vita-source/{name}: SHA-256 differs from source lock")

    try:
        vita_source_lock = json.loads(vita_source_lock_text)
    except json.JSONDecodeError as exc:
        ERRORS.append(f"tools/vita-corresponding-source.lock.json: invalid JSON: {exc}")
        vita_source_lock = {}
    if not isinstance(vita_source_lock, dict):
        ERRORS.append("tools/vita-corresponding-source.lock.json: lock must be an object")
        vita_source_lock = {}
    if vita_source_lock.get("schema") != "vita-moonlight/vita-corresponding-source-lock/v1":
        ERRORS.append("tools/vita-corresponding-source.lock.json: unexpected schema")
    source_archive_names = {
        item.get("name")
        for item in vita_source_lock.get("archive_inputs", [])
        if isinstance(item, dict)
    }
    expected_source_archive_names = {
        "zlib-1.3.2.tar.xz",
        "bzip2-1.0.8.tar.gz",
        "zstd-1.5.7.tar.gz",
        "libjpeg-turbo-3.2.0.tar.gz",
        "freetype-2.14.3.tar.xz",
        "curl-8.21.0.tar.xz",
        "expat-2.8.2.tar.xz",
        "opus-1.6.1.tar.gz",
        "gcc-15.2.0.tar.xz",
    }
    if source_archive_names != expected_source_archive_names:
        ERRORS.append("Vita source lock does not cover every archive-linked component")
    source_git_commits = {
        item.get("commit")
        for item in vita_source_lock.get("git_inputs", [])
        if isinstance(item, dict)
    }
    expected_source_git_commits = {
        "3061454d980de7d53608f594194cfac722721d2a",
        "a8f15ab09d5233f0a4e4ad0e8f6ade0da888cbed",
        "366f4140fb89193c55b2e115652da7dc28e3f7b4",
        "fbb8375870d799758e0b4358d2f1708781aa26a6",
        "63e1cd9152082d9ccb1b38d67a4caf975562fbeb",
        "e37621bf80f5140b567c96782f8dc369e1108e0f",
        "71f3789342f610a4eb3ba50fc33292c4900c6666",
    }
    if source_git_commits != expected_source_git_commits:
        ERRORS.append("Vita source lock does not cover the exact SDK/dependency git sources")
    source_submodule_commits = {
        item.get("commit")
        for item in vita_source_lock.get("submodules", [])
        if isinstance(item, dict)
    }
    if source_submodule_commits != {
        "07c32c80f98bb0d7214c577bd080eea3ce64a856",
        "dea6fb5414b180908b58c0293c831105b5d124dd",
        "8e06f6b77b5d4471bdc6d85ada81b67d37354a5c",
    }:
        ERRORS.append("Vita source lock does not cover every pinned submodule")
    blockers = vita_source_lock.get("known_distribution_blockers")
    if blockers != []:
        ERRORS.append("Vita source lock must have no unresolved distribution blocker")
    dependency_build = vita_source_lock.get("dependency_source_build")
    if not isinstance(dependency_build, dict):
        ERRORS.append("Vita source lock is missing the project-owned dependency build")
        dependency_build = {}
    if dependency_build.get("license_expression") != "GPL-3.0-or-later":
        ERRORS.append("Vita dependency build recipe must be GPL-3.0-or-later")
    if dependency_build.get("recipe") != "tools/build-vita-dependencies.sh":
        ERRORS.append("Vita source lock does not bind the dependency builder")
    if dependency_build.get("stager") != "tools/stage-vita-dependency-sources.py":
        ERRORS.append("Vita source lock does not bind the dependency stager")
    for needle, description in (
        ("blocked-not-complete-corresponding-source", "non-compliance status"),
        ("safe_extract_tar(", "safe git-archive extraction"),
        ("download exceeded locked size", "bounded source download"),
        ("SHA-256 mismatch", "source archive digest verification"),
        ("git\", \"archive\"", "full tagged and submodule git export"),
        ("SOURCE_MANIFEST.json", "machine-readable source manifest"),
        ("--require-complete", "release-fatal completion gate"),
    ):
        if needle not in vita_source_builder:
            ERRORS.append(
                f"tools/build-vita-source-bundle.py: missing {description}: {needle!r}"
            )

    components: list[str] = []
    for name in ("MAJOR", "MINOR", "PATCH"):
        match = re.search(
            rf'^\s*set\(VERSION_{name}\s+"([0-9]+)"\s*\)',
            cmake,
            re.MULTILINE,
        )
        if match is None:
            ERRORS.append(f"CMakeLists.txt: missing VERSION_{name}")
        else:
            components.append(str(int(match.group(1))))

    if len(components) != 3:
        return report()

    version = ".".join(components)
    unsigned_tag = f"v{version}-beta.1"
    tag_ref = f"refs/tags/{unsigned_tag}"

    required_policy_mentions = {
        "README.md": f"exact `{unsigned_tag}` preview",
        "host/BETA_SMOKE_TEST.md": f"exact `{unsigned_tag}` release",
        "docs/BUILDING.md": f"`{unsigned_tag}` unsigned preview",
        "docs/CODE_SIGNING_POLICY.md": f"exact `{unsigned_tag}` release",
        "docs/RELEASING.md": f"git tag -a {unsigned_tag} TESTED_COMMIT_SHA",
        ".github/ISSUE_TEMPLATE/beta_test_report.yml": unsigned_tag,
        ".github/ISSUE_TEMPLATE/bug_report.yml": unsigned_tag,
    }
    for relative, needle in required_policy_mentions.items():
        require(relative, needle, "current unsigned-preview identity")

    current_policy_files = [
        *required_policy_mentions,
        ".github/workflows/windows-host.yml",
        ".github/workflows/release.yml",
    ]
    beta_tag_pattern = re.compile(r"v[0-9]+\.[0-9]+\.[0-9]+-beta\.1")
    for relative in current_policy_files:
        stale_tags = sorted(
            tag
            for tag in set(beta_tag_pattern.findall(read(relative)))
            if tag != unsigned_tag
        )
        for stale_tag in stale_tags:
            ERRORS.append(
                f"{relative}: stale unsigned-preview identity {stale_tag!r}; "
                f"expected only {unsigned_tag!r}"
            )

    windows_workflow = read(".github/workflows/windows-host.yml")
    release_workflow = read(".github/workflows/release.yml")
    windows_installer = read("host/installer/VitaMoonlightHost.iss")
    for needle, description in (
        ("THIRD_PARTY_NOTICES.txt artifacts/portable", "portable Vita notice index"),
        ("Copy-Item licenses/vita/* artifacts/portable/licenses/vita", "portable exact Vita licenses"),
        ("LGPL-2.1-h264bitstream.txt", "portable h264bitstream LGPL"),
        ("OFL-Mononoki.txt", "portable Mononoki OFL"),
    ):
        if needle not in windows_workflow:
            ERRORS.append(
                f".github/workflows/windows-host.yml: missing {description}: {needle!r}"
            )
    for needle, description in (
        ('Source: "..\\..\\THIRD_PARTY_NOTICES.txt"', "installed Vita notice index"),
        ('Source: "..\\..\\licenses\\vita\\*"', "installed exact Vita licenses"),
        ("LGPL-2.1-h264bitstream.txt", "installed h264bitstream LGPL"),
        ("OFL-Mononoki.txt", "installed Mononoki OFL"),
    ):
        if needle not in windows_installer:
            ERRORS.append(
                f"host/installer/VitaMoonlightHost.iss: missing {description}: {needle!r}"
            )
    for needle, description in (
        (tag_ref, "exact unsigned tag ref"),
        (f"project version {version}", "matching unsigned project version"),
        ("unsigned-beta-preview", "explicit unsigned signing mode"),
        ("windows-signing-status.json", "signing-state manifest"),
    ):
        if needle not in windows_workflow:
            ERRORS.append(
                f".github/workflows/windows-host.yml: missing {description}: {needle!r}"
            )

    for needle, description in (
        (unsigned_tag, "exact unsigned tag"),
        ("needs: [vita, windows-host]", "paired platform build dependency"),
        ("name: moonlight-vpk", "Vita artifact download"),
        ("name: vita-moonlight-windows-host", "Windows artifact download"),
        ("signing_commit\" != \"$GITHUB_SHA", "Windows artifact commit binding"),
        ("sha256sum --check SHA256SUMS", "release checksum verification"),
        ("subject-path: release-dist/*", "release provenance attestation"),
        ("gh attestation verify", "published provenance verification"),
        ("draft: true", "verify-before-publish draft"),
        (
            "vita-moonlight/vita-source-dependency-manifest/v1",
            "locked Vita source-build dependency manifest",
        ),
        ("manifest_lock_sha256", "Vita dependency lock hash binding"),
        ("vita-dependency-lock-recipe.json", "Vita dependency recipe comparison"),
        (".build_verification.status == \"complete\"", "completed dependency build proof"),
        ("Build complete Vita Corresponding Source archive", "source archive build"),
        ("--lock tools/vita-corresponding-source.lock.json", "source archive lock"),
        ("--require-complete", "release-fatal Corresponding Source gate"),
        ("*-Vita-Source.tar.gz", "source archive release asset"),
        ('Release staging must contain exactly seven assets.', "seven-asset release contract"),
        (
            'policy_url="https://github.com/$GITHUB_REPOSITORY/blob/$GITHUB_REF_NAME/docs/CODE_SIGNING_POLICY.md"',
            "fork-safe tag-pinned signing-policy URL",
        ),
        (
            'expected_policy_link="[Code signing policy]($policy_url)"',
            "draft signing-policy link verification",
        ),
    ):
        if needle not in release_workflow:
            ERRORS.append(
                f".github/workflows/release.yml: missing {description}: {needle!r}"
            )

    if release_workflow.count(
        "See the [Code signing policy]($policy_url)"
    ) < 2:
        ERRORS.append(
            ".github/workflows/release.yml: both unsigned-preview and signed-release "
            "notes must include the tag-pinned Code signing policy link"
        )

    for relative in (
        "README.md",
        "PRIVACY.md",
        "CHANGELOG.md",
        "resources/changeinfo.xml",
        "docs/COMMUNITY_TESTING.md",
        "docs/RELEASING.md",
    ):
        if re.search(
            r"https://github\.com/[^/\s)]+/vita-moonlight",
            read(relative),
        ):
            ERRORS.append(
                f"{relative}: fork-neutral documentation must not hard-code "
                "a repository owner"
            )

    release_helper = read("release.py")
    require(
        "release.py",
        'r"<InformationalVersion>[^<]+</InformationalVersion>"',
        "InformationalVersion update",
    )
    if "check-version-consistency.py" not in release_helper:
        ERRORS.append("release.py: does not run the version consistency check")

    workflow_text = "\n".join(
        path.read_text(encoding="utf-8-sig")
        for path in sorted((ROOT / ".github" / "workflows").glob("*.yml"))
    )
    for tool in sorted((ROOT / "tools").glob("check-*.py")):
        if tool.name not in workflow_text:
            ERRORS.append(
                f"{tool.relative_to(ROOT)}: static contract tool is not invoked by CI"
            )

    return report(version, unsigned_tag)


def report(version: str = "unknown", unsigned_tag: str = "unknown") -> int:
    if ERRORS:
        print("Release contract check failed:", file=sys.stderr)
        for error in ERRORS:
            print(f"  - {error}", file=sys.stderr)
        return 1

    print(
        "Release contract check passed: "
        f"version={version}, unsigned_preview={unsigned_tag}"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
