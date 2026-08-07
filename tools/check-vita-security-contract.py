#!/usr/bin/env python3
"""Guard the Vita client's pairing, identity, and lifecycle invariants."""

from __future__ import annotations

import hashlib
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


def function_body(source: str, start_marker: str, end_marker: str) -> str:
    start = source.find(start_marker)
    end = source.find(end_marker, start + len(start_marker))
    if start < 0 or end < 0:
        return ""
    return source[start:end]


def parse_version_major_vector(text: str) -> int | None:
    """Mirror the bounded appversion grammar enforced by the Vita client."""
    if len(text) > 64:
        return None
    match = re.fullmatch(
        r"\s*([0-9]+)(?:\.-?[0-9]+)*\s*",
        text,
    )
    if match is None:
        return None
    major = int(match.group(1))
    if major > 2_147_483_647:
        return None
    for component in text.strip().split(".")[1:]:
        value = int(component)
        if value < -2_147_483_648 or value > 2_147_483_647:
            return None
    return major


def decode_hex_vector(
    text: str,
    expected_length: int | None = None,
) -> bytes | None:
    """Mirror the locale- and scanf-independent Vita pairing decoder."""
    if len(text) % 2 != 0 or (
        expected_length is not None and len(text) // 2 != expected_length
    ):
        return None
    decoded = bytearray()
    for offset in range(0, len(text), 2):
        pair = text[offset : offset + 2]
        if not re.fullmatch(r"[0-9A-Fa-f]{2}", pair):
            return None
        decoded.append(int(pair, 16))
    return bytes(decoded)


