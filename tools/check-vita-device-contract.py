#!/usr/bin/env python3
"""Guard saved-computer pairing and persistence invariants on Vita."""

from __future__ import annotations

import re
import sys
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
errors: list[str] = []


def read(relative_path: str) -> str:
    try:
        return (ROOT / relative_path).read_text(encoding="utf-8-sig")
    except OSError as error:
        errors.append(f"{relative_path}: could not read file: {error}")
        return ""


def require(condition: bool, message: str) -> None:
    if not condition:
        errors.append(message)


def body(source: str, start: str, end: str) -> str:
    first = source.find(start)
    if first < 0:
        return ""
    last = len(source) if not end else source.find(end, first + len(start))
    return "" if last < 0 else source[first:last]


def main() -> int:
    device_header = read("src/device.h")
    device = read("src/device.c")
    connect = read("src/gui/ui_connect.c")
    ui = read("src/gui/ui.c")
    ip_update = read("src/gui/ui_check.c")

    upsert = body(device, "device_info_t* upsert_device(", "void load_all_known_devices(")
    save = body(device, "bool save_device_info(", "")
    pairing = body(connect, "device_info_t* ui_connect_and_pairing(", "void ui_connect_resume(")
    change_name = body(ui, "case HOST_MANAGE_CHANGE_NAME:", "case HOST_MANAGE_FORCE_CONNECT:")

    require(
        "#define DEVICE_MAX_COUNT 16" in device_header
        and "known_devices.count >= DEVICE_MAX_COUNT" in device,
        "saved-computer count must be bounded to the host-status capacity",
    )
    require(
        "device_info_t* upsert_device(const device_info_t *info);" in device_header
        and "if (p->paired) merged.paired = true;" in upsert
        and "if (merged.mac[0] == '\\0')" in upsert
        and "return p;" in upsert,
        "discovery upsert must return the canonical record and preserve trusted state",
    )
    require(
        "bool save_device_info(const device_info_t *info);" in device_header
        and '"%s.tmp"' in save
        and '"%s.bak"' in save
        and "fflush(fd) == 0" in save
        and "fclose(fd) == 0" in save
        and "sceIoRename(temporary_path, path)" in save
        and "sceIoRename(backup_path, path)" in save,
        "device.ini must use a checked, recoverable journaled write",
    )
    require(
        "ini_parse(backup_path" in device
        and "sceIoRename(backup_path, path)" in device,
        "device.ini loading must recover the last complete backup",
    )
    require(
        "info = upsert_device(info);" in pairing
        and "info->paired = server.paired;" in pairing
        and "info->paired = true;" in pairing
        and pairing.find("info->paired = true;")
        < pairing.find("if (connection_paired() != 0)")
        and pairing.count("if (!save_device_info(info))") >= 2,
        "pair/re-pair must mutate and persist the canonical saved record",
    )
    require(
        'MENU_SEPARATOR("Saved computers")' in ui
        and '"Pairing required"' in ui
        and re.search(
            r"if\s*\(!cur->paired\)\s*\{\s*continue\s*;", ui, re.DOTALL
        )
        is None
        and "calloc(menu_capacity" in ui,
        "interrupted pairing entries must stay visible in a capacity-safe menu",
    )
    require(
        "char display_name[256];" in device_header
        and "info->display_name" in change_name
        and "info->name =" not in change_name
        and "remove_device(old_name)" not in change_name,
        "display aliases must not rename credential directories or host identity",
    )
    require(
        "check_connection(info->name" in ip_update
        and "info->paired = true" not in ip_update
        and "load_all_known_devices()" not in ip_update,
        "an mDNS address hint must be pin-verified without manufacturing paired state",
    )

    if errors:
        print("Vita saved-computer contract failed:", file=sys.stderr)
        for error in errors:
            print(f"- {error}", file=sys.stderr)
        return 1

    print("Vita saved-computer pairing and persistence contract passed.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
