#!/usr/bin/env python3
"""Verify, download, and stage every source-built Vita client dependency."""

from __future__ import annotations

import argparse
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import subprocess
import sys
import tarfile
import tempfile
import urllib.error
import urllib.request


ROOT = Path(__file__).resolve().parents[1]
DEFAULT_LOCK = Path(__file__).with_name("vita-corresponding-source.lock.json")
LOCK_SCHEMA = "vita-moonlight/vita-corresponding-source-lock/v1"
MANIFEST_SCHEMA = "vita-moonlight/vita-source-dependency-manifest/v1"
HEX40 = re.compile(r"[0-9a-f]{40}")
HEX64 = re.compile(r"[0-9a-f]{64}")
SAFE_NAME = re.compile(r"[A-Za-z0-9][A-Za-z0-9._+-]*")
EXPECTED_ORDER = [
    "zlib",
    "bzip2",
    "zstd",
    "libpng",
    "libjpeg-turbo",
    "FreeType",
    "libvita2d",
    "Expat",
    "Opus",
    "Mbed TLS",
    "curl",
]


class DependencySourceError(RuntimeError):
    """Raised when dependency provenance or staging fails closed."""


def utc_now() -> str:
    return datetime.now(timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z")


def read_json(path: Path) -> dict:
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise DependencySourceError(f"could not read JSON {path}: {exc}") from exc
    if not isinstance(value, dict):
        raise DependencySourceError(f"{path} must contain a JSON object")
    return value


def write_json(path: Path, value: dict) -> None:
    temporary = path.with_name(path.name + ".partial")
    try:
        path.parent.mkdir(parents=True, exist_ok=True)
        temporary.unlink(missing_ok=True)
        temporary.write_text(
            json.dumps(value, indent=2, sort_keys=True) + "\n",
            encoding="utf-8",
            newline="\n",
        )
        temporary.replace(path)
    except OSError as exc:
        raise DependencySourceError(f"could not write {path}: {exc}") from exc
    finally:
        temporary.unlink(missing_ok=True)


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    try:
        with path.open("rb") as source:
            while chunk := source.read(1024 * 1024):
                digest.update(chunk)
    except OSError as exc:
        raise DependencySourceError(f"could not hash {path}: {exc}") from exc
    return digest.hexdigest()


def safe_relative(value: str, description: str) -> PurePosixPath:
    path = PurePosixPath(value)
    if (
        not value
        or "\\" in value
        or path.is_absolute()
        or any(part in ("", ".", "..") for part in path.parts)
    ):
        raise DependencySourceError(f"unsafe {description}: {value!r}")
    return path


def locked_regular_file(path: Path, size: int, sha256: str, description: str) -> None:
    if path.is_symlink() or not path.is_file():
        raise DependencySourceError(f"{description}: expected a regular file at {path}")
    try:
        actual_size = path.stat().st_size
    except OSError as exc:
        raise DependencySourceError(f"could not stat {path}: {exc}") from exc
    actual_sha256 = sha256_file(path)
    if actual_size != size or actual_sha256 != sha256:
        raise DependencySourceError(
            f"{description}: bytes differ from lock "
            f"(size={actual_size}, sha256={actual_sha256})"
        )


def validate_lock(lock: dict) -> dict:
    if lock.get("schema") != LOCK_SCHEMA:
        raise DependencySourceError(f"unsupported source lock schema {lock.get('schema')!r}")
    if lock.get("known_distribution_blockers") != []:
        raise DependencySourceError("dependency source lock still contains distribution blockers")
    if "binary_package_build" in lock:
        raise DependencySourceError("source build must not retain VitaSDK binary-package provenance")

    recipe = lock.get("dependency_source_build")
    if not isinstance(recipe, dict):
        raise DependencySourceError("dependency_source_build must be an object")
    required_recipe_fields = {
        "license_expression",
        "recipe",
        "recipe_sha256",
        "stager",
        "stager_sha256",
        "mbedtls_source_lock_sha256",
        "build_order",
        "source_directories",
        "patches",
        "required_outputs",
    }
    if set(recipe) != required_recipe_fields:
        raise DependencySourceError(
            "dependency_source_build fields changed: "
            f"missing={sorted(required_recipe_fields - set(recipe))}, "
            f"unexpected={sorted(set(recipe) - required_recipe_fields)}"
        )
    if recipe["license_expression"] != "GPL-3.0-or-later":
        raise DependencySourceError("project dependency recipe must remain GPL-3.0-or-later")
    if recipe["build_order"] != EXPECTED_ORDER:
        raise DependencySourceError("dependency build order differs from the supported chain")

    for key, expected in (
        ("recipe", "tools/build-vita-dependencies.sh"),
        ("stager", "tools/stage-vita-dependency-sources.py"),
    ):
        if recipe.get(key) != expected:
            raise DependencySourceError(f"unexpected {key} path {recipe.get(key)!r}")
        digest = recipe.get(f"{key}_sha256")
        if not isinstance(digest, str) or HEX64.fullmatch(digest) is None:
            raise DependencySourceError(f"invalid {key} SHA-256")
        path = ROOT / expected
        if path.is_symlink() or not path.is_file() or sha256_file(path) != digest:
            raise DependencySourceError(f"{expected} differs from its source-build lock")

    mbed_lock_path = ROOT / str(lock.get("mbedtls_source_lock", ""))
    mbed_lock = read_json(mbed_lock_path)
    if mbed_lock.get("schema") != "vita-moonlight/vita-mbedtls-source-lock/v1":
        raise DependencySourceError("unexpected mbedTLS source-lock schema")
    if sha256_file(mbed_lock_path) != recipe["mbedtls_source_lock_sha256"]:
        raise DependencySourceError("mbedTLS source lock differs from dependency recipe")

    directories = recipe.get("source_directories")
    if not isinstance(directories, list) or len(directories) != len(EXPECTED_ORDER):
        raise DependencySourceError("source_directories must cover the full build order")
    if [item.get("component") for item in directories if isinstance(item, dict)] != EXPECTED_ORDER:
        raise DependencySourceError("source directory order differs from build order")
    archive_names = {
        item.get("name"): item
        for item in lock.get("archive_inputs", [])
        if isinstance(item, dict)
    }
    git_components = {
        item.get("component"): item
        for item in lock.get("git_inputs", [])
        if isinstance(item, dict)
    }
    seen_directories: set[str] = set()
    for item in directories:
        if not isinstance(item, dict) or set(item) != {"component", "directory", "kind", "source"}:
            raise DependencySourceError("every source directory needs component/directory/kind/source")
        directory = item["directory"]
        safe_relative(directory, "staged source directory")
        if "/" in directory or directory in seen_directories:
            raise DependencySourceError(f"invalid or duplicate staged directory {directory!r}")
        seen_directories.add(directory)
        if item["kind"] == "archive":
            if item["source"] not in archive_names:
                raise DependencySourceError(f"unknown locked archive {item['source']!r}")
        elif item["kind"] == "git":
            source = git_components.get(item["component"])
            if source is None or source.get("destination") != item["source"]:
                raise DependencySourceError(f"unknown locked git source for {item['component']}")
        elif item["kind"] == "vendored-mbedtls":
            if item["component"] != "Mbed TLS" or item["source"] != lock["mbedtls_source_lock"]:
                raise DependencySourceError("invalid vendored mbedTLS source mapping")
        else:
            raise DependencySourceError(f"unsupported source kind {item['kind']!r}")

    patches = recipe.get("patches")
    if not isinstance(patches, list):
        raise DependencySourceError("patches must be an array")
    expected_patch_components = {"zstd", "libpng", "libvita2d", "Opus"}
    actual_patch_components: set[str] = set()
    for item in patches:
        if not isinstance(item, dict) or set(item) != {"component", "path", "sha256", "size"}:
            raise DependencySourceError("every project patch needs component/path/size/sha256")
        relative = safe_relative(str(item["path"]), "project patch path")
        if relative.parts[:2] != ("patches", "vita-dependencies"):
            raise DependencySourceError(f"project patch is outside the reviewed directory: {relative}")
        if type(item["size"]) is not int or item["size"] <= 0 or HEX64.fullmatch(str(item["sha256"])) is None:
            raise DependencySourceError(f"invalid patch lock for {item['component']}")
        path = ROOT.joinpath(*relative.parts)
        locked_regular_file(path, item["size"], item["sha256"], f"{item['component']} patch")
        text = path.read_text(encoding="utf-8")
        if "SPDX-License-Identifier: GPL-3.0-or-later" not in text:
            raise DependencySourceError(f"{relative}: missing project GPL license marker")
        actual_patch_components.add(item["component"])
    if actual_patch_components != expected_patch_components:
        raise DependencySourceError("project patch set differs from the supported set")

    outputs = recipe.get("required_outputs")
    if not isinstance(outputs, list) or len(outputs) < len(EXPECTED_ORDER):
        raise DependencySourceError("required_outputs does not cover every dependency")
    for output in outputs:
        relative = safe_relative(str(output), "installed output")
        if relative.parts[0] != "arm-vita-eabi":
            raise DependencySourceError(f"installed output escapes target prefix: {output!r}")
    return recipe


def obtain_archive(item: dict, cache: Path) -> Path:
    name = item["name"]
    if not isinstance(name, str) or SAFE_NAME.fullmatch(name) is None:
        raise DependencySourceError(f"invalid archive name {name!r}")
    if not str(item.get("url", "")).startswith("https://"):
        raise DependencySourceError(f"{name}: source URL must use HTTPS")
    if type(item.get("size")) is not int or item["size"] <= 0:
        raise DependencySourceError(f"{name}: invalid locked size")
    if HEX64.fullmatch(str(item.get("sha256", ""))) is None:
        raise DependencySourceError(f"{name}: invalid locked SHA-256")
    cache.mkdir(parents=True, exist_ok=True)
    target = cache / name
    if target.exists():
        locked_regular_file(target, item["size"], item["sha256"], name)
        return target

    partial = target.with_name(target.name + ".partial")
    partial.unlink(missing_ok=True)
    request = urllib.request.Request(
        item["url"], headers={"User-Agent": "vita-moonlight-dependency-source/1"}
    )
    try:
        with urllib.request.urlopen(request, timeout=60) as response, partial.open("wb") as output:
            declared = response.headers.get("Content-Length")
            if declared is not None and int(declared) != item["size"]:
                raise DependencySourceError(
                    f"{name}: server length {declared} differs from source lock"
                )
            copied = 0
            while chunk := response.read(1024 * 1024):
                copied += len(chunk)
                if copied > item["size"]:
                    raise DependencySourceError(f"{name}: download exceeded locked size")
                output.write(chunk)
    except (OSError, ValueError, urllib.error.URLError) as exc:
        partial.unlink(missing_ok=True)
        if isinstance(exc, DependencySourceError):
            raise
        raise DependencySourceError(f"could not download {name}: {exc}") from exc
    locked_regular_file(partial, item["size"], item["sha256"], name)
    partial.replace(target)
    return target


def validate_tar_members(archive: tarfile.TarFile, expected_root: str) -> None:
    seen: set[str] = set()
    for member in archive.getmembers():
        name = member.name.rstrip("/")
        relative = safe_relative(name, "archive member")
        if relative.parts[0] != expected_root:
            raise DependencySourceError(
                f"archive member {member.name!r} is outside {expected_root!r}"
            )
        if name in seen:
            raise DependencySourceError(f"duplicate archive member {member.name!r}")
        seen.add(name)
        if member.isdev() or member.isfifo():
            raise DependencySourceError(f"unsupported archive member {member.name!r}")
        if member.issym() or member.islnk():
            link = PurePosixPath(member.linkname)
            if link.is_absolute() or ".." in link.parts or "\\" in member.linkname:
                raise DependencySourceError(f"unsafe archive link {member.name!r}")
    if not seen:
        raise DependencySourceError("source archive is empty")


def extract_archive(source: Path, destination: Path, expected_root: str) -> None:
    try:
        with tarfile.open(source, "r:*") as archive:
            validate_tar_members(archive, expected_root)
            archive.extractall(destination, filter="data")
    except (OSError, tarfile.TarError) as exc:
        raise DependencySourceError(f"could not safely extract {source.name}: {exc}") from exc
    root = destination / expected_root
    if root.is_symlink() or not root.is_dir():
        raise DependencySourceError(f"{source.name}: expected extracted root {expected_root}")


def run(command: list[str], *, cwd: Path | None = None, env: dict[str, str] | None = None) -> str:
    try:
        result = subprocess.run(
            command,
            cwd=cwd,
            env=env,
            check=False,
            capture_output=True,
            text=True,
            timeout=300,
        )
    except (OSError, subprocess.TimeoutExpired) as exc:
        raise DependencySourceError(f"could not run {' '.join(command)}: {exc}") from exc
    output = (result.stdout + result.stderr).strip()
    if result.returncode != 0:
        raise DependencySourceError(
            f"command failed with exit {result.returncode}: {' '.join(command)}: {output}"
        )
    return result.stdout.strip()


def export_git_source(item: dict, destination: Path) -> None:
    commit = str(item.get("commit", ""))
    repository = str(item.get("repository", ""))
    if HEX40.fullmatch(commit) is None:
        raise DependencySourceError(f"{item.get('component')}: invalid git commit")
    if not repository.startswith("https://github.com/") or not repository.endswith(".git"):
        raise DependencySourceError(f"{item.get('component')}: unsupported git repository")
    with tempfile.TemporaryDirectory(prefix="vita-dependency-git-") as temporary:
        clone = Path(temporary) / "repository"
        archive_path = Path(temporary) / "source.tar"
        run(["git", "init", "--quiet", str(clone)])
        run(["git", "-C", str(clone), "remote", "add", "origin", repository])
        run(["git", "-C", str(clone), "fetch", "--quiet", "--depth=1", "origin", commit])
        actual = run(["git", "-C", str(clone), "rev-parse", "FETCH_HEAD"])
        if actual != commit:
            raise DependencySourceError(f"{item['component']}: fetched {actual}, expected {commit}")
        with archive_path.open("wb") as output:
            try:
                subprocess.run(
                    ["git", "-C", str(clone), "archive", "--format=tar", commit],
                    check=True,
                    stdout=output,
                    stderr=subprocess.PIPE,
                )
            except (OSError, subprocess.CalledProcessError) as exc:
                raise DependencySourceError(f"could not export {item['component']}: {exc}") from exc
        destination.mkdir(parents=True)
        try:
            with tarfile.open(archive_path, "r:") as archive:
                for member in archive.getmembers():
                    safe_relative(member.name.rstrip("/"), "git archive member")
                    if member.isdev() or member.isfifo():
                        raise DependencySourceError(
                            f"unsupported git archive member {member.name!r}"
                        )
                archive.extractall(destination, filter="data")
        except (OSError, tarfile.TarError) as exc:
            raise DependencySourceError(f"could not extract {item['component']}: {exc}") from exc


def apply_project_patch(source_root: Path, patch: Path, ceiling: Path) -> None:
    environment = os.environ.copy()
    environment["GIT_CEILING_DIRECTORIES"] = str(ceiling.resolve())
    run(["git", "apply", "--check", str(patch.resolve())], cwd=source_root, env=environment)
    run(["git", "apply", str(patch.resolve())], cwd=source_root, env=environment)
    run(
        ["git", "apply", "--reverse", "--check", str(patch.resolve())],
        cwd=source_root,
        env=environment,
    )


def copy_mbedtls_inputs(lock: dict, destination: Path) -> list[dict]:
    mbed_lock = read_json(ROOT / lock["mbedtls_source_lock"])
    vendor = ROOT / "vendor" / "vita-source"
    destination.mkdir(parents=True)
    result: list[dict] = []
    for item in mbed_lock.get("inputs", []):
        source = vendor / item["name"]
        locked_regular_file(source, item["size"], item["sha256"], item["name"])
        shutil.copy2(source, destination / item["name"])
        result.append(
            {
                "component": "Mbed TLS",
                "kind": "vendored-mbedtls",
                "name": item["name"],
                "sha256": item["sha256"],
                "size": item["size"],
            }
        )
    return result


def stage_sources(lock: dict, recipe: dict, output: Path, cache: Path) -> list[dict]:
    if output.exists():
        raise DependencySourceError(f"refusing to reuse staged source directory {output}")
    output.mkdir(parents=True)
    archives = {
        item["name"]: item
        for item in lock["archive_inputs"]
        if isinstance(item, dict)
    }
    git_inputs = {
        item["destination"]: item
        for item in lock["git_inputs"]
        if isinstance(item, dict)
    }
    staged: list[dict] = []
    try:
        for mapping in recipe["source_directories"]:
            destination = output / mapping["directory"]
            if mapping["kind"] == "archive":
                item = archives[mapping["source"]]
                archive = obtain_archive(item, cache)
                extract_archive(archive, output, mapping["directory"])
                staged.append(
                    {
                        "component": mapping["component"],
                        "kind": "archive",
                        "name": item["name"],
                        "sha256": item["sha256"],
                        "size": item["size"],
                        "url": item["url"],
                        "version": item["version"],
                    }
                )
            elif mapping["kind"] == "git":
                item = git_inputs[mapping["source"]]
                export_git_source(item, destination)
                staged.append(
                    {
                        "commit": item["commit"],
                        "component": mapping["component"],
                        "kind": "git",
                        "repository": item["repository"],
                    }
                )
            else:
                staged.extend(copy_mbedtls_inputs(lock, destination))

        mapping_by_component = {
            item["component"]: item for item in recipe["source_directories"]
        }
        for item in recipe["patches"]:
            source_root = output / mapping_by_component[item["component"]]["directory"]
            apply_project_patch(source_root, ROOT / item["path"], output.parent)
    except Exception:
        shutil.rmtree(output, ignore_errors=True)
        raise
    return staged


def initial_manifest(lock_path: Path, lock: dict, recipe: dict, sources: list[dict]) -> dict:
    return {
        "schema": MANIFEST_SCHEMA,
        "source_lock": {
            "schema": LOCK_SCHEMA,
            "path": str(lock_path.relative_to(ROOT)).replace("\\", "/"),
            "sha256": sha256_file(lock_path),
            "locked_at_utc": lock["locked_at_utc"],
        },
        "dependency_source_build": recipe,
        "sources": sources,
        "build_verification": {"status": "pending", "outputs": []},
        "staged_at_utc": utc_now(),
    }


def finalize_manifest(lock_path: Path, lock: dict, recipe: dict, manifest_path: Path, sdk: Path) -> None:
    manifest = read_json(manifest_path)
    expected_lock = {
        "schema": LOCK_SCHEMA,
        "path": str(lock_path.relative_to(ROOT)).replace("\\", "/"),
        "sha256": sha256_file(lock_path),
        "locked_at_utc": lock["locked_at_utc"],
    }
    if (
        manifest.get("schema") != MANIFEST_SCHEMA
        or manifest.get("source_lock") != expected_lock
        or manifest.get("dependency_source_build") != recipe
        or manifest.get("build_verification") != {"status": "pending", "outputs": []}
    ):
        raise DependencySourceError("pending dependency manifest is not bound to this source lock")
    if sdk.is_symlink() or not sdk.is_dir():
        raise DependencySourceError(f"VitaSDK root is missing: {sdk}")
    outputs: list[dict] = []
    for relative_text in recipe["required_outputs"]:
        relative = safe_relative(relative_text, "installed output")
        path = sdk.joinpath(*relative.parts)
        if path.is_symlink() or not path.is_file():
            raise DependencySourceError(f"required source-built output is missing: {path}")
        outputs.append(
            {
                "path": relative_text,
                "sha256": sha256_file(path),
                "size": path.stat().st_size,
            }
        )
    manifest["build_verification"] = {
        "status": "complete",
        "completed_at_utc": utc_now(),
        "outputs": outputs,
    }
    write_json(manifest_path, manifest)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--lock", type=Path, default=DEFAULT_LOCK)
    parser.add_argument("--output-dir", type=Path)
    parser.add_argument("--cache-dir", type=Path, default=ROOT / ".tools" / "vita-dependency-source-cache")
    parser.add_argument("--manifest", type=Path)
    parser.add_argument("--finalize-install", type=Path)
    parser.add_argument("--self-test", action="store_true")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    lock_path = args.lock.resolve()
    try:
        lock_path.relative_to(ROOT)
    except ValueError as exc:
        raise DependencySourceError("source lock must be inside the repository") from exc
    lock = read_json(lock_path)
    recipe = validate_lock(lock)

    if args.self_test:
        if args.output_dir or args.manifest or args.finalize_install:
            raise DependencySourceError("--self-test cannot stage or finalize")
        print(
            "Vita dependency source self-test passed: "
            f"components={len(EXPECTED_ORDER)}, patches={len(recipe['patches'])}, "
            f"outputs={len(recipe['required_outputs'])}"
        )
        return 0
    if args.finalize_install is not None:
        if args.manifest is None or args.output_dir is not None:
            raise DependencySourceError("--finalize-install requires --manifest and no --output-dir")
        finalize_manifest(
            lock_path,
            lock,
            recipe,
            args.manifest.resolve(),
            args.finalize_install.resolve(),
        )
        print(f"finalized Vita dependency source manifest: {args.manifest}")
        return 0
    if args.output_dir is None or args.manifest is None:
        raise DependencySourceError("staging requires --output-dir and --manifest")
    manifest_path = args.manifest.resolve()
    if manifest_path == lock_path or manifest_path == args.output_dir.resolve():
        raise DependencySourceError("manifest must not overwrite the lock or staged sources")
    manifest_path.unlink(missing_ok=True)
    sources = stage_sources(lock, recipe, args.output_dir.resolve(), args.cache_dir.resolve())
    write_json(manifest_path, initial_manifest(lock_path, lock, recipe, sources))
    print(f"staged {len(sources)} verified Vita dependency source inputs")
    print(f"wrote pending Vita dependency manifest: {manifest_path}")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except DependencySourceError as exc:
        print(f"Vita dependency source staging failed: {exc}", file=sys.stderr)
        raise SystemExit(1)