def main() -> int:
    client = read("libgamestream/client.c")
    client_header = read("libgamestream/client.h")
    http = read("libgamestream/http.c")
    http_header = read("libgamestream/http.h")
    connect = read("src/gui/ui_connect.c")
    connection = read("src/connection.c")
    connection_header = read("src/connection.h")
    util = read("src/util.c")
    pair = function_body(client, "int gs_pair(", "int gs_applist(")
    abort_pair = function_body(
        client, "static int abort_pairing_session(", "int gs_pair("
    )
    pairing_hash = function_body(
        client,
        "static bool hash_pairing_challenge_binding(",
        "static bool certificate_signature_view(",
    )
    constant_time_compare = function_body(
        client,
        "static bool constant_time_equal(",
        "static bool hash_pairing_challenge_binding(",
    )
    http_init = function_body(http, "int http_init(", "static const char *request_path(")
    http_request = function_body(
        http, "int http_request_with_timeout(", "int http_request(char*"
    )
    start_app = function_body(client, "int gs_start_app(", "int gs_quit_app(")
    load_cert = function_body(
        client, "static int load_cert(", "static int load_serverinfo("
    )
    gs_init = function_body(client, "int gs_init(", "void gs_cleanup(")

    require(
        "CURLOPT_PINNEDPUBLICKEY" in http,
        "libgamestream/http.c: Sunshine SPKI pinning is missing",
    )
    require(
        'strncmp(url, "https://", 8) == 0 && !http_has_server_pin()' in http,
        "libgamestream/http.c: HTTPS must fail closed before secure pairing",
    )
    require(
        "http_set_server_pin_from_pem(plaincert, false)" in client
        and "http_set_server_pin_from_pem(plaincert, true)" in client,
        "libgamestream/client.c: pairing must activate then persist the verified pin",
    )
    proof_steps = [
        pair.find("memcpy(serverResponse, challengeResponse"),
        pair.find("verifySignature((char *)pairingSecret"),
        pair.find("copy_pem_certificate_signature("),
        pair.find("hash_pairing_challenge_binding("),
        pair.find("constant_time_equal("),
        pair.find("http_set_server_pin_from_pem(plaincert, false)"),
    ]
    require(
        all(step >= 0 for step in proof_steps)
        and proof_steps == sorted(proof_steps),
        "libgamestream/client.c: verify the signed, PIN-bound Sunshine challenge before activating its TLS pin",
    )
    sha256_binding_steps = [
        pairing_hash.find("SHA256_Update(&context, challenge, 16)"),
        pairing_hash.find("SHA256_Update(&context, serverCertificateSignature,"),
        pairing_hash.find("SHA256_Update(&context, serverSecret, 16)"),
    ]
    sha1_binding_steps = [
        pairing_hash.find("SHA1_Update(&context, challenge, 16)"),
        pairing_hash.find("SHA1_Update(&context, serverCertificateSignature,"),
        pairing_hash.find("SHA1_Update(&context, serverSecret, 16)"),
    ]
    require(
        all(step >= 0 for step in sha256_binding_steps + sha1_binding_steps)
        and sha256_binding_steps == sorted(sha256_binding_steps)
        and sha1_binding_steps == sorted(sha1_binding_steps)
        and "serverCertificateSignatureLength, pairingSecret, hash_length"
        in pair,
        "libgamestream/client.c: pairing hash must bind challenge || server-cert signature || server secret for both protocol generations",
    )
    require(
        "difference |= (unsigned int)(left[i] ^ right[i])" in constant_time_compare
        and "memcmp(" not in constant_time_compare
        and "strncmp(" not in constant_time_compare,
        "libgamestream/client.c: pairing proof comparison must be constant-time",
    )

    # Deterministic protocol vectors guard the exact field order used by
    # Moonlight/Sunshine for both GameStream pairing generations.
    vector_challenge = bytes(range(0x00, 0x10))
    vector_server_cert_signature = bytes(range(0x20, 0x40))
    vector_server_secret = bytes(range(0xF0, 0x100))
    vector_material = (
        vector_challenge + vector_server_cert_signature + vector_server_secret
    )
    require(
        hashlib.sha256(vector_material).hexdigest()
        == "25973ab39010a3359dce01cd2b08853d56ec7bf4ad3156cdf8f3f183878594f6"
        and hashlib.sha1(vector_material).hexdigest()
        == "bf54b2a51ccc1e236887091ecf487399bc872e4e",
        "pairing challenge binding vectors changed: expected challenge || server-cert signature || server secret",
    )
    require(
        "Request %s" not in http
        and 'printf("Response:' not in http
        and "CURLOPT_VERBOSE" not in http,
        "libgamestream/http.c: support logs must not expose pairing URLs or bodies",
    )
    require(
        "HTTP_MAX_RESPONSE_SIZE" in http,
        "libgamestream/http.c: HTTP response size must be bounded",
    )
    require(
        "HTTP_TIMEOUT_PAIRING_USER_SECONDS 120L" in http_header
        and "HTTP_TIMEOUT_PAIRING_ABORT_SECONDS 5L" in http_header
        and "HTTP_TIMEOUT_ORDINARY_SECONDS 30L" in http_header
        and "HTTP_TIMEOUT_LAUNCH_SECONDS 120L" in http_header
        and "http_request_with_timeout" in http_header,
        "libgamestream/http.h: pairing, ordinary, and launch timeout profiles are required",
    )
    require(
        "gs_refresh(server)" not in pair
        and "gs_unpair(server)" not in pair,
        "libgamestream/client.c: a committed Sunshine pairing must not be rolled back by a post-pair metadata refresh",
    )
    require(
        '"pairing identity"' in pair
        and pair.count("pairingUniqueId") >= 7
        and "persist_unique_id_atomic(\n          unique_id_path, pairingUniqueId)"
        in pair
        and pair.find("phrase=pairchallenge")
        < pair.find(
            "persist_unique_id_atomic(\n          unique_id_path, pairingUniqueId)"
        ),
        "libgamestream/client.c: each PIN attempt must use a fresh identity and persist it only after the final pinned challenge",
    )
    pending_commit = pair.find(
        "persist_unique_id_atomic(\n           pairing_pending_path, pairingUniqueId)"
    )
    first_pair_request = pair.find("phrase=getservercert")
    invalid_pending_block = function_body(
        pair,
        "if (pendingState == PAIRING_PENDING_INVALID)",
        "if (pendingState == PAIRING_PENDING_VALID",
    )
    require(
        '#define PAIRING_PENDING_FILE_NAME "pairing-pending.dat"' in client
        and "load_pairing_pending_id(abandonedPairingId)" in pair
        and pair.find("abort_pairing_session(\n          server, abandonedPairingId")
        < pending_commit
        < first_pair_request
        and "pairingSessionOpen = true;" in pair
        and "if (pairingSessionOpen)" in pair
        and "clear_pairing_pending_files();" in pair
        and "clientpairingsecret=00" in abort_pair
        and "HTTP_TIMEOUT_PAIRING_ABORT_SECONDS" in abort_pair
        and "if ((ret = xml_status(" not in abort_pair,
        "libgamestream/client.c: wrong, timed-out, and interrupted PIN attempts must journal and abort the exact Sunshine session before retry",
    )
    require(
        "clear_pairing_pending_files" not in invalid_pending_block
        and "forget this PC on the Vita" in invalid_pending_block,
        "libgamestream/client.c: a damaged pending-session record must remain fail-closed until Sunshine is restarted and the saved PC is explicitly forgotten",
    )
    require(
        "HTTP_TIMEOUT_PAIRING_USER_SECONDS" in pair
        and "HTTP_TIMEOUT_LAUNCH_SECONDS" in start_app
        and "HTTP_TIMEOUT_ORDINARY_SECONDS" in start_app
        and "CURLOPT_TIMEOUT, timeoutSeconds" in http_request,
        "libgamestream: every user-pairing/launch/ordinary request must select its timeout explicitly",
    )
    require(
        "if (ret != GS_OK) http_cleanup();" in http_init,
        "libgamestream/http.c: failed HTTP initialization must release curl and pin state",
    )
    require(
        "certificatePathLength" in load_cert
        and "keyPathLength" in load_cert
        and "p12PathLength" in load_cert
        and load_cert.count(">= sizeof(") >= 3,
        "libgamestream/client.c: every pairing credential path must reject truncation",
    )
    require(
        "ret = load_server_status(server);" in gs_init
        and "if (ret != GS_OK)" in gs_init
        and "free_server_status_data(server);" in gs_init
        and "http_cleanup();" in gs_init
        and "cleanup_client_credentials();" in gs_init,
        "libgamestream/client.c: failed host initialization must release all client state",
    )
    require(
        "primaryState = read_pin_file" in http
        and "backupState = read_pin_file" in http
        and "backupState == PIN_FILE_VALID" in http,
        "libgamestream/http.c: a valid pin backup must recover a missing or corrupt primary",
    )

    require(
        "fread(value, 1, UNIQUEID_CHARS, file)" in client,
        "libgamestream/client.c: unique ID must use byte-count fread semantics",
    )
    require(
        '#define LEGACY_SHARED_UNIQUE_ID "0123456789ABCDEF"' in client
        and "legacySharedIdentity && !hasAuthenticatedServerPin" in client
        and "persist_unique_id_atomic" in client
        and "if (hadPreviousIdentity) rename(backupPath, uniqueFilePath);" in client
        and "load_unique_id(keyDirectory, http_has_server_pin())" in client,
        "libgamestream/client.c: rotate the shared legacy ID atomically only during an unpinned migration",
    )
    rand_calls = [
        line.strip()
        for line in client.splitlines()
        if "RAND_bytes(" in line
    ]
    require(
        len(rand_calls) == 1 and "secure_random" in client,
        "libgamestream/client.c: every random value must use the checked helper",
    )
    require(
        "gs_generate_pin(pin)" in connect and "rand() % 10" not in connect,
        "src/gui/ui_connect.c: pairing PINs must use secure randomness",
    )

    require(
        "void gs_cleanup(PSERVER_DATA server)" in client
        and "void gs_cleanup(PSERVER_DATA server);" in client_header,
        "libgamestream: explicit client cleanup is missing",
    )
    require(
        "SERVER_DATA probe = {0};" in connect and "gs_cleanup(&probe);" in connect,
        "src/gui/ui_connect.c: reachability checks must use a disposable client",
    )
    require(
        "gs_free_applist(&server_applist);" in connect,
        "src/gui/ui_connect.c: application lists must be reclaimed",
    )
    require(
        "malloc(sizeof(APP_LIST))" not in util
        and "char *name = a->name;" in util,
        "src/util.c: App sorting must not allocate or dereference an unchecked swap node",
    )
    require(
        "void *resized = realloc(*buf, required_size);" in util
        and "*buf = realloc" not in util
        and "abort();" not in util,
        "src/util.c: buffer growth must preserve the previous allocation on OOM",
    )
    require(
        "allowUnsupportedVersion" in client_header
        and "is_vita_contract_mode" in client
        and "sops && is_vita_contract_mode(config)" in client,
        "libgamestream: version bypass and bounded mode negotiation must be separate",
    )
    launch_confirmation = start_app.find("server->currentGame = appId;")
    session_validation = start_app.find("result[0] == '\\0'")
    require(
        launch_confirmation >= 0
        and session_validation >= 0
        and session_validation < launch_confirmation,
        "libgamestream/client.c: do not record a running app before Sunshine confirms a non-empty session",
    )
    require(
        '"gamesession", &result' in start_app
        and '"resume", &result' in start_app
        and start_app.find('"gamesession", &result')
        < start_app.find('"resume", &result')
        < launch_confirmation,
        "libgamestream/client.c: an omitted gamesession field must fall back to the resume field",
    )
    require(
        "unsigned long codecModeSupport = SCM_H264;" in client
        and "serverCodecModeSupportText[0] != '\\0'" in client,
        "libgamestream/client.c: an omitted codec field must retain legacy H.264 support",
    )
    require(
        "parse_unsigned_decimal_field" in client
        and "parse_version_major_field" in client
        and "SERVERINFO_NUMERIC_TEXT_MAX" in client
        and "atoi(" not in client,
        "libgamestream/client.c: host numeric fields must use bounded range-checked parsing",
    )
    valid_appversions = {
        "7.1.431.-1": 7,
        "7.1.431.0": 7,
        "7": 7,
        "  7.1.-2  ": 7,
    }
    invalid_appversions = (
        "",
        "-1.0.0.0",
        "+7.1.0.0",
        "7.",
        "7.1.release",
        "7.1.+2",
        "2147483648.1.0.0",
        "7.1.431.-2147483649",
        "7.1.431.2147483648",
        "7" * 65,
    )
    require(
        all(
            parse_version_major_vector(version) == expected
            for version, expected in valid_appversions.items()
        )
        and all(
            parse_version_major_vector(version) is None
            for version in invalid_appversions
        )
        and "while (*component == '.')" in client
        and "if (*digits == '-') digits++;" in client
        and "strtol(component, &componentEnd, 10)" in client
        and "componentValue < INT_MIN || componentValue > INT_MAX" in client
        and "Sunshine returned an invalid currentgame field" in client
        and "Sunshine returned an invalid ServerCodecModeSupport field" in client
        and "Sunshine returned an invalid HttpsPort field" in client
        and "Sunshine returned an invalid appversion field" in client
        and "Sunshine returned an invalid numeric server field" not in client,
        "libgamestream/client.c: bounded appversion parsing must accept Sunshine's signed build sentinel and identify field failures",
    )
    require(
        "supported Sunshine host version" in client
        and "Sunshine/Apollo host version" not in client,
        "libgamestream/client.c: public compatibility errors must direct users to the supported Sunshine host",
    )
    hex_decoder = function_body(
        client,
        "static int hex_nibble(",
        "static int sign_it(",
    )
    sunshine_pem_fixture = (
        b"-----BEGIN CERTIFICATE-----\n"
        b"MIIBVitaMoonlightCompatibilityFixture==\n"
        b"-----END CERTIFICATE-----\n"
    )
    require(
        decode_hex_vector("000aA5fF") == b"\x00\x0a\xa5\xff"
        and decode_hex_vector(sunshine_pem_fixture.hex())
        == sunshine_pem_fixture
        and decode_hex_vector("000a", expected_length=3) is None
        and all(
            decode_hex_vector(value) is None
            for value in ("0", "GG", "0x", "0a 1", "0a\n")
        )
        and "sscanf(" not in hex_decoder
        and "strtol(" not in hex_decoder
        and "isxdigit(" not in hex_decoder
        and "if (value >= '0' && value <= '9')" in hex_decoder
        and "if (value >= 'a' && value <= 'f')" in hex_decoder
        and "if (value >= 'A' && value <= 'F')" in hex_decoder
        and "inputLength / 2 != outputLength" in hex_decoder
        and "Sunshine returned a non-hex pairing certificate" in client,
        "libgamestream/client.c: pairing hex must use a strict Vita-safe ASCII nibble decoder",
    )
    quit_app = function_body(client, "int gs_quit_app(", "int gs_init(")
    require(
        'strcmp(result, "1") != 0' in start_app
        and 'strcmp(result, "1") != 0' in quit_app,
        "libgamestream/client.c: launch and cancel responses require exact success confirmation",
    )
    require(
        "securePairingRequired" in client_header
        and 'MENU_ENTRY(CONNECT_PAIRUNPAIR, "Pair securely")' in connect,
        "Vita UI: existing installs need an explicit secure-pairing migration",
    )
    require(
        "SERVER_PIN_FILE_NAME" in http_header and "http_cleanup(void)" in http_header,
        "libgamestream/http.h: pin persistence/cleanup contract is incomplete",
    )

    abort_section = ""
    if "int connection_abort_attempt()" in connection:
        abort_section = connection.split("int connection_abort_attempt()", 1)[1]
        abort_section = abort_section.split("int connection_paired()", 1)[0]
    require(
        "int connection_abort_attempt();" in connection_header
        and (
            "connection_status != LI_READY" in abort_section
            or "connection_state_load() != LI_READY" in abort_section
        )
        and "LI_DISCONNECTED" in abort_section
        and "LiStopConnection" not in abort_section,
        "src/connection: READY attempts need an abort transition without stream teardown",
    )

    release_section = ""
    if "static bool release_host_client_state(void)" in connect:
        release_section = connect.split(
            "static bool release_host_client_state(void)", 1
        )[1].split("int get_app_id", 1)[0]
    require(
        "connection_abort_attempt()" in release_section
        and "gs_cleanup(&server);" in release_section
        and release_section.find("connection_abort_attempt()")
        < release_section.find("gs_cleanup(&server);"),
        "src/gui/ui_connect.c: READY state must be aborted before client cleanup",
    )
    require(
        re.search(r"^\s*connection_reset\(\);", connect, re.MULTILINE) is None,
        "src/gui/ui_connect.c: every connection_reset result must be checked",
    )
    require(
        "build_host_key_directory" in connect
        and "HOST_KEY_COMPONENT_MAX" in connect
        and "HOST_KEY_FILE_SUFFIX_RESERVE" in connect
        and "sprintf(key_dir" not in connect,
        "src/gui/ui_connect.c: host key directories must use a bounded safe component",
    )
    require(
        "strcpy(name, list->name)" not in connect
        and "snprintf(name, name_size" in connect
        and "sprintf(current_status" not in connect,
        "src/gui/ui_connect.c: host-controlled App titles must be copied with destination bounds",
    )
    require(
        "pos[0] = -1" not in connect,
        "src/gui/ui_connect.c: an empty App list must not create a negative menu index",
    )
    require(
        "CONNECT_PAIRUNPAIR = -1001" in connect
        and "CONNECT_DISCONNECT = -1002" in connect
        and "CONNECT_QUITAPP = -1003" in connect,
        "src/gui/ui_connect.c: local actions must not collide with Sunshine App IDs",
    )
    require(
        "Remain in LI_PAIRED while the" in connect,
        "src/gui/ui_connect.c: secure-pair migration must reload without disconnecting its paired state",
    )
    require(
        "Delete the saved PC in Vita Moonlight" in connect
        and "add it again" in connect
        and "Pair securely" in connect,
        "Vita UI: Sunshine identity mismatch recovery must give explicit delete/add/pair steps",
    )
    require(
        "if (!server->httpsPort || !hasAuthenticatedServerPin)" in client,
        "libgamestream/client.c: unpinned refresh must not skip HTTP discovery when HTTPS port is cached",
    )

    if errors:
        print("Vita security contract check FAILED:", file=sys.stderr)
        for error in errors:
            print(f"- {error}", file=sys.stderr)
        return 1

    print("Vita security contract check passed.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
