#!/usr/bin/env python3
"""Advance every packaged release identity and regenerate Vita change-info."""

from __future__ import annotations

import re
import subprocess
import sys
from pathlib import Path


REPOSITORY_ROOT = Path(__file__).resolve().parent
VERSION_PATTERN = re.compile(r"[0-9]+\.[0-9]+\.[0-9]+")
RELEASE_HEADING_PATTERN = re.compile(
    r"^##\s+([0-9]+\.[0-9]+\.[0-9]+)\s*$", re.MULTILINE
)


def read(relative_path: str) -> str:
    return (REPOSITORY_ROOT / relative_path).read_text(encoding="utf-8-sig")


def write(relative_path: str, content: str) -> None:
    with (REPOSITORY_ROOT / relative_path).open(
        "w", encoding="utf-8", newline="\n"
    ) as stream:
        stream.write(content)


def replace_required(
    relative_path: str,
    pattern: str,
    replacement: str,
    *,
    flags: int = re.MULTILINE,
) -> None:
    content = read(relative_path)
    updated, count = re.subn(pattern, lambda _: replacement, content, flags=flags)
    if count != 1:
        raise RuntimeError(
            f"{relative_path}: expected one release metadata match; found {count}"
        )
    write(relative_path, updated)


def vita_app_version(version: str) -> str:
    major, minor, _ = (int(component) for component in version.split("."))
    return f"{major:02d}.{minor:02d}"


def advance_changelog(version: str) -> str:
    changelog = read("CHANGELOG.md")
    headings = list(RELEASE_HEADING_PATTERN.finditer(changelog))
    matching_headings = [
        heading for heading in headings if heading.group(1) == version
    ]

    if matching_headings:
        if len(matching_headings) != 1 or headings[0] != matching_headings[0]:
            raise RuntimeError(
                f"CHANGELOG.md: release {version} already exists but is not "
                "the first release heading"
            )
        return changelog

    unreleased_pattern = re.compile(r"^##\s+Unreleased\s*$", re.MULTILINE)
    unreleased_headings = list(unreleased_pattern.finditer(changelog))
    if len(unreleased_headings) > 1:
        raise RuntimeError(
            "CHANGELOG.md: expected at most one Unreleased heading; found "
            f"{len(unreleased_headings)}"
        )

    if unreleased_headings:
        changelog = unreleased_pattern.sub(f"## {version}", changelog, count=1)
    else:
        # This repository keeps the current release first and does not require
        # a permanent Unreleased section. Start an empty section for the
        # maintainer to fill before the release tag is created.
        changelog = f"## {version}\n\n" + changelog.lstrip("\ufeff\r\n")

    write("CHANGELOG.md", changelog)
    return changelog


def generate_changeinfo(changelog: str) -> None:
    groups: list[dict[str, object]] = []
    current_group: dict[str, object] | None = None

    for line in changelog.splitlines():
        heading = re.fullmatch(r"##\s+([0-9]+\.[0-9]+\.[0-9]+)\s*", line)
        if heading:
            app_version = vita_app_version(heading.group(1))
            if current_group is None or current_group["version"] != app_version:
                current_group = {"version": app_version, "lines": []}
                groups.append(current_group)
        if current_group is not None:
            current_group["lines"].append(line.strip())  # type: ignore[union-attr]

    if not groups:
        raise RuntimeError("CHANGELOG.md: no numeric release headings were found")
    if "]]>" in changelog:
        raise RuntimeError("CHANGELOG.md: ']]>' cannot be embedded in change-info CDATA")

    prefix = '<?xml version="1.0" encoding="UTF-8"?>\n<changeinfo>\n'
    suffix = "</changeinfo>\n"
    entries: list[str] = []
    encoded_size = len(prefix.encode("utf-8")) + len(suffix.encode("utf-8"))

    for group in groups:
        lines = group["lines"]
        entry = (
            f'<changes app_ver="{group["version"]}"><![CDATA[\n'
            + "<br>\n".join(lines)  # type: ignore[arg-type]
            + "]]></changes>\n"
        )
        entry_size = len(entry.encode("utf-8"))
        if encoded_size + entry_size > 65536:
            break
        entries.append(entry)
        encoded_size += entry_size

    write("resources/changeinfo.xml", prefix + "".join(entries) + suffix)


def main() -> int:
    version = (
        sys.argv[1]
        if len(sys.argv) >= 2
        else input("new release version? ").strip()
    )
    if not VERSION_PATTERN.fullmatch(version):
        print("version format must be A.B.C", file=sys.stderr)
        return 1

    versions = tuple(int(component) for component in version.split("."))
    assembly_version = f"{version}.0"
    changelog = advance_changelog(version)

    template = read("sce_sys/livearea/contents/template.xml.format")
    write(
        "sce_sys/livearea/contents/template.xml",
        template.format(version=version),
    )

    replace_required(
        "CMakeLists.txt",
        r'^\s*set\(VERSION_MAJOR\b[^\n]*$',
        f'set(VERSION_MAJOR "{versions[0]}")',
    )
    replace_required(
        "CMakeLists.txt",
        r'^\s*set\(VERSION_MINOR\b[^\n]*$',
        f'set(VERSION_MINOR "{versions[1]}")',
    )
    replace_required(
        "CMakeLists.txt",
        r'^\s*set\(VERSION_PATCH\b[^\n]*$',
        f'set(VERSION_PATCH "{versions[2]}")',
    )
    replace_required(
        "host/VitaMoonlight.Host/VitaMoonlight.Host.csproj",
        r"<Version>[^<]+</Version>",
        f"<Version>{version}</Version>",
    )
    replace_required(
        "host/VitaMoonlight.Host/VitaMoonlight.Host.csproj",
        r"<InformationalVersion>[^<]+</InformationalVersion>",
        f"<InformationalVersion>{version}</InformationalVersion>",
    )
    replace_required(
        "host/VitaMoonlight.Host/VitaMoonlight.Host.csproj",
        r"<AssemblyVersion>[^<]+</AssemblyVersion>",
        f"<AssemblyVersion>{assembly_version}</AssemblyVersion>",
    )
    replace_required(
        "host/VitaMoonlight.Host/VitaMoonlight.Host.csproj",
        r"<FileVersion>[^<]+</FileVersion>",
        f"<FileVersion>{assembly_version}</FileVersion>",
    )
    replace_required(
        "host/installer/VitaMoonlightHost.iss",
        r"^AppVersion=.*$",
        f"AppVersion={version}",
    )
    replace_required(
        "host/installer/VitaMoonlightHost.iss",
        r"^VersionInfoVersion=.*$",
        f"VersionInfoVersion={assembly_version}",
    )
    replace_required(
        "docs/README.pod",
        r"=head1 VERSION\s+[^\s]+",
        f"=head1 VERSION\n\n{version}",
    )
    replace_required(
        "docs/CMakeLists.txt",
        r'--release="vita-moonlight [^"]+"',
        f'--release="vita-moonlight {version}"',
    )
    replace_required(
        "host/FINAL_RELEASE_CHECKLIST.md",
        r"Create and push `v[0-9]+\.[0-9]+\.[0-9]+`",
        f"Create and push `v{version}`",
    )

    generate_changeinfo(changelog)
    subprocess.run(
        [sys.executable, str(REPOSITORY_ROOT / "tools/check-version-consistency.py")],
        cwd=REPOSITORY_ROOT,
        check=True,
    )
    print(f"Release metadata advanced to {version}.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
