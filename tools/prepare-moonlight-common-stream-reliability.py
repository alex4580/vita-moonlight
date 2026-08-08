#!/usr/bin/env python3
"""Create hash-locked moonlight-common-c stream-reliability build copies."""

from __future__ import print_function

import argparse
import hashlib
import os
import re
import sys
import tempfile
from pathlib import Path


UPSTREAM_COMMIT = "07c32c80f98bb0d7214c577bd080eea3ce64a856"
EXPECTED_PATCH_SHA256 = (
    "50c8c88e7a00872bb28d4adbf249e91d1bc50aaf20330da0318464daa35e1e76"
)
EXPECTED_FILES = {
    "src/ControlStream.c": (
        "214c7b6652727a9f1657e37a8296967e628053ec399c55812f5e6f6850278bab",
        "3dbba7ae02026abc69b4dd7f7fa7e7b4eeb61c19ec0599d4dc2e1980298bafd4",
    ),
    "src/Limelight-internal.h": (
        "94f2bafb77d61dc004f65305df1763ac2d32bf1d5060535d0ffe26cad8e347ce",
        "bf0fbd7a036d23dcab2845e6c49fd6938802299e7c93b8664dd6c8b3dd7b64f4",
    ),
    "src/RtpVideoQueue.c": (
        "8d52ea5a6ac6e987967419d33d611a2e35db6660f8be4c6cd0abde4cfde6c33a",
        "50525d94f8cc5292f8c0c14b16fb1a85bc53b67470ab1c1d6610dd0f2dcd4988",
    ),
    "src/VideoDepacketizer.c": (
        "8c84109789896a34934f7a775bfbc0023c21e027a0f919c2c3d3e0b88f7442c4",
        "203562f5a06e287fa1d52831cfd38b6e8cabd09c94e7e714dc39087f9064fc98",
    ),
    "src/VideoStream.c": (
        "7e1e3170ace7a96d9e61743dde9b8a60dc72c1fb2ffda8ba79f9fd505743d498",
        "eb1084721af8765bd9722c8016d6b6b573b7ddd26e715ea5c7540456315fb1da",
    ),
}

_DIFF_HEADER = re.compile(rb"^diff --git a/(.+) b/(.+)\n$")
_INDEX_HEADER = re.compile(rb"^index [0-9a-f]+\.\.[0-9a-f]+ 100644\n$")
_HUNK_HEADER = re.compile(
    rb"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@(?: .*)?\n$"
)


class BackportError(RuntimeError):
    pass


def _sha256(data):
    return hashlib.sha256(data).hexdigest()


def _canonical_source(path, expected_hash):
    try:
        raw = path.read_bytes()
    except OSError as exc:
        raise BackportError("cannot read base source {}: {}".format(path, exc))
    data = raw.replace(b"\r\n", b"\n")
    actual = _sha256(data)
    if actual != expected_hash:
        raise BackportError(
            "base source hash drifted: expected {}, got {} ({})".format(
                expected_hash, actual, path
            )
        )
    return data


def _count(value):
    return int(value) if value is not None else 1


def _apply_file_patch(source, patch_lines, path):
    source_lines = source.splitlines(keepends=True)
    source_cursor = 0
    output = []
    line_number = 0
    saw_hunk = False

    while line_number < len(patch_lines):
        header = patch_lines[line_number]
        match = _HUNK_HEADER.match(header)
        if match is None:
            raise BackportError(
                "{}: unexpected patch content at file-patch line {}".format(
                    path, line_number + 1
                )
            )
        saw_hunk = True
        old_start = int(match.group(1))
        old_count = _count(match.group(2))
        new_count = _count(match.group(4))
        hunk_start = old_start - 1
        if hunk_start < source_cursor or hunk_start > len(source_lines):
            raise BackportError("{}: invalid or overlapping hunk".format(path))
        output.extend(source_lines[source_cursor:hunk_start])
        source_cursor = hunk_start
        consumed = 0
        produced = 0
        line_number += 1

        while line_number < len(patch_lines) and not patch_lines[
            line_number
        ].startswith(b"@@ "):
            patch_line = patch_lines[line_number]
            marker = patch_line[:1]
            payload = patch_line[1:]
            if marker == b" ":
                if (
                    source_cursor >= len(source_lines)
                    or source_lines[source_cursor] != payload
                ):
                    raise BackportError(
                        "{}: context mismatch at source line {}".format(
                            path, source_cursor + 1
                        )
                    )
                output.append(payload)
                source_cursor += 1
                consumed += 1
                produced += 1
            elif marker == b"-":
                if (
                    source_cursor >= len(source_lines)
                    or source_lines[source_cursor] != payload
                ):
                    raise BackportError(
                        "{}: removal mismatch at source line {}".format(
                            path, source_cursor + 1
                        )
                    )
                source_cursor += 1
                consumed += 1
            elif marker == b"+":
                output.append(payload)
                produced += 1
            else:
                raise BackportError(
                    "{}: unsupported patch marker at file-patch line {}".format(
                        path, line_number + 1
                    )
                )
            line_number += 1

        if consumed != old_count or produced != new_count:
            raise BackportError(
                "{}: hunk count mismatch {}/{}; expected {}/{}".format(
                    path, consumed, produced, old_count, new_count
                )
            )

    if not saw_hunk:
        raise BackportError("{}: patch contains no hunks".format(path))
    output.extend(source_lines[source_cursor:])
    return b"".join(output)


