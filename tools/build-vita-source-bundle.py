#!/usr/bin/env python3
"""Build the auditable Vita source archive that accompanies a VPK release.

The archive contains the tagged project tree, complete pinned submodule trees,
every hash-verified upstream source archive used by the Vita dependency set,
the exact SDK runtime sources recorded by the pinned SDK, and all distributable
build recipes and patches.  ``--require-complete`` fails closed if a future lock
records any unresolved distribution blocker.
"""

from __future__ import annotations

import argparse
import gzip
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
SCHEMA = "vita-moonlight/vita-corresponding-source-lock/v1"
HEX40 = re.compile(r"[0-9a-f]{40}")
HEX64 = re.compile(r"[0-9a-f]{64}")
SAFE_NAME = re.compile(r"[A-Za-z0-9][A-Za-z0-9._+-]*")


class SourceBundleError(RuntimeError):
    """Raised when source provenance or archive construction is unsafe."""


def read_json(path: Path) -> dict:
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        raise SourceBundleError(f"could not read JSON {path}: {exc}") from exc
    if not isinstance(value, dict):
        raise SourceBundleError(f"{path} must contain a JSON object")
    return value


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as source:
        while True:
            chunk = source.read(1024 * 1024)
            if not chunk:
                break
            digest.update(chunk)
    return digest.hexdigest()


def safe_relative(value: str, description: str) -> PurePosixPath:
    path = PurePosixPath(value)
    if (
        not value
        or path.is_absolute()
        or "\\" in value
        or any(part in ("", ".", "..") for part in path.parts)
    ):
        raise SourceBundleError(f"unsafe {description}: {value!r}")
    return path


