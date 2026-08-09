#!/usr/bin/env python3
"""Create the hash-locked moonlight-common-c RTSP backport build copy."""

from __future__ import print_function

import argparse
import hashlib
import os
import re
import sys
import tempfile
from pathlib import Path


UPSTREAM_COMMIT = "7b026e77be62175104640e7e722b758df6d3d0d7"
EXPECTED_BASE_LF_SHA256 = "d62758e0715f70763ef3c30295264313a6f6c02837822e6953355e78135e581b"
EXPECTED_BASE_CRLF_SHA256 = "927c90e6958df3b50da173e4c3854963d097302285ea59d228209b1af33620e6"
EXPECTED_PATCH_SHA256 = "fc4b0296fa11f7b0950933cc55ca0637ee18a76737ce50c9bdda71feb4800cdf"
EXPECTED_OUTPUT_SHA256 = "3d95e61d3b30cd9b411b192f9a34a4ae4a84b389a47f391c3d6f9afde2aa268a"

_PATCH_PREAMBLE = (
    b"diff --git a/src/RtspConnection.c b/src/RtspConnection.c\n"
    b"index 52ce2b10d6b9eae55ffe5709d22aae8542cfd5dd.."
    b"3669bdc10f89fc527399e9b9449a44d8989ae3fc 100644\n"
    b"--- a/src/RtspConnection.c\n"
    b"+++ b/src/RtspConnection.c\n"
)
_HUNK_HEADER = re.compile(
    rb"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@(?: .*)?\n$"
)


class BackportError(RuntimeError):
    pass


def _sha256(data):
    return hashlib.sha256(data).hexdigest()


def _read_verified(path, expected_hash, label):
    try:
        data = path.read_bytes()
    except OSError as exc:
        raise BackportError("cannot read {} {}: {}".format(label, path, exc))

    actual_hash = _sha256(data)
    if actual_hash != expected_hash:
        raise BackportError(
            "{} hash drifted: expected {}, got {} ({})".format(
                label, expected_hash, actual_hash, path
            )
        )
    return data


def _read_verified_source(path):
    try:
        data = path.read_bytes()
    except OSError as exc:
        raise BackportError("cannot read base source {}: {}".format(path, exc))

    raw_hash = _sha256(data)
    if raw_hash == EXPECTED_BASE_LF_SHA256:
        canonical_data = data
    elif raw_hash == EXPECTED_BASE_CRLF_SHA256:
        canonical_data = data.replace(b"\r\n", b"\n")
    else:
        raise BackportError(
            "base source hash drifted: expected LF {} or CRLF {}, got {} ({})".format(
                EXPECTED_BASE_LF_SHA256,
                EXPECTED_BASE_CRLF_SHA256,
                raw_hash,
                path,
            )
        )

    if _sha256(canonical_data) != EXPECTED_BASE_LF_SHA256:
        raise BackportError("base source newline normalization was not deterministic")
    return canonical_data


def _count(value):
    return int(value) if value is not None else 1


