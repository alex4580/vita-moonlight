#!/usr/bin/env python3
"""Fail when release identity metadata disagrees across packaged components."""

from __future__ import annotations

import os
import re
import sys
from pathlib import Path
from typing import List, Optional


REPOSITORY_ROOT = Path(__file__).resolve().parents[1]
errors: List[str] = []


def read(relative_path: str) -> str:
    path = REPOSITORY_ROOT / relative_path
    try:
        return path.read_text(encoding="utf-8-sig")
    except OSError as exc:
        errors.append(f"{relative_path}: could not read file: {exc}")
        return ""


def extract(
    relative_path: str,
    pattern: str,
    description: str,
    flags: int = 0,
) -> Optional[str]:
    matches = re.findall(pattern, read(relative_path), flags)
    if len(matches) != 1:
        errors.append(
            f"{relative_path}: expected exactly one {description}; found {len(matches)}"
        )
        return None

    value = matches[0]
    if isinstance(value, tuple):
        errors.append(
            f"{relative_path}: internal checker error: {description} has multiple groups"
        )
        return None
    return value


def expect(description: str, actual: Optional[str], expected: str) -> None:
    if actual is not None and actual != expected:
        errors.append(f"{description}: expected {expected!r}, found {actual!r}")


def main() -> int:
    major = extract(
        "CMakeLists.txt",
        r'^\s*set\(VERSION_MAJOR\s+"([0-9]+)"\s*\)',
        "VERSION_MAJOR declaration",
        re.MULTILINE,
    )
    minor = extract(
        "CMakeLists.txt",
        r'^\s*set\(VERSION_MINOR\s+"([0-9]+)"\s*\)',
        "VERSION_MINOR declaration",
        re.MULTILINE,
    )
    patch = extract(
        "CMakeLists.txt",
        r'^\s*set\(VERSION_PATCH\s+"([0-9]+)"\s*\)',
        "VERSION_PATCH declaration",
        re.MULTILINE,
    )

    if major is None or minor is None or patch is None:
        return report_failure()

    version = f"{int(major)}.{int(minor)}.{int(patch)}"
    assembly_version = f"{version}.0"
    vita_app_version = f"{int(major):02d}.{int(minor):02d}"

    expect(
        "Windows host package version",
        extract(
            "host/VitaMoonlight.Host/VitaMoonlight.Host.csproj",
            r"<Version>([^<]+)</Version>",
            "Version element",
        ),
        version,
    )
    expect(
        "Windows host assembly version",
        extract(
            "host/VitaMoonlight.Host/VitaMoonlight.Host.csproj",
            r"<AssemblyVersion>([^<]+)</AssemblyVersion>",
            "AssemblyVersion element",
        ),
        assembly_version,
    )
    expect(
        "Windows host file version",
        extract(
            "host/VitaMoonlight.Host/VitaMoonlight.Host.csproj",
            r"<FileVersion>([^<]+)</FileVersion>",
            "FileVersion element",
        ),
        assembly_version,
    )
    expect(
        "Windows installer version",
        extract(
            "host/installer/VitaMoonlightHost.iss",
            r"^AppVersion=([^\s]+)\s*$",
            "AppVersion declaration",
            re.MULTILINE,
        ),
        version,
    )
    expect(
        "Windows installer file version",
        extract(
            "host/installer/VitaMoonlightHost.iss",
            r"^VersionInfoVersion=([^\s]+)\s*$",
            "VersionInfoVersion declaration",
            re.MULTILINE,
        ),
        assembly_version,
    )
    expect(
        "manual build version",
        extract(
            "docs/CMakeLists.txt",
            r'--release="vita-moonlight ([^"]+)"',
            "pod2man release version",
        ),
        version,
    )
    expect(
        "manual source version",
        extract(
            "docs/README.pod",
            r"=head1 VERSION\s+([^\s]+)",
            "VERSION section",
        ),
        version,
    )
    expect(
        "LiveArea version",
        extract(
            "sce_sys/livearea/contents/template.xml",
            r'<str size="18"[^>]*>v([0-9]+\.[0-9]+\.[0-9]+)</str>',
            "version label",
        ),
        version,
    )
    expect(
        "current changelog version",
        extract(
            "CHANGELOG.md",
            r"\A\s*##\s+([0-9]+\.[0-9]+\.[0-9]+)\s*$",
            "first release heading",
            re.MULTILINE,
        ),
        version,
    )
    expect(
        "Vita change-info release version",
        extract(
            "resources/changeinfo.xml",
            r"\A\s*<\?xml[^>]*>\s*<changeinfo>\s*<changes[^>]*>"
            r"<!\[CDATA\[\s*##\s+([0-9]+\.[0-9]+\.[0-9]+)<br>",
            "first release heading",
        ),
        version,
    )
    expect(
        "Vita change-info app version",
        extract(
            "resources/changeinfo.xml",
            r'\A\s*<\?xml[^>]*>\s*<changeinfo>\s*<changes app_ver="'
            r'([0-9]{2}\.[0-9]{2})">',
            "first app_ver attribute",
        ),
        vita_app_version,
    )
    expect(
        "release checklist tag",
        extract(
            "host/FINAL_RELEASE_CHECKLIST.md",
            r"Create and push `v([0-9]+\.[0-9]+\.[0-9]+)`",
            "current release tag instruction",
        ),
        version,
    )

    if os.environ.get("GITHUB_REF_TYPE") == "tag":
        tag = os.environ.get("GITHUB_REF_NAME", "")
        allowed_tag = re.compile(
            rf"^v{re.escape(version)}"
            r"(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$"
        )
        if not allowed_tag.fullmatch(tag):
            errors.append(
                "Git tag: expected "
                f"'v{version}' or 'v{version}-<prerelease>', found {tag!r}"
            )

    if errors:
        return report_failure()

    print(f"Version consistency check passed: {version}")
    return 0


def report_failure() -> int:
    print("Version consistency check failed:", file=sys.stderr)
    for error in errors:
        print(f"  - {error}", file=sys.stderr)
    return 1


if __name__ == "__main__":
    raise SystemExit(main())