def apply_verified_patch(source_dir, patch_path):
    try:
        patch_data = patch_path.read_bytes()
    except OSError as exc:
        raise BackportError("cannot read patch {}: {}".format(patch_path, exc))
    if b"\r\n" in patch_data:
        raise BackportError("patch line endings drifted from reviewed LF form")
    actual_patch_hash = _sha256(patch_data)
    if actual_patch_hash != EXPECTED_PATCH_SHA256:
        raise BackportError(
            "patch hash drifted: expected {}, got {} ({})".format(
                EXPECTED_PATCH_SHA256, actual_patch_hash, patch_path
            )
        )

    lines = patch_data.splitlines(keepends=True)
    cursor = 0
    file_patches = {}
    while cursor < len(lines):
        match = _DIFF_HEADER.match(lines[cursor])
        if match is None or match.group(1) != match.group(2):
            raise BackportError(
                "unexpected diff header at patch line {}".format(cursor + 1)
            )
        try:
            path = match.group(1).decode("ascii")
        except UnicodeDecodeError:
            raise BackportError("patch contains a non-ASCII path")
        if path not in EXPECTED_FILES or path in file_patches:
            raise BackportError("unexpected or duplicate patch target {}".format(path))
        if cursor + 3 >= len(lines):
            raise BackportError("truncated header for {}".format(path))
        if _INDEX_HEADER.match(lines[cursor + 1]) is None:
            raise BackportError("invalid index header for {}".format(path))
        if lines[cursor + 2] != ("--- a/{}\n".format(path)).encode("ascii"):
            raise BackportError("invalid old path for {}".format(path))
        if lines[cursor + 3] != ("+++ b/{}\n".format(path)).encode("ascii"):
            raise BackportError("invalid new path for {}".format(path))
        cursor += 4
        start = cursor
        while cursor < len(lines) and not lines[cursor].startswith(b"diff --git "):
            cursor += 1
        file_patches[path] = lines[start:cursor]

    missing = sorted(set(EXPECTED_FILES) - set(file_patches))
    if missing:
        raise BackportError("patch is missing targets: {}".format(", ".join(missing)))

    outputs = {}
    for path, hashes in EXPECTED_FILES.items():
        source = _canonical_source(source_dir / path, hashes[0])
        output = _apply_file_patch(source, file_patches[path], path)
        actual_output_hash = _sha256(output)
        if actual_output_hash != hashes[1]:
            raise BackportError(
                "{} output drifted: expected {}, got {}".format(
                    path, hashes[1], actual_output_hash
                )
            )
        outputs[path] = output
    return outputs


def _write_if_changed(path, data):
    if path.exists() and path.read_bytes() == data:
        return
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary_name = None
    try:
        with tempfile.NamedTemporaryFile(
            mode="wb", prefix=".stream-reliability.", suffix=".tmp",
            dir=str(path.parent), delete=False
        ) as temporary:
            temporary.write(data)
            temporary.flush()
            os.fsync(temporary.fileno())
            temporary_name = temporary.name
        os.replace(temporary_name, str(path))
    finally:
        if temporary_name is not None and os.path.exists(temporary_name):
            os.unlink(temporary_name)


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source-dir", required=True, type=Path)
    parser.add_argument("--patch", required=True, type=Path)
    parser.add_argument("--output-dir", type=Path)
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args(argv)
    if not args.check and args.output_dir is None:
        parser.error("--output-dir is required unless --check is used")

    try:
        outputs = apply_verified_patch(args.source_dir, args.patch)
        if not args.check:
            for path, data in outputs.items():
                _write_if_changed(args.output_dir / path, data)
    except BackportError as exc:
        print("stream-reliability backport verification failed: {}".format(exc),
              file=sys.stderr)
        return 1

    print(
        "moonlight-common-c stream-reliability backport verified: "
        "commit={} patch={} files={}".format(
            UPSTREAM_COMMIT, EXPECTED_PATCH_SHA256, len(outputs)
        )
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
