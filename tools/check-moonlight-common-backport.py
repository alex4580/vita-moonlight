#!/usr/bin/env python3
"""Verify that the pinned RTSP backport is the source compiled into the VPK."""

from __future__ import print_function

import re
import subprocess
import sys
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
SUBMODULE = ROOT / "third_party" / "moonlight-common-c"
EXPECTED_SUBMODULE_COMMIT = "07c32c80f98bb0d7214c577bd080eea3ce64a856"
PREPARE_TOOL = ROOT / "tools" / "prepare-moonlight-common-rtsp.py"
BASE_SOURCE = SUBMODULE / "src" / "RtspConnection.c"
PATCH_FILE = ROOT / "patches" / "moonlight-common-c" / "7b026e7-rtsp-hardening.patch"
THIRD_PARTY_CMAKE = ROOT / "third_party" / "CMakeLists.txt"
GIT_ATTRIBUTES = ROOT / ".gitattributes"


def fail(message):
    print("moonlight-common-c backport contract failed: {}".format(message), file=sys.stderr)
    return 1


def run_checked(command, label):
    try:
        result = subprocess.run(
            command,
            cwd=str(ROOT),
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            universal_newlines=True,
            check=False,
        )
    except OSError as exc:
        raise RuntimeError("could not run {}: {}".format(label, exc))
    if result.returncode != 0:
        detail = (result.stderr or result.stdout).strip()
        raise RuntimeError("{} failed: {}".format(label, detail))
    return result.stdout.strip()


def main():
    try:
        submodule_head = run_checked(
            [
                "git",
                "-c",
                "safe.directory={}".format(SUBMODULE.resolve().as_posix()),
                "-C",
                str(SUBMODULE),
                "rev-parse",
                "HEAD",
            ],
            "submodule revision check",
        )
        if submodule_head != EXPECTED_SUBMODULE_COMMIT:
            return fail(
                "moonlight-common-c is {}, expected {}".format(
                    submodule_head, EXPECTED_SUBMODULE_COMMIT
                )
            )

        verification = run_checked(
            [
                sys.executable,
                str(PREPARE_TOOL),
                "--source",
                str(BASE_SOURCE),
                "--patch",
                str(PATCH_FILE),
                "--check",
            ],
            "hash-locked patch verification",
        )

        cmake = THIRD_PARTY_CMAKE.read_text(encoding="utf-8")
        git_attributes = GIT_ATTRIBUTES.read_text(encoding="utf-8")
    except (OSError, RuntimeError) as exc:
        return fail(str(exc))

    required_fragments = (
        '"${CMAKE_CURRENT_SOURCE_DIR}/moonlight-common-c/src/RtspConnection.c"',
        '"${CMAKE_SOURCE_DIR}/patches/moonlight-common-c/7b026e7-rtsp-hardening.patch"',
        '"${CMAKE_SOURCE_DIR}/tools/prepare-moonlight-common-rtsp.py"',
        '"${CMAKE_CURRENT_BINARY_DIR}/generated/moonlight-common-c/RtspConnection.c"',
        '--output "${MOONLIGHT_RTSP_GENERATED_SOURCE}"',
        "add_custom_target(verify-moonlight-common-rtsp-backport",
        "add_dependencies(moonlight-common-c verify-moonlight-common-rtsp-backport)",
    )
    for fragment in required_fragments:
        if fragment not in cmake:
            return fail("third_party/CMakeLists.txt is missing {!r}".format(fragment))

    if (
        "patches/moonlight-common-c/*.patch text eol=lf -whitespace"
        not in git_attributes.splitlines()
    ):
        return fail(
            ".gitattributes does not preserve the reviewed patch's LF bytes "
            "and suppress unified-diff whitespace false positives"
        )

    library_match = re.search(
        r"add_library\(moonlight-common-c\s+STATIC(?P<sources>.*?)\n\)",
        cmake,
        flags=re.DOTALL,
    )
    if library_match is None:
        return fail("could not find the moonlight-common-c source list")
    sources = library_match.group("sources")
    if '"${MOONLIGHT_RTSP_GENERATED_SOURCE}"' not in sources:
        return fail("moonlight-common-c does not compile the generated RTSP source")
    if re.search(r"moonlight-common-c/src/RtspConnection\.c", sources):
        return fail("moonlight-common-c still compiles the unpatched RTSP source")

    print("moonlight-common-c backport contract passed")
    if verification:
        print(verification)
    return 0


if __name__ == "__main__":
    sys.exit(main())
