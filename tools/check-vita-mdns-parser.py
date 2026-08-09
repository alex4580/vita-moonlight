#!/usr/bin/env python3
"""Compile and run the platform-neutral mDNS parser security tests."""

from __future__ import annotations

import pathlib
import shutil
import subprocess
import sys
import tempfile


ROOT = pathlib.Path(__file__).resolve().parents[1]
PARSER_DIR = ROOT / "third_party" / "mdnsniff"
TEST_SOURCE = ROOT / "tests" / "mdns_parser_test.c"


def fail(message: str) -> None:
    raise SystemExit(f"mDNS parser contract failed: {message}")


def main() -> int:
    compiler = next(
        (path for name in ("cc", "gcc", "clang") if (path := shutil.which(name))),
        None,
    )
    if compiler is None:
        fail("no C compiler found (install cc, gcc, or clang)")

    required = [
        PARSER_DIR / "mdns_parser.c",
        PARSER_DIR / "mdns_parser.h",
        TEST_SOURCE,
    ]
    for path in required:
        if not path.is_file():
            fail(f"missing {path.relative_to(ROOT)}")

    parser_text = required[0].read_text(encoding="utf-8")
    socket_text = (PARSER_DIR / "udp_sniffer_vita.c").read_text(
        encoding="utf-8"
    )
    if "SPDX-License-Identifier: GPL-3.0-only" not in parser_text:
        fail("parser is missing its GPL-3.0-only SPDX declaration")
    if "pointer_hops > packet_size" not in parser_text:
        fail("compression-pointer cycle bound is missing")
    if "*state = next;" not in parser_text:
        fail("transactional state commit is missing")
    if (
        "close_socket_after_network_change" not in socket_text
        or socket_text.count("if (send_query() < 0)") < 2
        or "MDNS_REOPEN_INTERVAL_MS" not in socket_text
        or "MDNS_MAX_PACKETS_PER_POLL" not in socket_text
        or "source.sin_port != sceNetHtons(MDNS_PORT)" not in socket_text
    ):
        fail("Vita socket must recover efficiently after Wi-Fi or resume changes")

    with tempfile.TemporaryDirectory(prefix="vita-mdns-test-") as temp_dir:
        executable = pathlib.Path(temp_dir) / (
            "mdns_parser_test.exe" if sys.platform == "win32" else "mdns_parser_test"
        )
        command = [
            compiler,
            "-std=c99",
            "-Wall",
            "-Wextra",
            "-Werror",
            "-pedantic",
            f"-I{PARSER_DIR}",
            str(PARSER_DIR / "mdns_parser.c"),
            str(TEST_SOURCE),
            "-o",
            str(executable),
        ]
        subprocess.run(command, cwd=ROOT, check=True)
        subprocess.run([str(executable)], cwd=ROOT, check=True)

    print("Vita mDNS parser contract passed")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
