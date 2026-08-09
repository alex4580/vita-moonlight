#!/usr/bin/env python3
"""Verify and stage the offline, source-built Vita mbedTLS dependency."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import tarfile
import tempfile
from datetime import datetime, timezone
from pathlib import Path, PurePosixPath
from typing import Any


ROOT = Path(__file__).resolve().parents[1]
DEFAULT_LOCK_PATH = Path(__file__).with_name("vita-mbedtls-source.lock.json")
DEFAULT_VENDOR_PATH = ROOT / "vendor" / "vita-source"
LOCK_SCHEMA = "vita-moonlight/vita-mbedtls-source-lock/v1"
MANIFEST_SCHEMA = "vita-moonlight/vita-mbedtls-source-manifest/v1"
SHA256 = re.compile(r"^[0-9a-f]{64}$")
INPUTS = (
    (
        "mbedtls-3.6.7.tar.bz2",
        "official-source",
        "Apache-2.0 OR GPL-2.0-or-later",
    ),
    (
        "mbedtls-3.6.7-vita.patch",
        "project-vita-compatibility-patch",
        "GPL-3.0-or-later",
    ),
)
PATCHED_FILES = {
    "library/common.h",
    "library/entropy_poll.c",
    "library/net_sockets.c",
    "library/platform_util.c",
}
EXPECTED_BUILD = {
    "build_target": "mbedtls",
    "cmake_definitions": [
        "CMAKE_BUILD_TYPE=Release",
        "CMAKE_C_FLAGS=-D__vita__",
        "ENABLE_PROGRAMS=OFF",
        "ENABLE_TESTING=OFF",
        "MBEDTLS_FATAL_WARNINGS=OFF",
    ],
    "config_enable": ["MBEDTLS_THREADING_C", "MBEDTLS_THREADING_PTHREAD"],
    "install_prefix": "${VITASDK}/arm-vita-eabi",
    "patch_fuzz": 0,
    "patch_strip": 1,
    "toolchain_file": "${VITASDK}/share/vita.toolchain.cmake",
}


class ProvenanceError(RuntimeError):
    """Raised when a source-build input or recipe violates the lock."""


def exact_fields(value: dict[str, Any], expected: set[str], description: str) -> None:
    actual = set(value)
    if actual != expected:
        raise ProvenanceError(
            f"{description} fields changed: missing={sorted(expected - actual)}, "
            f"unexpected={sorted(actual - expected)}"
        )


def load_lock(path: Path) -> tuple[dict[str, Any], str]:
    try:
        raw = path.read_bytes()
        value = json.loads(raw)
    except OSError as exc:
        raise ProvenanceError(f"could not read mbedTLS source lock {path}: {exc}") from exc
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise ProvenanceError(f"invalid mbedTLS source lock JSON: {exc}") from exc
    if not isinstance(value, dict):
        raise ProvenanceError("mbedTLS source lock must be an object")
    exact_fields(value, {"schema", "locked_at_utc", "component", "inputs", "build"}, "lock")
    if value.get("schema") != LOCK_SCHEMA:
        raise ProvenanceError(f"unsupported mbedTLS source lock schema {value.get('schema')!r}")
    if re.fullmatch(
        r"[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z",
        str(value.get("locked_at_utc", "")),
    ) is None:
        raise ProvenanceError("mbedTLS source lock has an invalid UTC timestamp")

    component = value.get("component")
    if component != {
        "abi_line": "3.6-lts",
        "license_expression": "Apache-2.0 AND GPL-3.0-or-later",
        "name": "mbedtls",
        "version": "3.6.7",
    }:
        raise ProvenanceError("mbedTLS component identity changed")
    if value.get("build") != EXPECTED_BUILD:
        raise ProvenanceError("mbedTLS source-build recipe differs from the supported recipe")

    inputs = value.get("inputs")
    if not isinstance(inputs, list) or len(inputs) != len(INPUTS):
        raise ProvenanceError(f"mbedTLS source lock must contain {len(INPUTS)} inputs")
    for item, (expected_name, expected_role, expected_license) in zip(inputs, INPUTS):
        if not isinstance(item, dict):
            raise ProvenanceError(f"mbedTLS input {expected_name} is not an object")
        exact_fields(
            item,
            {"name", "role", "license_expression", "size", "sha256", "provenance"},
            expected_name,
        )
        if (
            item.get("name") != expected_name
            or item.get("role") != expected_role
            or item.get("license_expression") != expected_license
        ):
            raise ProvenanceError(f"mbedTLS input order or role changed at {expected_name}")
        if type(item.get("size")) is not int or item["size"] <= 0:
            raise ProvenanceError(f"{expected_name}: invalid byte count")
        if not isinstance(item.get("sha256"), str) or SHA256.fullmatch(item["sha256"]) is None:
            raise ProvenanceError(f"{expected_name}: invalid SHA-256")
        provenance = item.get("provenance")
        if not isinstance(provenance, dict):
            raise ProvenanceError(f"{expected_name}: missing provenance")
        exact_fields(
            provenance,
            {"asset_id", "path", "repository", "revision", "url"},
            f"{expected_name} provenance",
        )

    source, vita_patch = inputs
    if source["provenance"] != {
        "asset_id": 464469768,
        "path": "mbedtls-3.6.7.tar.bz2",
        "repository": "Mbed-TLS/mbedtls",
        "revision": "mbedtls-3.6.7",
        "url": "https://github.com/Mbed-TLS/mbedtls/releases/download/mbedtls-3.6.7/mbedtls-3.6.7.tar.bz2",
    }:
        raise ProvenanceError("official mbedTLS source provenance changed")
    if vita_patch["provenance"] != {
        "asset_id": None,
        "path": "vendor/vita-source/mbedtls-3.6.7-vita.patch",
        "repository": "alex4580/vita-moonlight",
        "revision": "release-tree",
        "url": None,
    }:
        raise ProvenanceError("project mbedTLS compatibility-patch provenance changed")
    return value, hashlib.sha256(raw).hexdigest()


def verify_file(path: Path, item: dict[str, Any], output_directory: Path | None) -> None:
    if path.is_symlink() or not path.is_file():
        raise ProvenanceError(f"{item['name']}: expected a regular vendored input at {path}")
    destination = output_directory / item["name"] if output_directory else None
    temporary = destination.with_name(destination.name + ".partial") if destination else None
    target = None
    digest = hashlib.sha256()
    count = 0
    try:
        if temporary is not None:
            temporary.unlink(missing_ok=True)
            target = temporary.open("wb")
        with path.open("rb") as source:
            while True:
                chunk = source.read(1024 * 1024)
                if not chunk:
                    break
                if target is not None:
                    target.write(chunk)
                digest.update(chunk)
                count += len(chunk)
        if target is not None:
            target.close()
            target = None
        if count != item["size"] or digest.hexdigest() != item["sha256"]:
            raise ProvenanceError(
                f"{item['name']}: vendored bytes differ from lock "
                f"(size={count}, sha256={digest.hexdigest()})"
            )
        if temporary is not None and destination is not None:
            temporary.replace(destination)
    except OSError as exc:
        raise ProvenanceError(f"{item['name']}: could not verify or stage: {exc}") from exc
    finally:
        if target is not None:
            target.close()
        if temporary is not None:
            temporary.unlink(missing_ok=True)


def safe_archive_members(archive_path: Path) -> None:
    seen: set[str] = set()
    try:
        with tarfile.open(archive_path, mode="r:bz2") as archive:
            for member in archive:
                member_path = PurePosixPath(member.name)
                if (
                    not member.name
                    or "\\" in member.name
                    or member_path.is_absolute()
                    or ".." in member_path.parts
                    or not member_path.parts
                    or member_path.parts[0] != "mbedtls-3.6.7"
                ):
                    raise ProvenanceError(
                        f"mbedtls source archive contains unsafe path {member.name!r}"
                    )
                if member.name in seen:
                    raise ProvenanceError(
                        f"mbedtls source archive contains duplicate path {member.name!r}"
                    )
                seen.add(member.name)
                if not member.isfile() and not member.isdir():
                    raise ProvenanceError(
                        f"mbedtls source archive contains unsupported entry {member.name!r}"
                    )
    except (OSError, tarfile.TarError) as exc:
        raise ProvenanceError(f"invalid official mbedTLS source archive: {exc}") from exc
    if not seen:
        raise ProvenanceError("official mbedTLS source archive is empty")


def read_locked_text(path: Path, description: str) -> str:
    try:
        raw = path.read_bytes()
        text = raw.decode("utf-8")
    except (OSError, UnicodeDecodeError) as exc:
        raise ProvenanceError(f"could not read {description}: {exc}") from exc
    if "\r" in text:
        raise ProvenanceError(f"{description} must use canonical LF line endings")
    return text


def find_git() -> str:
    candidate = shutil.which("git")
    if candidate:
        return candidate
    windows_candidate = (
        Path(os.environ.get("ProgramFiles", r"C:\Program Files"))
        / "Git"
        / "cmd"
        / "git.exe"
    )
    if windows_candidate.is_file():
        return str(windows_candidate)
    raise ProvenanceError("Git is required to verify the exact-context Vita patch")


def run_checked(command: list[str], cwd: Path, description: str) -> str:
    try:
        result = subprocess.run(
            command,
            cwd=cwd,
            check=False,
            capture_output=True,
            text=True,
            timeout=120,
        )
    except (OSError, subprocess.TimeoutExpired) as exc:
        raise ProvenanceError(f"could not run {description}: {exc}") from exc
    output = (result.stdout + result.stderr).strip()
    if result.returncode != 0:
        raise ProvenanceError(f"{description} failed with exit {result.returncode}: {output}")
    return output


def assert_patch_contract(text: str) -> None:
    if not text.startswith(
        "Vita Moonlight mbedTLS compatibility patch\n"
        "Copyright (C) 2026 Vita Moonlight contributors\n"
        "SPDX-License-Identifier: GPL-3.0-or-later\n"
    ):
        raise ProvenanceError("mbedTLS Vita patch is missing its project license header")
    changed = set(
        match.group(1)
        for match in re.finditer(r"^diff --git a/(\S+) b/\S+$", text, re.MULTILINE)
    )
    if changed != PATCHED_FILES:
        raise ProvenanceError(
            f"mbedTLS Vita patch changes unexpected files: {sorted(changed)}"
        )
    requirements = {
        "Vita Unix-like platform classification": "+    defined(__vita__) ||",
        "Vita platform entropy allowance": "+    !defined(__vita__)",
        "bounded Vita getentropy wrapper": "+        len = buflen > 256 ? 256 : buflen;",
        "Vita getentropy failure handling": "+        if (getentropy(buf, len) == -1)",
        "initialized Vita socket state": "+    long b = 0L;",
        "checked Vita socket state query": "+    if (getsockopt(ctx->fd, SOL_SOCKET, SO_NONBLOCK, &b, &b_size) != 0 ||",
        "fail-closed Vita socket state query": "+        b != 1L) {",
        "Vita blocking socket mode": "+    long b = 0L;",
        "Vita non-blocking socket mode": "+    long b = 1L;",
        "Vita socklen_t declaration": "+    defined(__vita__)",
        "Vita monotonic-time implementation": "+    defined(__HAIKU__) || defined(__vita__)",
    }
    for description, needle in requirements.items():
        if needle not in text:
            raise ProvenanceError(f"mbedTLS Vita patch is missing {description}")
    changed_file_notice = (
        "+/* Modified for PlayStation Vita by Vita Moonlight contributors, 2026. */"
    )
    if text.count(changed_file_notice) != len(PATCHED_FILES):
        raise ProvenanceError(
            "every modified mbedTLS file must carry the Apache-2.0 changed-file notice"
        )
    if text.count("+#elif defined(__vita__)") != 4:
        raise ProvenanceError("mbedTLS Vita patch must contain four explicit Vita runtime branches")


def assert_patched_sources(source_root: Path) -> None:
    expected = {
        "library/common.h": ["defined(__vita__)", "#define MBEDTLS_PLATFORM_IS_UNIXLIKE"],
        "library/entropy_poll.c": [
            "vita_getentropy_wrapper",
            "len = buflen > 256 ? 256 : buflen;",
            "MBEDTLS_ERR_ENTROPY_SOURCE_FAILED",
        ],
        "library/net_sockets.c": [
            "long b = 0L;\n    socklen_t b_size = sizeof(b);",
            "if (getsockopt(ctx->fd, SOL_SOCKET, SO_NONBLOCK, &b, &b_size) != 0 ||\n        b != 1L) {",
            "errno = err;\n        return 0;",
            "long b = 0L;",
            "long b = 1L;",
            "setsockopt(ctx->fd, SOL_SOCKET, SO_NONBLOCK",
        ],
        "library/platform_util.c": ["defined(__HAIKU__) || defined(__vita__)"],
    }
    for relative, needles in expected.items():
        text = (source_root / relative).read_text(encoding="utf-8")
        if (
            "Modified for PlayStation Vita by Vita Moonlight contributors, 2026."
            not in text
        ):
            raise ProvenanceError(f"patched {relative} is missing its changed-file notice")
        for needle in needles:
            if needle not in text:
                raise ProvenanceError(f"patched {relative} is missing {needle!r}")


def verify_patch_application(vendor_directory: Path) -> None:
    archive_path = vendor_directory / INPUTS[0][0]
    patch_path = (vendor_directory / INPUTS[1][0]).resolve()
    patch_text = read_locked_text(
        patch_path, "project mbedTLS Vita compatibility patch"
    )
    assert_patch_contract(patch_text)

    git_executable = find_git()
    with tempfile.TemporaryDirectory(prefix="vita-mbedtls-") as temporary:
        temporary_path = Path(temporary)
        with tarfile.open(archive_path, mode="r:bz2") as archive:
            archive.extractall(temporary_path, filter="data")
        source_root = temporary_path / "mbedtls-3.6.7"
        command = [git_executable, "apply", str(patch_path)]
        run_checked(
            [git_executable, "apply", "--check", str(patch_path)],
            source_root,
            "mbedTLS Vita exact-context patch check",
        )
        run_checked(command, source_root, "mbedTLS Vita exact-context patch application")
        assert_patched_sources(source_root)
        config_runner = (
            "import runpy,sys; "
            "sys.path.insert(0, 'scripts'); "
            "sys.argv=['scripts/config.py', *sys.argv[1:]]; "
            "runpy.run_path('scripts/config.py', run_name='__main__')"
        )
        for option in EXPECTED_BUILD["config_enable"]:
            run_checked(
                [sys.executable, "-c", config_runner, "set", option],
                source_root,
                f"mbedTLS config enable {option}",
            )
        config = (source_root / "include" / "mbedtls" / "mbedtls_config.h").read_text(
            encoding="utf-8"
        )
        for option in EXPECTED_BUILD["config_enable"]:
            if f"#define {option}" not in config:
                raise ProvenanceError(f"mbedTLS config script did not enable {option}")


def verify_and_stage(
    lock: dict[str, Any], vendor_directory: Path, output_directory: Path | None
) -> None:
    if vendor_directory.is_symlink() or not vendor_directory.is_dir():
        raise ProvenanceError(f"missing regular mbedTLS vendor directory: {vendor_directory}")
    actual = sorted(path.name for path in vendor_directory.iterdir())
    expected = sorted(name for name, _, _ in INPUTS)
    if actual != expected:
        raise ProvenanceError(
            "mbedTLS vendor set differs from lock: "
            f"missing={sorted(set(expected) - set(actual))}, "
            f"unexpected={sorted(set(actual) - set(expected))}"
        )
    if output_directory is not None:
        if output_directory.resolve() == vendor_directory.resolve():
            raise ProvenanceError("--output-dir must not overwrite mbedTLS vendor inputs")
        output_directory.mkdir(parents=True, exist_ok=True)
    for item in lock["inputs"]:
        verify_file(vendor_directory / item["name"], item, output_directory)
        print(
            f"verified mbedTLS input {item['name']}: {item['size']} bytes, "
            f"sha256:{item['sha256']}"
        )
    safe_archive_members(vendor_directory / INPUTS[0][0])
    verify_patch_application(vendor_directory)


def write_manifest(path: Path, lock: dict[str, Any], lock_sha256: str) -> None:
    manifest = {
        "schema": MANIFEST_SCHEMA,
        "source_lock": {
            "schema": LOCK_SCHEMA,
            "sha256": lock_sha256,
            "locked_at_utc": lock["locked_at_utc"],
        },
        "component": lock["component"],
        "inputs": lock["inputs"],
        "build": lock["build"],
        "verified_at_utc": datetime.now(timezone.utc)
        .replace(microsecond=0)
        .isoformat()
        .replace("+00:00", "Z"),
    }
    temporary = path.with_name(path.name + ".partial")
    try:
        path.parent.mkdir(parents=True, exist_ok=True)
        temporary.unlink(missing_ok=True)
        temporary.write_text(
            json.dumps(manifest, indent=2, sort_keys=True) + "\n",
            encoding="utf-8",
            newline="\n",
        )
        temporary.replace(path)
    except OSError as exc:
        raise ProvenanceError(f"could not write mbedTLS manifest {path}: {exc}") from exc
    finally:
        temporary.unlink(missing_ok=True)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--lock", type=Path, default=DEFAULT_LOCK_PATH)
    parser.add_argument("--vendor-dir", type=Path, default=DEFAULT_VENDOR_PATH)
    parser.add_argument("--output-dir", type=Path)
    parser.add_argument("--manifest", type=Path)
    parser.add_argument("--self-test", action="store_true")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    if args.self_test and (args.output_dir is not None or args.manifest is not None):
        raise ProvenanceError("--self-test cannot stage inputs or write a manifest")
    if not args.self_test and (args.output_dir is None or args.manifest is None):
        raise ProvenanceError("--output-dir and --manifest are required")
    lock, lock_sha256 = load_lock(args.lock)
    if args.manifest is not None:
        manifest_path = args.manifest.resolve()
        vendor_path = args.vendor_dir.resolve()
        if manifest_path == args.lock.resolve() or (
            manifest_path == vendor_path or vendor_path in manifest_path.parents
        ):
            raise ProvenanceError(
                "--manifest must not overwrite the source lock or vendor inputs"
            )
        try:
            args.manifest.unlink(missing_ok=True)
        except OSError as exc:
            raise ProvenanceError(
                f"could not remove stale mbedTLS manifest {args.manifest}: {exc}"
            ) from exc
    verify_and_stage(lock, args.vendor_dir, args.output_dir)
    if args.self_test:
        print(
            "Vita mbedTLS source self-test passed: "
            f"version=3.6.7, lock_sha256={lock_sha256}"
        )
        return 0
    if args.manifest is None:
        raise AssertionError("manifest path validation failed")
    write_manifest(args.manifest, lock, lock_sha256)
    print(f"wrote Vita mbedTLS source manifest: {args.manifest}")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except ProvenanceError as exc:
        print(f"Vita mbedTLS source verification failed: {exc}", file=sys.stderr)
        raise SystemExit(1)