def validate_lock(lock: dict) -> list[dict]:
    if lock.get("schema") != SCHEMA:
        raise SourceBundleError(f"unsupported source lock schema {lock.get('schema')!r}")

    archives = lock.get("archive_inputs")
    git_inputs = lock.get("git_inputs")
    submodules = lock.get("submodules")
    in_tree = lock.get("in_tree_components")
    blockers = lock.get("known_distribution_blockers")
    coverage = lock.get("compiled_component_coverage")
    for name, value in (
        ("archive_inputs", archives),
        ("git_inputs", git_inputs),
        ("submodules", submodules),
        ("in_tree_components", in_tree),
        ("known_distribution_blockers", blockers),
        ("compiled_component_coverage", coverage),
    ):
        if not isinstance(value, list):
            raise SourceBundleError(f"{name} must be an array")

    expected_archives = {
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
    seen_names: set[str] = set()
    for item in archives:
        if not isinstance(item, dict):
            raise SourceBundleError("every archive input must be an object")
        name = item.get("name")
        if not isinstance(name, str) or SAFE_NAME.fullmatch(name) is None:
            raise SourceBundleError(f"invalid archive name {name!r}")
        if name in seen_names:
            raise SourceBundleError(f"duplicate archive input {name}")
        seen_names.add(name)
        if not isinstance(item.get("url"), str) or not item["url"].startswith("https://"):
            raise SourceBundleError(f"{name}: source URL must use HTTPS")
        if HEX64.fullmatch(str(item.get("sha256", ""))) is None:
            raise SourceBundleError(f"{name}: invalid SHA-256")
        if type(item.get("size")) is not int or item["size"] <= 0:
            raise SourceBundleError(f"{name}: invalid byte count")
        if not item.get("component") or not item.get("license_expression"):
            raise SourceBundleError(f"{name}: component and license are required")
    if seen_names != expected_archives:
        raise SourceBundleError("archive inputs do not match the linked source set")

    destinations: set[str] = set()
    for item in git_inputs:
        if not isinstance(item, dict):
            raise SourceBundleError("every git input must be an object")
        commit = str(item.get("commit", ""))
        destination = str(item.get("destination", ""))
        repository = str(item.get("repository", ""))
        safe_relative(destination, "git destination")
        if destination in destinations:
            raise SourceBundleError(f"duplicate git destination {destination}")
        destinations.add(destination)
        if HEX40.fullmatch(commit) is None:
            raise SourceBundleError(f"{destination}: invalid commit")
        if not repository.startswith("https://github.com/") or not repository.endswith(".git"):
            raise SourceBundleError(f"{destination}: unsupported git repository")
        if not item.get("component") or not item.get("license_expression"):
            raise SourceBundleError(f"{destination}: component and license are required")

    expected_submodules = {
        "third_party/moonlight-common-c": "07c32c80f98bb0d7214c577bd080eea3ce64a856",
        "third_party/enet": "dea6fb5414b180908b58c0293c831105b5d124dd",
        "third_party/inih": "8e06f6b77b5d4471bdc6d85ada81b67d37354a5c",
    }
    actual_submodules: dict[str, str] = {}
    for item in submodules:
        if not isinstance(item, dict):
            raise SourceBundleError("every submodule must be an object")
        path = str(item.get("path", ""))
        commit = str(item.get("commit", ""))
        safe_relative(path, "submodule path")
        if HEX40.fullmatch(commit) is None:
            raise SourceBundleError(f"{path}: invalid submodule commit")
        actual_submodules[path] = commit
    if actual_submodules != expected_submodules:
        raise SourceBundleError("submodule source pins do not match the VPK source set")

    for item in in_tree:
        if not isinstance(item, dict):
            raise SourceBundleError("every in-tree component must be an object")
        relative = safe_relative(str(item.get("path", "")), "in-tree component path")
        if not (ROOT / Path(*relative.parts)).exists():
            raise SourceBundleError(f"missing in-tree source {relative}")
        if not item.get("component") or not item.get("license_expression"):
            raise SourceBundleError(f"{relative}: component and license are required")

    dependency_build = lock.get("dependency_source_build")
    if not isinstance(dependency_build, dict):
        raise SourceBundleError("project-owned dependency_source_build is required")
    if dependency_build.get("license_expression") != "GPL-3.0-or-later":
        raise SourceBundleError("dependency build recipe must remain GPL-3.0-or-later")
    expected_build_order = [
        "zlib", "bzip2", "zstd", "libpng", "libjpeg-turbo", "FreeType",
        "libvita2d", "Expat", "Opus", "Mbed TLS", "curl",
    ]
    if dependency_build.get("build_order") != expected_build_order:
        raise SourceBundleError("dependency source build order changed")
    for path_key, hash_key, expected_path in (
        ("recipe", "recipe_sha256", "tools/build-vita-dependencies.sh"),
        ("stager", "stager_sha256", "tools/stage-vita-dependency-sources.py"),
    ):
        if dependency_build.get(path_key) != expected_path:
            raise SourceBundleError(f"unexpected dependency {path_key} path")
        path = ROOT / expected_path
        if (
            path.is_symlink()
            or not path.is_file()
            or sha256_file(path) != dependency_build.get(hash_key)
        ):
            raise SourceBundleError(f"{expected_path} differs from the source lock")
    patches = dependency_build.get("patches")
    if not isinstance(patches, list) or {
        item.get("component") for item in patches if isinstance(item, dict)
    } != {"zstd", "libpng", "libvita2d", "Opus"}:
        raise SourceBundleError("dependency source patch set changed")
    for item in patches:
        if not isinstance(item, dict):
            raise SourceBundleError("every dependency patch must be an object")
        relative = safe_relative(str(item.get("path", "")), "dependency patch path")
        path = ROOT.joinpath(*relative.parts)
        if (
            type(item.get("size")) is not int
            or not path.is_file()
            or path.stat().st_size != item["size"]
            or sha256_file(path) != item.get("sha256")
        ):
            raise SourceBundleError(f"{relative}: bytes differ from source lock")
    if not isinstance(dependency_build.get("source_directories"), list) or not isinstance(
        dependency_build.get("required_outputs"), list
    ):
        raise SourceBundleError("dependency source mappings and outputs are required")
    for blocker in blockers:
        if not isinstance(blocker, dict) or not all(
            blocker.get(field) for field in ("component", "detail", "resolution")
        ):
            raise SourceBundleError("every blocker requires component, detail, and resolution")

    sdk = lock.get("vitasdk_archive")
    expected_sdk = {
        "sha256": "b1a8f4d6e41460c9ecf2c3c4590eb7eb3ea6b690767f508bd5696dbc77a4272e",
        "newlib_commit": "fbb8375870d799758e0b4358d2f1708781aa26a6",
        "pthread_commit": "63e1cd9152082d9ccb1b38d67a4caf975562fbeb",
        "vita_headers_commit": "e37621bf80f5140b567c96782f8dc369e1108e0f",
        "vita_toolchain_commit": "71f3789342f610a4eb3ba50fc33292c4900c6666",
        "buildscripts_commit": "366f4140fb89193c55b2e115652da7dc28e3f7b4",
    }
    if not isinstance(sdk, dict) or any(sdk.get(k) != v for k, v in expected_sdk.items()):
        raise SourceBundleError("VitaSDK archive/runtime source identity changed")

    mbed_lock_path = ROOT / str(lock.get("mbedtls_source_lock", ""))
    mbed_lock = read_json(mbed_lock_path)
    if mbed_lock.get("schema") != "vita-moonlight/vita-mbedtls-source-lock/v1":
        raise SourceBundleError("unexpected mbedTLS source-lock schema")
    if sha256_file(mbed_lock_path) != dependency_build.get("mbedtls_source_lock_sha256"):
        raise SourceBundleError("mbedTLS source lock differs from dependency build lock")
    return blockers


def run(command: list[str], *, cwd: Path | None = None, stdout=None) -> str:
    try:
        completed = subprocess.run(
            command,
            cwd=cwd,
            check=True,
            stdout=stdout if stdout is not None else subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=stdout is None,
        )
    except (OSError, subprocess.CalledProcessError) as exc:
        detail = ""
        if isinstance(exc, subprocess.CalledProcessError) and exc.stderr:
            detail = f": {exc.stderr.strip()}"
        raise SourceBundleError(f"command failed: {' '.join(command)}{detail}") from exc
    return "" if stdout is not None else completed.stdout.strip()


def safe_extract_tar(archive: Path, destination: Path) -> None:
    destination.mkdir(parents=True, exist_ok=True)
    with tarfile.open(archive, "r:") as source:
        for member in source.getmembers():
            relative = safe_relative(member.name.rstrip("/"), "git archive member")
            target = destination.joinpath(*relative.parts)
            if member.isdir():
                target.mkdir(parents=True, exist_ok=True)
                continue
            target.parent.mkdir(parents=True, exist_ok=True)
            if member.issym():
                link = PurePosixPath(member.linkname)
                if link.is_absolute() or ".." in link.parts:
                    raise SourceBundleError(f"unsafe symlink in git archive: {member.name}")
                os.symlink(member.linkname, target)
                continue
            if not member.isfile():
                raise SourceBundleError(f"unsupported git archive entry: {member.name}")
            stream = source.extractfile(member)
            if stream is None:
                raise SourceBundleError(f"could not read git archive entry: {member.name}")
            with target.open("wb") as output:
                shutil.copyfileobj(stream, output)
            target.chmod(member.mode & 0o777)


def export_git_tree(repository: Path, commit: str, destination: Path, scratch: Path) -> None:
    archive = scratch / (hashlib.sha256(str(destination).encode()).hexdigest() + ".tar")
    with archive.open("wb") as output:
        run(["git", "archive", "--format=tar", commit], cwd=repository, stdout=output)
    safe_extract_tar(archive, destination)


def stage_remote_git(item: dict, stage: Path, scratch: Path) -> dict:
    clone = scratch / ("git-" + hashlib.sha256(item["repository"].encode()).hexdigest())
    run(["git", "init", "--quiet", str(clone)])
    run(["git", "-C", str(clone), "remote", "add", "origin", item["repository"]])
    run([
        "git", "-C", str(clone), "fetch", "--quiet", "--depth=1", "origin", item["commit"]
    ])
    actual = run(["git", "-C", str(clone), "rev-parse", "FETCH_HEAD"])
    if actual != item["commit"]:
        raise SourceBundleError(f"{item['component']}: fetched {actual}, expected {item['commit']}")
    destination = stage.joinpath(*PurePosixPath(item["destination"]).parts)
    export_git_tree(clone, item["commit"], destination, scratch)
    return {
        "component": item["component"],
        "repository": item["repository"],
        "commit": actual,
        "destination": item["destination"],
        "license_expression": item["license_expression"],
    }


def verify_archive(path: Path, item: dict) -> None:
    try:
        size = path.stat().st_size
    except OSError as exc:
        raise SourceBundleError(f"could not stat {path}: {exc}") from exc
    if size != item["size"]:
        raise SourceBundleError(f"{item['name']}: expected {item['size']} bytes, got {size}")
    actual = sha256_file(path)
    if actual != item["sha256"]:
        raise SourceBundleError(f"{item['name']}: SHA-256 mismatch ({actual})")


def obtain_archive(item: dict, cache: Path) -> Path:
    cache.mkdir(parents=True, exist_ok=True)
    target = cache / item["name"]
    if target.exists():
        verify_archive(target, item)
        return target

    temporary = target.with_suffix(target.suffix + ".part")
    request = urllib.request.Request(
        item["url"], headers={"User-Agent": "vita-moonlight-source-bundle/1"}
    )
    try:
        with urllib.request.urlopen(request, timeout=60) as response, temporary.open("wb") as output:
            declared = response.headers.get("Content-Length")
            if declared is not None and int(declared) != item["size"]:
                raise SourceBundleError(
                    f"{item['name']}: server length {declared} differs from source lock"
                )
            copied = 0
            while True:
                chunk = response.read(1024 * 1024)
                if not chunk:
                    break
                copied += len(chunk)
                if copied > item["size"]:
                    raise SourceBundleError(f"{item['name']}: download exceeded locked size")
                output.write(chunk)
    except (OSError, urllib.error.URLError, ValueError) as exc:
        temporary.unlink(missing_ok=True)
        if isinstance(exc, SourceBundleError):
            raise
        raise SourceBundleError(f"could not download {item['name']}: {exc}") from exc
    verify_archive(temporary, item)
    temporary.replace(target)
    return target


def stage_mbedtls_inputs(lock: dict, destination: Path) -> list[dict]:
    source_lock_path = ROOT / lock["mbedtls_source_lock"]
    source_lock = read_json(source_lock_path)
    destination.mkdir(parents=True, exist_ok=True)
    staged: list[dict] = []
    for item in source_lock.get("inputs", []):
        path = ROOT / "vendor" / "vita-source" / item["name"]
        verify_archive(path, item)
        shutil.copy2(path, destination / item["name"])
        staged.append({
            "name": item["name"],
            "sha256": item["sha256"],
            "size": item["size"],
            "role": item["role"],
            "license_expression": item["license_expression"],
        })
    shutil.copy2(source_lock_path, destination / source_lock_path.name)
    return staged


def repository_version() -> str:
    cmake = (ROOT / "CMakeLists.txt").read_text(encoding="utf-8")
    values: list[str] = []
    for component in ("MAJOR", "MINOR", "PATCH"):
        match = re.search(
            rf'^\s*set\(VERSION_{component}\s+"([0-9]+)"\s*\)', cmake, re.MULTILINE
        )
        if match is None:
            raise SourceBundleError(f"could not read VERSION_{component}")
        values.append(str(int(match.group(1))))
    return ".".join(values)


def write_deterministic_tar(stage: Path, output: Path, prefix: str, epoch: int) -> None:
    temporary_tar = output.with_suffix("")
    with tarfile.open(temporary_tar, "w", format=tarfile.PAX_FORMAT, dereference=False) as archive:
        paths = [stage] + sorted(stage.rglob("*"), key=lambda path: path.relative_to(stage).as_posix())
        for path in paths:
            relative = path.relative_to(stage).as_posix()
            arcname = prefix if not relative else f"{prefix}/{relative}"
            info = archive.gettarinfo(str(path), arcname)
            info.uid = 0
            info.gid = 0
            info.uname = ""
            info.gname = ""
            info.mtime = epoch
            if info.isfile():
                with path.open("rb") as source:
                    archive.addfile(info, source)
            else:
                archive.addfile(info)
    with temporary_tar.open("rb") as source, output.open("wb") as raw:
        with gzip.GzipFile(filename="", mode="wb", fileobj=raw, mtime=epoch) as compressed:
            shutil.copyfileobj(source, compressed, length=1024 * 1024)
    temporary_tar.unlink()


def build_bundle(lock: dict, lock_path: Path, output_dir: Path, cache: Path) -> Path:
    blockers = validate_lock(lock)
    root_commit = run(["git", "rev-parse", "HEAD"], cwd=ROOT)
    expected_ci_commit = os.environ.get("GITHUB_SHA")
    if expected_ci_commit and root_commit != expected_ci_commit:
        raise SourceBundleError(
            f"checkout commit {root_commit} differs from GITHUB_SHA {expected_ci_commit}"
        )
    if subprocess.run(["git", "diff", "--quiet", "HEAD", "--"], cwd=ROOT).returncode != 0:
        raise SourceBundleError("tracked repository files differ from the release commit")
    epoch = int(run(["git", "show", "-s", "--format=%ct", root_commit], cwd=ROOT))
    version = repository_version()

    with tempfile.TemporaryDirectory(prefix="vita-moonlight-source-") as temporary:
        scratch = Path(temporary) / "scratch"
        stage = Path(temporary) / "stage"
        scratch.mkdir()
        stage.mkdir()

        repository_destination = stage / "repository"
        export_git_tree(ROOT, root_commit, repository_destination, scratch)

        submodule_manifest: list[dict] = []
        for item in lock["submodules"]:
            tree_entry = run(["git", "ls-tree", "HEAD", "--", item["path"]], cwd=ROOT)
            match = re.fullmatch(r"160000 commit ([0-9a-f]{40})\t.+", tree_entry)
            if match is None or match.group(1) != item["commit"]:
                raise SourceBundleError(f"{item['path']}: gitlink differs from the source lock")
            checkout = ROOT.joinpath(*PurePosixPath(item["path"]).parts)
            actual = run(["git", "rev-parse", "HEAD"], cwd=checkout)
            if actual != item["commit"]:
                raise SourceBundleError(
                    f"{item['path']}: checkout {actual} differs from gitlink {item['commit']}"
                )
            destination = repository_destination.joinpath(*PurePosixPath(item["path"]).parts)
            export_git_tree(checkout, actual, destination, scratch)
            submodule_manifest.append({**item, "commit": actual})

        archive_destination = stage / "upstream-archives"
        archive_destination.mkdir()
        archive_manifest: list[dict] = []
        for item in lock["archive_inputs"]:
            source = obtain_archive(item, cache)
            shutil.copy2(source, archive_destination / item["name"])
            archive_manifest.append({
                key: item[key]
                for key in ("component", "version", "name", "url", "size", "sha256", "license_expression")
            })

        git_manifest = [stage_remote_git(item, stage, scratch) for item in lock["git_inputs"]]
        mbed_manifest = stage_mbedtls_inputs(lock, archive_destination / "mbedtls-3.6.7-vita")

        source_lock_hash = sha256_file(lock_path)
        manifest = {
            "schema": "vita-moonlight/vita-source-bundle-manifest/v1",
            "completion_status": "blocked-not-complete-corresponding-source" if blockers else "complete",
            "project_version": version,
            "repository_commit": root_commit,
            "source_date_epoch": epoch,
            "source_lock": {
                "path": str(lock_path.relative_to(ROOT)).replace("\\", "/"),
                "sha256": source_lock_hash,
            },
            "submodules": submodule_manifest,
            "archive_inputs": archive_manifest,
            "git_inputs": git_manifest,
            "mbedtls_inputs": mbed_manifest,
            "compiled_component_coverage": lock["compiled_component_coverage"],
            "known_distribution_blockers": blockers,
            "excluded_nonessential_inputs": lock.get("excluded_nonessential_inputs", []),
            "vitasdk_archive_provenance": lock["vitasdk_archive"],
            "dependency_source_build": lock["dependency_source_build"],
        }
        (stage / "SOURCE_MANIFEST.json").write_text(
            json.dumps(manifest, indent=2, sort_keys=True) + "\n", encoding="utf-8"
        )
        status = [
            "Vita Moonlight Vita source-bundle status",
            "========================================",
            "",
            f"Project version: {version}",
            f"Repository commit: {root_commit}",
            f"Completion status: {manifest['completion_status']}",
            "",
        ]
        if blockers:
            status.extend([
                "This archive does not claim to be complete GPL Corresponding Source while a",
                "recorded blocker remains. See SOURCE_MANIFEST.json for its exact remediation.",
                "",
            ])
        else:
            status.extend([
                "This archive contains the complete source set and project-owned build recipes",
                "used for the Vita VPK, including pinned dependency and SDK runtime sources.",
                "See SOURCE_MANIFEST.json for exact versions, hashes, and build order.",
                "",
            ])
        (stage / "SOURCE_BUNDLE_STATUS.txt").write_text("\n".join(status), encoding="utf-8")

        output_dir.mkdir(parents=True, exist_ok=True)
        output = output_dir / f"Vita-Moonlight-{version}-Vita-Source.tar.gz"
        write_deterministic_tar(stage, output, f"vita-moonlight-{version}-vita-source", epoch)
        return output


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--lock", type=Path, default=DEFAULT_LOCK)
    parser.add_argument("--output-dir", type=Path)
    parser.add_argument("--cache-dir", type=Path, default=ROOT / ".tools" / "vita-source-cache")
    parser.add_argument("--self-test", action="store_true")
    parser.add_argument("--require-complete", action="store_true")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    try:
        lock_path = args.lock.resolve()
        try:
            lock_path.relative_to(ROOT)
        except ValueError as exc:
            raise SourceBundleError("the source lock must be inside the repository") from exc
        lock = read_json(lock_path)
        blockers = validate_lock(lock)
        if args.self_test:
            print(
                "Vita source lock self-test passed: "
                f"archives={len(lock['archive_inputs'])}, git_inputs={len(lock['git_inputs'])}, "
                f"submodules={len(lock['submodules'])}, blockers={len(blockers)}"
            )
            if args.require_complete and blockers:
                raise SourceBundleError(
                    "release cannot claim complete Corresponding Source: "
                    + "; ".join(item["component"] for item in blockers)
                )
            return 0
        if args.output_dir is None:
            raise SourceBundleError("--output-dir is required unless --self-test is used")
        if args.require_complete and blockers:
            raise SourceBundleError(
                "release cannot claim complete Corresponding Source: "
                + "; ".join(item["component"] for item in blockers)
            )
        output = build_bundle(
            lock, lock_path, args.output_dir.resolve(), args.cache_dir.resolve()
        )
        print(f"wrote Vita source bundle: {output}")
        print(f"SHA-256: {sha256_file(output)}")
        return 0
    except SourceBundleError as exc:
        print(f"Vita source bundle failed: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
