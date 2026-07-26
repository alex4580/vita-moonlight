#!/usr/bin/env python3
"""Fetch the current VitaSDK packages with fail-closed provenance checks.

VitaSDK rebuilds and replaces the assets on its long-lived ``master`` release
several times per day. GitHub asset IDs therefore are not stable. This helper
pins the source tag commit instead, resolves the required assets by exact
name, verifies GitHub's SHA-256 digest and byte count, and records everything
needed to audit the resulting VPK build.
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
PINNED_RELEASE_ID = 43579240
PINNED_TAG_COMMIT = "595079e5ed94b5213bccddecc7796efddde61325"
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
SHA256_DIGEST = re.compile(r"^sha256:([0-9a-f]{64})$")
DOWNLOAD_PREFIX = (
    "https://github.com/vitasdk/packages/releases/download/master/"
)
USER_AGENT = "alex4580/vita-moonlight VitaSDK dependency resolver"


class ProvenanceError(RuntimeError):
    """Raised when upstream metadata is missing, changed, or inconsistent."""


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


def validate_asset(asset: dict[str, Any], expected_name: str) -> dict[str, Any]:
    name = asset.get("name")
    if name != expected_name:
        raise ProvenanceError(
            f"asset name mismatch: expected {expected_name!r}, found {name!r}"
        )

    asset_id = asset.get("id")
    size = asset.get("size")
    digest = asset.get("digest")
    download_url = asset.get("browser_download_url")
    updated_at = asset.get("updated_at")
    if not isinstance(asset_id, int) or asset_id <= 0:
        raise ProvenanceError(f"{name}: invalid GitHub asset ID")
    if not isinstance(size, int) or size <= 0:
        raise ProvenanceError(f"{name}: invalid byte count")
    if not isinstance(digest, str):
        raise ProvenanceError(f"{name}: GitHub did not provide a SHA-256 digest")
    digest_match = SHA256_DIGEST.fullmatch(digest)
    if digest_match is None:
        raise ProvenanceError(f"{name}: unsupported digest {digest!r}")
    if (
        not isinstance(download_url, str)
        or download_url != DOWNLOAD_PREFIX + expected_name
    ):
        raise ProvenanceError(f"{name}: unexpected download URL {download_url!r}")
    if not isinstance(updated_at, str) or not updated_at:
        raise ProvenanceError(f"{name}: missing asset update timestamp")

    return {
        "name": name,
        "asset_id": asset_id,
        "size": size,
        "sha256": digest_match.group(1),
        "updated_at": updated_at,
        "download_url": download_url,
    }


def resolve_assets() -> tuple[dict[str, Any], list[dict[str, Any]]]:
    tag_ref = request_json(f"{API_ROOT}/git/ref/tags/{RELEASE_TAG}")
    tag_object = tag_ref.get("object")
    if not isinstance(tag_object, dict):
        raise ProvenanceError("VitaSDK tag response has no object")
    if tag_object.get("type") != "commit":
        raise ProvenanceError("VitaSDK master tag no longer points to a commit")
    if tag_object.get("sha") != PINNED_TAG_COMMIT:
        raise ProvenanceError(
            "VitaSDK package source tag changed: expected "
            f"{PINNED_TAG_COMMIT}, found {tag_object.get('sha')!r}. "
            "Review the upstream changes before updating the pin."
        )

    release = request_json(f"{API_ROOT}/releases/tags/{RELEASE_TAG}")
    if release.get("id") != PINNED_RELEASE_ID:
        raise ProvenanceError(
            f"VitaSDK release ID changed: found {release.get('id')!r}"
        )
    if release.get("tag_name") != RELEASE_TAG:
        raise ProvenanceError(
            f"VitaSDK release tag changed: found {release.get('tag_name')!r}"
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
    for name in PACKAGE_NAMES:
        candidates = assets_by_name.get(name, [])
        if len(candidates) != 1:
            raise ProvenanceError(
                f"expected exactly one VitaSDK asset named {name!r}; "
                f"found {len(candidates)}"
            )
        resolved.append(validate_asset(candidates[0], name))

    release_record = {
        "id": release["id"],
        "tag": release["tag_name"],
        "tag_commit": PINNED_TAG_COMMIT,
        "published_at": release.get("published_at"),
        "updated_at": release.get("updated_at"),
    }
    return release_record, resolved


def download_asset(asset: dict[str, Any], output_directory: Path) -> None:
    destination = output_directory / asset["name"]
    temporary = destination.with_name(destination.name + ".partial")
    temporary.unlink(missing_ok=True)
    digest = hashlib.sha256()
    byte_count = 0
    try:
        with request(asset["download_url"], accept="application/octet-stream") as source:
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
                f"{asset['name']}: expected {asset['size']} bytes, "
                f"downloaded {byte_count}"
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
    release: dict[str, Any],
    assets: list[dict[str, Any]],
) -> None:
    manifest = {
        "schema": 1,
        "repository": REPOSITORY,
        "release": release,
        "resolved_at_utc": datetime.now(timezone.utc)
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


def self_test() -> None:
    sample = {
        "name": "zlib.tar.xz",
        "id": 42,
        "size": 10,
        "digest": "sha256:" + "a" * 64,
        "updated_at": "2026-07-26T00:00:00Z",
        "browser_download_url": DOWNLOAD_PREFIX + "zlib.tar.xz",
    }
    validated = validate_asset(sample, "zlib.tar.xz")
    if validated["sha256"] != "a" * 64:
        raise AssertionError("valid digest was not preserved")
    for field, value in (
        ("digest", "sha512:" + "a" * 64),
        ("size", 0),
        ("browser_download_url", "https://example.invalid/zlib.tar.xz"),
    ):
        poisoned = dict(sample)
        poisoned[field] = value
        try:
            validate_asset(poisoned, "zlib.tar.xz")
        except ProvenanceError:
            continue
        raise AssertionError(f"invalid {field} was accepted")
    print("VitaSDK package resolver self-test passed.")


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output-dir", type=Path)
    parser.add_argument("--manifest", type=Path)
    parser.add_argument("--self-test", action="store_true")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    if args.self_test:
        self_test()
        return 0
    if args.output_dir is None or args.manifest is None:
        raise ProvenanceError("--output-dir and --manifest are required")

    args.output_dir.mkdir(parents=True, exist_ok=True)
    release, assets = resolve_assets()
    for asset in assets:
        download_asset(asset, args.output_dir)
        print(
            f"verified {asset['name']}: "
            f"{asset['size']} bytes, sha256:{asset['sha256']}"
        )
    write_manifest(args.manifest, release, assets)
    print(f"wrote VitaSDK dependency manifest: {args.manifest}")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except ProvenanceError as exc:
        print(f"VitaSDK dependency verification failed: {exc}", file=sys.stderr)
        raise SystemExit(1)
