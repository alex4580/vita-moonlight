#!/usr/bin/env python3
"""Enforce the Vita saved-host scanner's lifecycle and latency contract."""

from __future__ import annotations

import re
import sys
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "src" / "check_host.c"
DISCOVERY_SOURCE = ROOT / "src" / "gui" / "ui_device.c"


def function_body(source: str, name: str) -> str:
    match = re.search(
        rf"\b(?:void|int|SceUID|static\s+void|static\s+int|static\s+bool)\s+{re.escape(name)}\s*\([^)]*\)\s*\{{",
        source,
    )
    if match is None:
        return ""

    start = match.end() - 1
    depth = 0
    for offset, character in enumerate(source[start:], start=start):
        if character == "{":
            depth += 1
        elif character == "}":
            depth -= 1
            if depth == 0:
                return source[start : offset + 1]
    return ""


def main() -> int:
    source = SOURCE.read_text(encoding="utf-8")
    discovery = DISCOVERY_SOURCE.read_text(encoding="utf-8")
    start = function_body(source, "start_host_scan_thread")
    stop = function_body(source, "stop_host_scan_thread")
    worker = function_body(source, "host_scan_thread")
    probe = function_body(source, "ping_host")
    discovery_worker = function_body(discovery, "mdns_discovery_main")
    discovery_start = function_body(discovery, "start_search_thread")
    discovery_stop = function_body(
        discovery, "stop_search_thread_if_running"
    )
    discovery_end = function_body(discovery, "end_search_thread")
    discovery_select = function_body(
        discovery, "ui_search_device_callback"
    )
    discovery_publish = function_body(
        discovery, "moonlight_found_callback"
    )
    discovery_menu = function_body(discovery, "ui_search_device_loop")
    errors: list[str] = []

    def require(condition: bool, message: str) -> None:
        if not condition:
            errors.append(message)

    require(source.count("static SceUID host_scan_thread_id") == 1,
            "scanner must own exactly one kernel-thread handle")
    require("host_scan_thread_ids" not in source and
            "host_scan_thread_count" not in source and
            "MAX_SCAN_THREADS" not in source,
            "legacy multi-thread handle pool must remain removed")
    require(bool(start) and bool(stop) and bool(worker) and bool(probe),
            "scanner start, stop, worker, and bounded probe functions must exist")
    require("g_host_status[" not in start,
            "starting a scan must preserve cached host status")
    require("reconcile_host_status_cache();" in start and
            "cached_host_name" in source,
            "cached status must follow stable host identity when indexes move")
    require("sceKernelWaitThreadEnd(tid, NULL, NULL)" in stop,
            "stop must join the worker without a short unsafe timeout")
    require(stop.find("sceKernelWaitThreadEnd") < stop.find("sceKernelDeleteThread"),
            "the worker may only be deleted after it is joined")
    require("if (result < 0)" in stop and
            stop.find("if (result < 0)") < stop.find("sceKernelDeleteThread"),
            "a failed join must prevent deletion of a live worker")
    require("HOST_SCAN_STOP_REQUESTED" in stop and
            "HOST_SCAN_RUNNING" in worker,
            "worker shutdown must use an explicit cooperative state")
    require(worker.count("udp_sniffer_vita_init()") == 1 and
            worker.count("udp_sniffer_vita_set_callback(host_scan_mdns_cb)") == 1,
            "the worker must share one mDNS listener across all hosts")
    require("Probe at most one host per tick" in worker,
            "each scanner tick must bound work to one host probe")
    require("while (!scan_mdns_found" not in source and
            "scan_mdns_hostname_ref" not in source,
            "per-host blocking mDNS polling must remain removed")
    require("HOST_OFFLINE_MAX_RETRY_TICKS" in source and
            "offline_retry_ticks" in worker,
            "offline hosts must use capped retry backoff")
    require("if (!info.paired)" in worker and
            worker.find("if (!info.paired)") < worker.find("ping_host("),
            "saved unpaired entries must not generate reachability probes")
    require("if (mdns_active) udp_sniffer_vita_poll();" in worker and
            "known_devices.devices[i].paired" in worker,
            "an unpaired-only saved list must not start discovery traffic")
    require(worker.find("copy_host_string(pending_ip_update") <
            worker.find("pending_ip_update_idx = selected"),
            "an IP-change address must be complete before its index is published")
    require("SO_NONBLOCK" in probe or "O_NONBLOCK" in probe,
            "reachability probes must use non-blocking sockets")
    require("select(" in probe and "getsockopt(" in probe and
            "SO_ERROR" in probe and "HOST_PROBE_TIMEOUT_US" in probe,
            "reachability probes must be bounded and verify SO_ERROR")
    require("SO_RCVTIMEO" not in probe and "SO_SNDTIMEO" not in probe,
            "scanner probes must not regress to blocking connect timeouts")
    require(all((discovery_worker, discovery_start, discovery_stop,
                 discovery_end, discovery_select, discovery_publish,
                 discovery_menu)),
            "discovery worker lifecycle functions must remain inspectable")
    require("static int search_thread_status" in discovery and
            "static SceUID search_thread_id = -1" in discovery and
            "__atomic_load_n(&search_thread_status, __ATOMIC_ACQUIRE)" in
            discovery and
            "__atomic_store_n(&search_thread_status, status, "
            "__ATOMIC_RELEASE)" in discovery,
            "discovery must atomically publish state and own one handle")
    require("__atomic_load_n(&found_device, __ATOMIC_ACQUIRE)" in discovery
            and "__atomic_store_n(&found_device, count, __ATOMIC_RELEASE)"
            in discovery,
            "discovery result count must be an acquire/release publication boundary")
    require(discovery_publish.find("devices[count].port = port;") <
            discovery_publish.find("publish_device_count(count + 1);"),
            "a discovery slot must be complete before its count is published")
    require("int count = discovered_device_count();" in discovery_select and
            "device_index >= count" in discovery_select and
            discovery_select.find("device_index >= count") <
            discovery_select.find("device_info_t *dev = &devices[device_index]"),
            "discovery selection must acquire and bounds-check before dereference")
    require("int count = discovered_device_count();" in discovery_menu and
            "for (int i = 0; i < count; i++)" in discovery_menu,
            "the discovery menu must render an acquired immutable snapshot")
    require("int rendered_count = context ? *(const int *)context : count;" in
            discovery_select and "if (count != rendered_count)" in
            discovery_select and "DEVICE_VIEW_ITEM + count - 1" not in
            discovery_select,
            "discovery refresh must compare snapshot counts without an empty-list or filtered-slot sentinel")
    require("sceKernelWaitThreadEnd(thid, NULL, NULL)" in discovery_end and
            discovery_end.find("sceKernelWaitThreadEnd") <
            discovery_end.find("sceKernelDeleteThread"),
            "discovery stop must join before deleting its worker")
    require("int result = end_search_thread(search_thread_id);" in
            discovery_stop and
            discovery_stop.find("if (result < 0)") <
            discovery_stop.find("search_thread_id = -1"),
            "a failed discovery join must retain handle ownership")
    require("stop_search_thread_if_running() < 0" in discovery_start and
            discovery_start.find("stop_search_thread_if_running() < 0") <
            discovery_start.find("sceKernelCreateThread"),
            "discovery restart must refuse to overlap an old worker")
    require("udp_sniffer_vita_set_callback(NULL)" in discovery_worker and
            "udp_sniffer_vita_deinit()" in discovery_worker,
            "the discovery worker must release its shared mDNS listener")
    require("stop_search_thread_if_running() < 0" in discovery_select and
            discovery_select.find("stop_search_thread_if_running() < 0") <
            discovery_select.find("ui_connect_and_pairing(dev)"),
            "PIN pairing must never overlap the discovery worker")

    if errors:
        for error in errors:
            print(f"error: {error}", file=sys.stderr)
        return 1

    print("Vita host scanner lifecycle contract verified.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
