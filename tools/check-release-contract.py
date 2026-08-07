#!/usr/bin/env python3
"""Verify the public-release identity, workflow, and documentation contract."""

from __future__ import annotations

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
    vita_fetcher = read("tools/fetch-vitasdk-packages.py")
    vita_lock_text = read("tools/vitasdk-packages.lock.json")
    makepsv = read("makepsv")
    building = read("docs/BUILDING.md")

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
        ("--lock tools/vitasdk-packages.lock.json", "explicit committed package lock"),
        ("--manifest \"$dependency_manifest\"", "auditable dependency manifest"),
    ):
        if needle not in vita_workflow:
            ERRORS.append(
                f".github/workflows/cmake-psvita.yml: missing {description}: {needle!r}"
            )
    for needle, description in (
        ("load_lock(args.lock)", "committed-lock loading"),
        ("require_asset_match(locked_asset, upstream)", "locked metadata comparison"),
        ('request(asset["asset_api_url"]', "asset-ID download"),
        ('actual_sha256 != asset["sha256"]', "downloaded content verification"),
    ):
        if needle not in vita_fetcher:
            ERRORS.append(
                f"tools/fetch-vitasdk-packages.py: missing {description}: {needle!r}"
            )

    expected_vitasdk_packages = (
        "zlib.tar.xz",
        "bzip2.tar.xz",
        "zstd.tar.xz",
        "libpng.tar.xz",
        "libjpeg-turbo.tar.xz",
        "freetype.tar.xz",
        "libvita2d.tar.xz",
        "openssl.tar.xz",
        "curl.tar.xz",
        "expat.tar.xz",
        "opus.tar.xz",
    )
    try:
        vita_lock = json.loads(vita_lock_text)
    except json.JSONDecodeError as exc:
        ERRORS.append(f"tools/vitasdk-packages.lock.json: invalid JSON: {exc}")
        vita_lock = {}
    if not isinstance(vita_lock, dict):
        ERRORS.append("tools/vitasdk-packages.lock.json: lock must be an object")
        vita_lock = {}
    if vita_lock.get("schema") != "vita-moonlight/vitasdk-package-lock/v1":
        ERRORS.append("tools/vitasdk-packages.lock.json: unexpected lock schema")
    if vita_lock.get("repository") != "vitasdk/packages":
        ERRORS.append("tools/vitasdk-packages.lock.json: unexpected repository")
    timestamp_pattern = (
        r"[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z"
    )
    if re.fullmatch(
        timestamp_pattern, str(vita_lock.get("locked_at_utc", ""))
    ) is None:
        ERRORS.append("tools/vitasdk-packages.lock.json: invalid lock timestamp")
    locked_assets = vita_lock.get("assets")
    if not isinstance(locked_assets, list):
        ERRORS.append("tools/vitasdk-packages.lock.json: assets must be an array")
        locked_assets = []
    locked_names = [
        asset.get("name") for asset in locked_assets if isinstance(asset, dict)
    ]
    if locked_names != list(expected_vitasdk_packages):
        ERRORS.append(
            "tools/vitasdk-packages.lock.json: dependency names/order do not match the supported set"
        )
    locked_ids: list[int] = []
    for asset in locked_assets:
        if not isinstance(asset, dict):
            ERRORS.append("tools/vitasdk-packages.lock.json: every asset must be an object")
            continue
        name = asset.get("name")
        asset_id = asset.get("asset_id")
        if type(asset_id) is not int or asset_id <= 0:
            ERRORS.append(f"tools/vitasdk-packages.lock.json: {name!r} has invalid asset ID")
            continue
        locked_ids.append(asset_id)
        if type(asset.get("size")) is not int or asset["size"] <= 0:
            ERRORS.append(f"tools/vitasdk-packages.lock.json: {name!r} has invalid byte count")
        if re.fullmatch(r"[0-9a-f]{64}", str(asset.get("sha256", ""))) is None:
            ERRORS.append(f"tools/vitasdk-packages.lock.json: {name!r} has invalid SHA-256")
        if re.fullmatch(
            timestamp_pattern, str(asset.get("updated_at", ""))
        ) is None:
            ERRORS.append(f"tools/vitasdk-packages.lock.json: {name!r} has invalid timestamp")
        if asset.get("asset_api_url") != (
            f"https://api.github.com/repos/vitasdk/packages/releases/assets/{asset_id}"
        ):
            ERRORS.append(f"tools/vitasdk-packages.lock.json: {name!r} has invalid asset URL")
    if len(locked_ids) != len(set(locked_ids)):
        ERRORS.append("tools/vitasdk-packages.lock.json: asset IDs must be unique")

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
            "vita-moonlight/vitasdk-dependency-manifest/v2",
            "locked Vita dependency manifest",
        ),
        ("manifest_lock_sha256", "Vita dependency lock hash binding"),
        ("vitasdk-lock-core.json", "Vita dependency content comparison"),
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
