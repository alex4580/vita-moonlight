#!/usr/bin/env python3
"""Guard the security invariants of the untrusted Sunshine XML parser."""

from pathlib import Path
import re
import sys
import xml.etree.ElementTree as ET


ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "libgamestream" / "xml.c"

# Current Sunshine (src/nvhttp.cpp, HTTPS on_verify_failed) serializes an
# unrecognized client certificate in this attribute-based shape. Keep an exact
# protocol fixture so a generic XML error cannot silently reintroduce the saved
# host/pairing dead end.
SUNSHINE_UNAUTHORIZED_SERVERINFO = (
    '<?xml version="1.0" encoding="utf-8"?>\n'
    '<root status_code="401" query="/serverinfo" '
    'status_message="The client is not authorized. Certificate verification failed."/>'
)


def require(source: str, pattern: str, description: str) -> None:
    if re.search(pattern, source, re.MULTILINE | re.DOTALL) is None:
        raise AssertionError(description)


def main() -> int:
    source = SOURCE.read_text(encoding="utf-8")
    client = (ROOT / "libgamestream/client.c").read_text(encoding="utf-8-sig")
    errors = (ROOT / "libgamestream/errors.h").read_text(encoding="utf-8-sig")

    require(source, r"#define XML_MAX_DOCUMENT_BYTES", "document size is bounded")
    require(source, r"#define XML_MAX_SEARCH_TEXT_BYTES", "text allocation is bounded")
    require(source, r"#define XML_MAX_APP_TITLE_BYTES 255u", "App titles fit the Vita UI contract")
    require(source, r"#define XML_MAX_APPS", "App count is bounded")
    require(source, r"#define XML_MAX_MODES", "display-mode count is bounded")
    require(source, r"#define XML_MAX_DEPTH", "XML nesting depth is bounded")
    if client.count("xml_status(") != client.count("(ret = xml_status("):
        raise AssertionError("client propagates every XML status/parser failure")
    require(source, r"resized\s*=\s*\(char \*\) realloc", "realloc uses a temporary owner")
    require(source, r"if \(resized == NULL\)", "realloc failure is propagated")
    require(source, r"if \(parser == NULL\)", "parser allocation is checked")
    require(source, r"XML_ERROR_NO_MEMORY", "Expat OOM is propagated")
    require(source, r"free_app_list\(query\.list\)", "partial App lists are freed")
    require(source, r"free_mode_list\(query\.list\)", "partial mode lists are freed")
    require(source, r"static char xml_status_error", "status errors use bounded storage")
    require(
        source,
        r"query\.status\s*==\s*401\s*\?\s*GS_CLIENT_UNAUTHORIZED\s*:\s*GS_ERROR",
        "Sunshine's HTTPS authorization rejection has a typed result",
    )
    if "#define GS_CLIENT_UNAUTHORIZED -12" not in errors:
        raise AssertionError("typed Sunshine client-authorization error is missing")

    unauthorized = ET.fromstring(SUNSHINE_UNAUTHORIZED_SERVERINFO)
    if (
        unauthorized.tag != "root"
        or unauthorized.attrib.get("status_code") != "401"
        or unauthorized.attrib.get("query") != "/serverinfo"
        or unauthorized.attrib.get("status_message")
        != "The client is not authorized. Certificate verification failed."
    ):
        raise AssertionError("current Sunshine unauthorized-serverinfo fixture changed")
    require(source, r"\*app_list = NULL", "App output is cleared before parsing")
    require(source, r"\*mode_list = NULL", "mode output is cleared before parsing")
    require(
        source,
        r"parse_unsigned_text\(query->memory, &id, 0\)",
        "App ID zero is reserved as the no-running-App sentinel",
    )

    forbidden = {
        r"\bstrdup\s*\(": "status messages must not allocate unowned storage",
        r"->memory\s*=\s*realloc\s*\(": "realloc must not overwrite its owner",
        r"XML_ParserCreate\([^;]+;\s*XML_SetUserData":
            "parser callbacks must not be configured before a null check",
    }
    for pattern, description in forbidden.items():
        if re.search(pattern, source, re.MULTILINE | re.DOTALL):
            raise AssertionError(description)

    print("Sunshine XML parser hardening contract: PASS")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except AssertionError as error:
        print(f"Sunshine XML parser hardening contract: FAIL: {error}", file=sys.stderr)
        raise SystemExit(1)
