#!/usr/bin/env python3
"""Fetch the locked VitaSDK packages with fail-closed provenance checks.

VitaSDK rebuilds and replaces assets on its long-lived ``master`` release.
The GitHub release response is therefore discovery metadata, not a trust root.
This helper takes every expected asset ID, byte count, update timestamp, and
SHA-256 digest from the committed lock file, requires the live asset metadata
to match it exactly, downloads by immutable-in-context asset ID, and verifies
the downloaded bytes against the committed digest.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import sys
import time
import urllib.error
import urllib.request
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


API_ROOT = "https://api.github.com/repos/vitasdk/packages"
REPOSITORY = "vitasdk/packages"
RELEASE_TAG = "master"
LOCK_SCHEMA = "vita-moonlight/vitasdk-package-lock/v1"
MANIFEST_SCHEMA = "vita-moonlight/vitasdk-dependency-manifest/v2"
DEFAULT_LOCK_PATH = Path(__file__).with_name("vitasdk-packages.lock.json")
PACKAGE_NAMES = (
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
LOCK_FIELDS = {"schema", "repository", "locked_at_utc", "release", "assets"}
RELEASE_FIELDS = {"id", "tag", "tag_commit", "published_at", "updated_at"}
ASSET_FIELDS = {
    "name",
    "asset_id",
    "size",
    "sha256",
    "updated_at",
    "asset_api_url",
    "download_url",
}
SHA256_DIGEST = re.compile(r"^sha256:([0-9a-f]{64})$")
BARE_SHA256 = re.compile(r"^[0-9a-f]{64}$")
COMMIT_SHA = re.compile(r"^[0-9a-f]{40}$")
UTC_TIMESTAMP = re.compile(
    r"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z$"
)
DOWNLOAD_PREFIX = "https://github.com/vitasdk/packages/releases/download/master/"
USER_AGENT = "vita-moonlight VitaSDK dependency resolver"


class ProvenanceError(RuntimeError):
    """Raised when locked, upstream, or downloaded metadata is inconsistent."""


def request(url: str, *, accept: str) -> urllib.response.addinfourl:
    headers = {
        "Accept": accept,
        "User-Agent": USER_AGENT,
        "X-GitHub-Api-Version": "2022-11-28",
    }
    last_error: Exception | None = None
    for attempt in range(3):
        try:
            return urllib.request.urlopen(
                urllib.request.Request(url, headers=headers),
                timeout=60,
            )
        except (OSError, urllib.error.URLError) as exc:
            last_error = exc
            if attempt < 2:
                time.sleep(2**attempt)
    raise ProvenanceError(f"request failed after 3 attempts: {url}: {last_error}")


def request_json(url: str) -> dict[str, Any]:
    with request(url, accept="application/vnd.github+json") as response:
        try:
            value = json.load(response)
        except (UnicodeDecodeError, json.JSONDecodeError) as exc:
            raise ProvenanceError(f"invalid JSON response from {url}: {exc}") from exc
    if not isinstance(value, dict):
        raise ProvenanceError(f"expected a JSON object from {url}")
    return value


def require_exact_fields(
    value: dict[str, Any], expected: set[str], description: str
) -> None:
    actual = set(value)
    if actual != expected:
        missing = sorted(expected - actual)
        unexpected = sorted(actual - expected)
        raise ProvenanceError(
            f"{description} fields changed: missing={missing}, unexpected={unexpected}"
        )


def validate_timestamp(value: Any, description: str) -> str:
    if not isinstance(value, str) or UTC_TIMESTAMP.fullmatch(value) is None:
        raise ProvenanceError(f"{description}: expected a whole-second UTC timestamp")
    return value


def validate_locked_asset(asset: dict[str, Any], expected_name: str) -> dict[str, Any]:
    require_exact_fields(asset, ASSET_FIELDS, f"locked asset {expected_name}")
    name = asset.get("name")
    if name != expected_name:
        raise ProvenanceError(
            f"locked asset name mismatch: expected {expected_name!r}, found {name!r}"
        )
    asset_id = asset.get("asset_id")
    size = asset.get("size")
    sha256 = asset.get("sha256")
    if not isinstance(asset_id, int) or isinstance(asset_id, bool) or asset_id <= 0:
        raise ProvenanceError(f"{name}: lock has an invalid GitHub asset ID")
    if not isinstance(size, int) or isinstance(size, bool) or size <= 0:
        raise ProvenanceError(f"{name}: lock has an invalid byte count")
    if not isinstance(sha256, str) or BARE_SHA256.fullmatch(sha256) is None:
        raise ProvenanceError(f"{name}: lock has an invalid SHA-256 digest")

    asset_api_url = asset.get("asset_api_url")
    expected_api_url = f"{API_ROOT}/releases/assets/{asset_id}"
    if asset_api_url != expected_api_url:
        raise ProvenanceError(
            f"{name}: lock has unexpected asset API URL {asset_api_url!r}"
        )
    download_url = asset.get("download_url")
    if download_url != DOWNLOAD_PREFIX + expected_name:
        raise ProvenanceError(
            f"{name}: lock has unexpected browser download URL {download_url!r}"
        )
    updated_at = validate_timestamp(asset.get("updated_at"), f"{name} update")
    return {
        "name": name,
        "asset_id": asset_id,
        "size": size,
        "sha256": sha256,
        "updated_at": updated_at,
        "asset_api_url": asset_api_url,
        "download_url": download_url,
    }


def validate_lock_document(value: Any) -> dict[str, Any]:
    if not isinstance(value, dict):
        raise ProvenanceError("VitaSDK package lock must be a JSON object")
    require_exact_fields(value, LOCK_FIELDS, "VitaSDK package lock")
    if value.get("schema") != LOCK_SCHEMA:
        raise ProvenanceError(f"unsupported VitaSDK package lock schema {value.get('schema')!r}")
    if value.get("repository") != REPOSITORY:
        raise ProvenanceError(f"unexpected locked repository {value.get('repository')!r}")
    locked_at_utc = validate_timestamp(value.get("locked_at_utc"), "lock time")

    raw_release = value.get("release")
    if not isinstance(raw_release, dict):
        raise ProvenanceError("VitaSDK package lock has no release object")
    require_exact_fields(raw_release, RELEASE_FIELDS, "locked release")
    release_id = raw_release.get("id")
    if not isinstance(release_id, int) or isinstance(release_id, bool) or release_id <= 0:
        raise ProvenanceError("locked release has an invalid GitHub release ID")
    if raw_release.get("tag") != RELEASE_TAG:
        raise ProvenanceError(f"locked release tag is not {RELEASE_TAG!r}")
    tag_commit = raw_release.get("tag_commit")
    if not isinstance(tag_commit, str) or COMMIT_SHA.fullmatch(tag_commit) is None:
        raise ProvenanceError("locked release has an invalid tag commit")
    release = {
        "id": release_id,
        "tag": RELEASE_TAG,
        "tag_commit": tag_commit,
        "published_at": validate_timestamp(
            raw_release.get("published_at"), "release publication"
        ),
        "updated_at": validate_timestamp(
            raw_release.get("updated_at"), "release update"
        ),
    }

    raw_assets = value.get("assets")
    if not isinstance(raw_assets, list):
        raise ProvenanceError("VitaSDK package lock has no asset list")
    if len(raw_assets) != len(PACKAGE_NAMES):
        raise ProvenanceError(
            f"VitaSDK package lock must contain {len(PACKAGE_NAMES)} assets"
        )
    assets: list[dict[str, Any]] = []
    for index, expected_name in enumerate(PACKAGE_NAMES):
        raw_asset = raw_assets[index]
        if not isinstance(raw_asset, dict):
            raise ProvenanceError(f"locked asset {expected_name} is not an object")
        assets.append(validate_locked_asset(raw_asset, expected_name))
    asset_ids = [asset["asset_id"] for asset in assets]
    if len(set(asset_ids)) != len(asset_ids):
        raise ProvenanceError("VitaSDK package lock contains duplicate asset IDs")

    return {
        "schema": LOCK_SCHEMA,
        "repository": REPOSITORY,
        "locked_at_utc": locked_at_utc,
        "release": release,
        "assets": assets,
    }


def load_lock(path: Path) -> tuple[dict[str, Any], str]:
    try:
        raw = path.read_bytes()
    except OSError as exc:
        raise ProvenanceError(f"could not read VitaSDK package lock {path}: {exc}") from exc
    try:
        value = json.loads(raw)
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise ProvenanceError(f"invalid VitaSDK package lock JSON: {exc}") from exc
    return validate_lock_document(value), hashlib.sha256(raw).hexdigest()


def validate_upstream_asset(asset: dict[str, Any], expected_name: str) -> dict[str, Any]:
    name = asset.get("name")
    if name != expected_name:
        raise ProvenanceError(
            f"asset name mismatch: expected {expected_name!r}, found {name!r}"
        )
    asset_id = asset.get("id")
    size = asset.get("size")
    digest = asset.get("digest")
    if not isinstance(asset_id, int) or isinstance(asset_id, bool) or asset_id <= 0:
        raise ProvenanceError(f"{name}: upstream has an invalid GitHub asset ID")
    if not isinstance(size, int) or isinstance(size, bool) or size <= 0:
        raise ProvenanceError(f"{name}: upstream has an invalid byte count")
    if not isinstance(digest, str):
        raise ProvenanceError(f"{name}: GitHub did not provide a SHA-256 digest")
    digest_match = SHA256_DIGEST.fullmatch(digest)
    if digest_match is None:
        raise ProvenanceError(f"{name}: unsupported upstream digest {digest!r}")

    asset_api_url = asset.get("url")
    expected_api_url = f"{API_ROOT}/releases/assets/{asset_id}"
    if asset_api_url != expected_api_url:
        raise ProvenanceError(f"{name}: unexpected asset API URL {asset_api_url!r}")
    download_url = asset.get("browser_download_url")
    if download_url != DOWNLOAD_PREFIX + expected_name:
        raise ProvenanceError(f"{name}: unexpected download URL {download_url!r}")
    return {
        "name": name,
        "asset_id": asset_id,
        "size": size,
        "sha256": digest_match.group(1),
        "updated_at": validate_timestamp(asset.get("updated_at"), f"{name} update"),
        "asset_api_url": asset_api_url,
        "download_url": download_url,
    }


def require_asset_match(
    locked: dict[str, Any], upstream: dict[str, Any]
) -> None:
    mismatches = [
        field for field in sorted(ASSET_FIELDS) if upstream.get(field) != locked.get(field)
    ]
    if mismatches:
        details = ", ".join(
            f"{field}: locked={locked.get(field)!r}, upstream={upstream.get(field)!r}"
            for field in mismatches
        )
        raise ProvenanceError(
            f"{locked['name']}: upstream asset no longer matches the committed lock ({details}). "
            "Review the upstream rebuild and update the lock intentionally."
        )


def resolve_assets(lock: dict[str, Any]) -> tuple[dict[str, Any], list[dict[str, Any]]]:
    locked_release = lock["release"]
    tag_ref = request_json(f"{API_ROOT}/git/ref/tags/{RELEASE_TAG}")
    tag_object = tag_ref.get("object")
    if not isinstance(tag_object, dict):
        raise ProvenanceError("VitaSDK tag response has no object")
    if tag_object.get("type") != "commit":
        raise ProvenanceError("VitaSDK master tag no longer points to a commit")
    if tag_object.get("sha") != locked_release["tag_commit"]:
        raise ProvenanceError(
            "VitaSDK package source tag changed: expected "
            f"{locked_release['tag_commit']}, found {tag_object.get('sha')!r}. "
            "Review the upstream changes before updating the lock."
        )

    release = request_json(f"{API_ROOT}/releases/tags/{RELEASE_TAG}")
    for field, upstream_field in (
        ("id", "id"),
        ("tag", "tag_name"),
        ("published_at", "published_at"),
    ):
        if release.get(upstream_field) != locked_release[field]:
            raise ProvenanceError(
                f"VitaSDK release {field} changed: locked={locked_release[field]!r}, "
                f"upstream={release.get(upstream_field)!r}"
            )

    raw_assets = release.get("assets")
    if not isinstance(raw_assets, list):
        raise ProvenanceError("VitaSDK release has no asset list")
    assets_by_name: dict[str, list[dict[str, Any]]] = {}
    for raw_asset in raw_assets:
        if not isinstance(raw_asset, dict):
            continue
        name = raw_asset.get("name")
        if isinstance(name, str):
            assets_by_name.setdefault(name, []).append(raw_asset)

    resolved: list[dict[str, Any]] = []
    for locked_asset in lock["assets"]:
        name = locked_asset["name"]
        candidates = assets_by_name.get(name, [])
        if len(candidates) != 1:
            raise ProvenanceError(
                f"expected exactly one VitaSDK asset named {name!r}; found {len(candidates)}"
            )
        upstream = validate_upstream_asset(candidates[0], name)
        require_asset_match(locked_asset, upstream)
        resolved.append(locked_asset)
    return locked_release, resolved


def download_asset(asset: dict[str, Any], output_directory: Path) -> None:
    destination = output_directory / asset["name"]
    temporary = destination.with_name(destination.name + ".partial")
    temporary.unlink(missing_ok=True)
    digest = hashlib.sha256()
    byte_count = 0
    try:
        with request(asset["asset_api_url"], accept="application/octet-stream") as source:
            with temporary.open("wb") as target:
                while True:
                    chunk = source.read(1024 * 1024)
                    if not chunk:
                        break
                    target.write(chunk)
                    digest.update(chunk)
                    byte_count += len(chunk)
        actual_sha256 = digest.hexdigest()
        if byte_count != asset["size"]:
            raise ProvenanceError(
                f"{asset['name']}: expected {asset['size']} bytes, downloaded {byte_count}"
            )
        if actual_sha256 != asset["sha256"]:
            raise ProvenanceError(
                f"{asset['name']}: expected SHA-256 {asset['sha256']}, "
                f"downloaded {actual_sha256}"
            )
        temporary.replace(destination)
    finally:
        temporary.unlink(missing_ok=True)


def write_manifest(
    path: Path,
    lock: dict[str, Any],
    lock_sha256: str,
    release: dict[str, Any],
    assets: list[dict[str, Any]],
) -> None:
    manifest = {
        "schema": MANIFEST_SCHEMA,
        "repository": REPOSITORY,
        "source_lock": {
            "schema": LOCK_SCHEMA,
            "sha256": lock_sha256,
            "locked_at_utc": lock["locked_at_utc"],
        },
        "release": release,
        "verified_at_utc": datetime.now(timezone.utc)
        .replace(microsecond=0)
        .isoformat()
        .replace("+00:00", "Z"),
        "assets": assets,
    }
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(
        json.dumps(manifest, indent=2, sort_keys=True) + "\n",
        encoding="utf-8",
        newline="\n",
    )


def self_test(lock_path: Path) -> None:
    lock, lock_sha256 = load_lock(lock_path)
    if [asset["name"] for asset in lock["assets"]] != list(PACKAGE_NAMES):
        raise AssertionError("locked dependency order changed")
    if BARE_SHA256.fullmatch(lock_sha256) is None:
        raise AssertionError("lock file hash is invalid")

    locked = lock["assets"][0]
    sample = {
        "name": locked["name"],
        "id": locked["asset_id"],
        "size": locked["size"],
        "digest": "sha256:" + locked["sha256"],
        "updated_at": locked["updated_at"],
        "url": locked["asset_api_url"],
        "browser_download_url": locked["download_url"],
    }
    validated = validate_upstream_asset(sample, locked["name"])
    require_asset_match(locked, validated)

    for field, value in (
        ("digest", "sha512:" + "a" * 64),
        ("size", 0),
        ("url", "https://example.invalid/asset"),
        ("updated_at", "not-a-timestamp"),
    ):
        poisoned = dict(sample)
        poisoned[field] = value
        try:
            validate_upstream_asset(poisoned, locked["name"])
        except ProvenanceError:
            continue
        raise AssertionError(f"invalid upstream {field} was accepted")

    changed = dict(validated)
    changed["sha256"] = "0" * 64
    try:
        require_asset_match(locked, changed)
    except ProvenanceError:
        pass
    else:
        raise AssertionError("upstream content change was accepted")

    poisoned_lock = json.loads(json.dumps(lock))
    poisoned_lock["assets"][0]["asset_id"] += 1
    try:
        validate_lock_document(poisoned_lock)
    except ProvenanceError:
        pass
    else:
        raise AssertionError("inconsistent locked asset identity was accepted")
    print(
        "VitaSDK package lock self-test passed: "
        f"assets={len(PACKAGE_NAMES)}, lock_sha256={lock_sha256}"
    )


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--lock", type=Path, default=DEFAULT_LOCK_PATH)
    parser.add_argument("--output-dir", type=Path)
    parser.add_argument("--manifest", type=Path)
    parser.add_argument("--self-test", action="store_true")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    if args.self_test:
        self_test(args.lock)
        return 0
    if args.output_dir is None or args.manifest is None:
        raise ProvenanceError("--output-dir and --manifest are required")

    lock, lock_sha256 = load_lock(args.lock)
    args.output_dir.mkdir(parents=True, exist_ok=True)
    release, assets = resolve_assets(lock)
    for asset in assets:
        download_asset(asset, args.output_dir)
        print(
            f"verified locked {asset['name']}: asset_id={asset['asset_id']}, "
            f"{asset['size']} bytes, sha256:{asset['sha256']}"
        )
    write_manifest(args.manifest, lock, lock_sha256, release, assets)
    print(f"wrote VitaSDK dependency manifest: {args.manifest}")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except ProvenanceError as exc:
        print(f"VitaSDK dependency verification failed: {exc}", file=sys.stderr)
        raise SystemExit(1)