def apply_verified_patch(source_data, patch_data):
    """Apply the single-file unified diff strictly and return patched bytes."""
    if not patch_data.startswith(_PATCH_PREAMBLE):
        raise BackportError("patch does not target only src/RtspConnection.c")
    if b"\r\n" in patch_data:
        raise BackportError("patch line endings drifted from the reviewed LF form")

    source_lines = source_data.splitlines(keepends=True)
    patch_lines = patch_data.splitlines(keepends=True)
    line_number = len(_PATCH_PREAMBLE.splitlines())
    source_cursor = 0
    output = []
    saw_hunk = False

    while line_number < len(patch_lines):
        header = patch_lines[line_number]
        match = _HUNK_HEADER.match(header)
        if match is None:
            raise BackportError(
                "unexpected patch content at line {}".format(line_number + 1)
            )

        saw_hunk = True
        old_start = int(match.group(1))
        old_count = _count(match.group(2))
        new_count = _count(match.group(4))
        hunk_source_start = old_start - 1
        if hunk_source_start < source_cursor or hunk_source_start > len(source_lines):
            raise BackportError("patch hunk source range is invalid or overlaps")

        output.extend(source_lines[source_cursor:hunk_source_start])
        source_cursor = hunk_source_start
        consumed = 0
        produced = 0
        line_number += 1

        while line_number < len(patch_lines) and not patch_lines[line_number].startswith(b"@@ "):
            patch_line = patch_lines[line_number]
            if not patch_line:
                raise BackportError("empty raw line in unified diff")

            marker = patch_line[:1]
            payload = patch_line[1:]
            if marker == b" ":
                if source_cursor >= len(source_lines) or source_lines[source_cursor] != payload:
                    raise BackportError(
                        "patch context at patch line {} does not match source line {}".format(
                            line_number + 1, source_cursor + 1
                        )
                    )
                output.append(payload)
                source_cursor += 1
                consumed += 1
                produced += 1
            elif marker == b"-":
                if source_cursor >= len(source_lines) or source_lines[source_cursor] != payload:
                    raise BackportError(
                        "patch removal at patch line {} does not match source line {}".format(
                            line_number + 1, source_cursor + 1
                        )
                    )
                source_cursor += 1
                consumed += 1
            elif marker == b"+":
                output.append(payload)
                produced += 1
            else:
                raise BackportError(
                    "unsupported unified-diff marker at line {}".format(line_number + 1)
                )
            line_number += 1

        if consumed != old_count or produced != new_count:
            raise BackportError(
                "patch hunk count mismatch: consumed/produced {}/{}; expected {}/{}".format(
                    consumed, produced, old_count, new_count
                )
            )

    if not saw_hunk:
        raise BackportError("patch contains no hunks")

    output.extend(source_lines[source_cursor:])
    output_data = b"".join(output)
    output_hash = _sha256(output_data)
    if output_hash != EXPECTED_OUTPUT_SHA256:
        raise BackportError(
            "patched output drifted: expected {}, got {}".format(
                EXPECTED_OUTPUT_SHA256, output_hash
            )
        )
    return output_data


def prepare(source_path, patch_path):
    source_data = _read_verified_source(source_path)
    patch_data = _read_verified(patch_path, EXPECTED_PATCH_SHA256, "backport patch")
    return apply_verified_patch(source_data, patch_data)


def _write_if_changed(output_path, data):
    if output_path.exists() and output_path.read_bytes() == data:
        return False

    output_path.parent.mkdir(parents=True, exist_ok=True)
    temporary_name = None
    try:
        with tempfile.NamedTemporaryFile(
            mode="wb",
            prefix=".RtspConnection.",
            suffix=".tmp",
            dir=str(output_path.parent),
            delete=False,
        ) as temporary:
            temporary.write(data)
            temporary.flush()
            os.fsync(temporary.fileno())
            temporary_name = temporary.name
        os.replace(temporary_name, str(output_path))
    finally:
        if temporary_name is not None and os.path.exists(temporary_name):
            os.unlink(temporary_name)
    return True


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", required=True, type=Path)
    parser.add_argument("--patch", required=True, type=Path)
    parser.add_argument("--output", type=Path)
    parser.add_argument(
        "--check",
        action="store_true",
        help="verify hashes and patch application without writing a build copy",
    )
    args = parser.parse_args(argv)

    if not args.check and args.output is None:
        parser.error("--output is required unless --check is used")

    try:
        output_data = prepare(args.source, args.patch)
        if not args.check:
            _write_if_changed(args.output, output_data)
    except BackportError as exc:
        print("moonlight-common-c RTSP backport verification failed: {}".format(exc), file=sys.stderr)
        return 1

    print(
        "moonlight-common-c RTSP backport verified: commit={} base={} patch={} output={}".format(
            UPSTREAM_COMMIT,
            EXPECTED_BASE_LF_SHA256,
            EXPECTED_PATCH_SHA256,
            EXPECTED_OUTPUT_SHA256,
        )
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
