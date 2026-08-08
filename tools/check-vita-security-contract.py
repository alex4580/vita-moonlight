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
    crypto = read("libgamestream/crypto.c")
    crypto_header = read("libgamestream/crypto.h")
    mkcert = read("libgamestream/mkcert.c")
    http = read("libgamestream/http.c")
    http_header = read("libgamestream/http.h")
    xml = read("libgamestream/xml.c")
    error_codes = read("libgamestream/errors.h")
    main_source = read("src/main.c")
    vita_input = read("src/input/vita.c")
    root_cmake = read("CMakeLists.txt")
    standalone_cmake = read("libgamestream/CMakeLists.txt")
    connect = read("src/gui/ui_connect.c")
    connection = read("src/connection.c")
    persist_unique = function_body(
        client,
        "static int persist_unique_id_atomic(",
        "static bool pairing_pending_sibling_path(",
    )
    connection_header = read("src/connection.h")
    util = read("src/util.c")
    pair = function_body(client, "int gs_pair(", "int gs_applist(")
    abort_pair = function_body(
        client,
        "static int abort_pairing_session(",
        "static int finish_pairing_challenge(",
    )
    pairing_journal_load = function_body(
        client,
        "static PAIRING_JOURNAL_STATE load_pairing_journal(",
        "static int persist_pairing_journal(",
    )
    pairing_journal_persist = function_body(
        client,
        "static int persist_pairing_journal(",
        "static int transition_pairing_journal(",
    )
    pairing_reconcile = function_body(
        client, "static int reconcile_pairing_journal(", "int gs_pair("
    )
    load_unique = function_body(
        client, "static int load_unique_id(", "static bool credential_sibling_path("
    )
    pairing_hash = function_body(
        client,
        "static bool hash_pairing_challenge_binding(",
        "static int copy_pem_certificate_signature(",
    )
    constant_time_compare = function_body(
        client,
        "static bool constant_time_equal(",
        "static bool hash_pairing_challenge_binding(",
    )
    http_init = function_body(
        http, "int http_init_with_recovery(", "static const char *request_path("
    )
    http_request_transport = function_body(
        http,
        "static int http_request_with_timeout_locked(",
        "int http_request_with_timeout(",
    )
    http_request_wrapper = function_body(
        http,
        "int http_request_with_timeout(",
        "HTTP_BRIDGE_RESULT http_bridge_request_with_timeout_ms(",
    )
    start_app = function_body(client, "int gs_start_app(", "int gs_quit_app(")
    load_cert = function_body(
        client, "static int load_cert(", "static int load_serverinfo("
    )
    load_serverinfo = function_body(
        client, "static int load_serverinfo(", "static void free_server_status_data("
    )
    load_server_status = function_body(
        client, "static int load_server_status(", "static void free_server_status_data("
    )
    gs_init = function_body(client, "int gs_init(", "void gs_cleanup(")
    host_probe = function_body(
        connect,
        "static host_probe_result_t probe_connection(",
        "bool check_connection(",
    )
    strict_connection_probe = function_body(
        connect,
        "bool check_connection(",
        "void ui_connect_paired_device(",
    )
    paired_device_connect = function_body(
        connect,
        "void ui_connect_paired_device(",
        "void ui_connect_address(",
    )
    migration_flag = paired_device_connect.find("info->paired = false;")
    migration_save = paired_device_connect.find(
        "save_device_info(info)", migration_flag
    )
    migration_menu = paired_device_connect.find("while (ui_connected_menu()")

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
        and "http_set_server_pin(authenticatedServerPin, true)" in client,
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
    binding_steps = [
        pairing_hash.find(".data = challenge"),
        pairing_hash.find(".data = serverCertificateSignature"),
        pairing_hash.find(".data = serverSecret"),
        pairing_hash.find("gs_crypto_hash_segments("),
    ]
    require(
        all(step >= 0 for step in binding_steps)
        and binding_steps == sorted(binding_steps)
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
        and pair.find("clientpairingsecret=%s")
        < pair.find("PAIRING_STAGE_AUTHORIZED")
        < pair.find("http_set_server_pin(authenticatedServerPin, true)")
        < pair.find(
            "persist_unique_id_atomic(\n          unique_id_path, pairingUniqueId)"
        )
        < pair.find("finish_pairing_challenge(server, pairingUniqueId)"),
        "libgamestream/client.c: each PIN attempt must use a fresh ID and commit the authenticated pin/ID after host authorization but before the retryable final HTTPS probe",
    )
    pending_commit = pair.find("PAIRING_STAGE_OPENED, pairingUniqueId")
    first_pair_request = pair.find("phrase=getservercert")
    invalid_pending_block = function_body(
        pair,
        "if (pendingState == PAIRING_JOURNAL_INVALID)",
        "if (pendingState == PAIRING_JOURNAL_VALID",
    )
    require(
        '#define PAIRING_PENDING_FILE_NAME "pairing-pending.dat"' in client
        and "load_pairing_journal()" in pair
        and pair.find("abort_pairing_session(\n            server, pairing_journal.pairId")
        < pending_commit
        < first_pair_request
        and "pairingSessionOpen = true;" in pair
        and "if (pairingSessionOpen)" in pair
        and "PAIRING_STAGE_COMPLETE" in pair
        and "clientpairingsecret=00" in abort_pair
        and "HTTP_TIMEOUT_PAIRING_ABORT_SECONDS" in abort_pair
        and "if ((ret = xml_status(" not in abort_pair,
        "libgamestream/client.c: wrong, timed-out, and interrupted PIN attempts must journal and abort the exact Sunshine session before retry",
    )
    require(
        "remove(" not in invalid_pending_block
        and "forget this PC on the Vita" in invalid_pending_block,
        "libgamestream/client.c: a damaged pending-session record must remain fail-closed until Sunshine is restarted and the saved PC is explicitly forgotten",
    )
    require(
        "HTTP_TIMEOUT_PAIRING_USER_SECONDS" in pair
        and "HTTP_TIMEOUT_LAUNCH_SECONDS" in start_app
        and "HTTP_TIMEOUT_ORDINARY_SECONDS" in start_app
        and "CURLOPT_TIMEOUT, timeoutSeconds" in http_request_transport
        and "http_request_with_timeout_locked(url, data, timeoutSeconds)"
        in http_request_wrapper
        and "pthread_mutex_lock(&httpRequestMutex);" in http_request_wrapper
        and "pthread_mutex_unlock(&httpRequestMutex);" in http_request_wrapper
        and re.search(r"\bhttp_request\s*\(", client) is None
        and "int http_request(char*" not in http
        and "int http_request(char*" not in http_header,
        "libgamestream: every user-pairing/launch/ordinary request must select its timeout explicitly",
    )
    require(
        "if (ret != GS_OK) http_cleanup();" in http_init,
        "libgamestream/http.c: failed HTTP initialization must release curl and pin state",
    )
    require(
        "certificatePathLength" in load_cert
        and "keyPathLength" in load_cert
        and load_cert.count(">= sizeof(") >= 2,
        "libgamestream/client.c: every pairing credential path must reject truncation",
    )
    require(
        "persist_identity_atomic" in client
        and 'certificatePath, ".tmp"' in client
        and 'certificatePath, ".bak"' in client
        and 'keyPath, ".tmp"' in client
        and 'keyPath, ".bak"' in client
        and "candidatePairs" in load_cert
        and "identity_files_match(certificatePath, keyPath)" in client
        and "client.p12" not in client
        and "PKCS12" not in client
        and "PKCS12" not in mkcert,
        "libgamestream: PEM certificate/private-key persistence must be pair-validated, crash-recoverable, and retire the unused PKCS#12 artifact",
    )
    require(
        "foundArtifact = foundArtifact ||" in load_cert
        and "if (identity == NULL && foundArtifact)" in load_cert
        and "no files were replaced" in load_cert
        and "active_certificate_path" in load_cert
        and "active_key_path" in load_cert
        and "http_init_with_recovery(" in gs_init,
        "libgamestream/client.c: existing certificate/key artifacts must either produce a matching recoverable identity or fail closed without silent regeneration",
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
        and "temporaryState = read_pin_file" in http
        and "backupState = read_pin_file" in http
        and "PIN_FILE_INVALID" in http
        and "PIN_FILE_IO_ERROR" in http
        and "no files were replaced" in http,
        "libgamestream/http.c: primary/.tmp/.bak pin artifacts must distinguish absence, validity, corruption, and I/O failure without replacing damaged evidence",
    )
    require(
        http.find("const char *selectedPin")
        < http.find("The saved Sunshine identity files are damaged")
        and "temporaryState != PIN_FILE_ABSENT" in http
        and "backupState != PIN_FILE_ABSENT" in http,
        "libgamestream/http.c: a valid authoritative pin must recover past torn siblings while an all-invalid set remains fail-closed",
    )
    require(
        "http_server_pin_is_valid" in http_header
        and "http_init_with_recovery" in http_header
        and "recoveryServerPin == NULL" in http_init
        and "http_set_server_pin(recoveryServerPin, false)" in http_init,
        "libgamestream/http.c: interrupted authorization must install the journal-bound pin ephemerally without replacing committed trust files",
    )

    require(
        "fread(value, 1, UNIQUEID_CHARS, file)" in client,
        "libgamestream/client.c: unique ID must use byte-count fread semantics",
    )
    require(
        '#define LEGACY_SHARED_UNIQUE_ID "0123456789ABCDEF"' in client
        and "static bool is_hex_text(" in client
        and "!is_hex_text(value, UNIQUEID_CHARS)" in client
        and "legacySharedIdentity && !hasAuthenticatedServerPin" in client
        and "persist_unique_id_atomic" in client
        and "if (hadPreviousIdentity) rename(backupPath, uniqueFilePath);" in client
        and "recoveryActive ? pairing_journal.pairId : NULL" in gs_init,
        "libgamestream/client.c: rotate the shared legacy ID atomically only during an unpinned migration",
    )
    require(
        persist_unique.count("(void)remove(temporaryPath);") >= 2
        and "bool invalidPrimaryRemoved = remove(uniqueFilePath) == 0;"
        in persist_unique
        and "rename(backupPath, uniqueFilePath) == 0" in persist_unique
        and "previous identity was restored" in persist_unique,
        "libgamestream/client.c: failed unique-ID staging must be cleaned and failed committed readback must restore the verified prior identity",
    )
    require(
        "ARTIFACT_ABSENT" in load_unique
        and "ARTIFACT_VALID" in load_unique
        and 'build_unique_id_path(\n          temporaryPath' in load_unique
        and "no files were replaced" in load_unique
        and "!valid && !allAbsent" in load_unique
        and "!valid && hasAuthenticatedServerPin" in load_unique,
        "libgamestream/client.c: unique-ID primary/.tmp/.bak artifacts must fail closed and an authenticated host must never receive a silently regenerated ID",
    )
    require(
        load_unique.find("const char *selectedId")
        < load_unique.find("if (!valid && !allAbsent)")
        and "temporaryState != ARTIFACT_ABSENT" in client
        and "backupState != ARTIFACT_ABSENT" in client,
        "libgamestream/client.c: a valid authoritative Vita ID must recover past torn siblings while an all-invalid set remains fail-closed",
    )
    require(
        "PAIRING_JOURNAL_VERSION 1u" in client
        and '"%schecksum=%s\\n"' in client
        and "gs_crypto_hash(" in client
        and "candidate.generation > selected.generation" in pairing_journal_load
        and "candidate.generation == selected.generation" in pairing_journal_load
        and "ARTIFACT_INVALID" in pairing_journal_load
        and "ARTIFACT_IO_ERROR" in pairing_journal_load
        and "read_pairing_journal_file(temporaryPath, &staged)" in pairing_journal_persist
        and "read_pairing_journal_file(primaryPath, &committed)" in pairing_journal_persist,
        "libgamestream/client.c: pairing recovery must use a strict versioned/checksummed, generation-selected, read-back-verified journal",
    )
    require(
        "bool foundCorrupt = false;" in pairing_journal_load
        and "bool generationConflict = false;" in pairing_journal_load
        and "generationConflict || (!foundValid && foundCorrupt)"
        in pairing_journal_load
        and "temporaryState != ARTIFACT_ABSENT" in pairing_journal_persist
        and "backupState != ARTIFACT_ABSENT" in pairing_journal_persist,
        "libgamestream/client.c: pairing journal recovery must tolerate torn siblings only when a valid unconflicted generation remains",
    )
    journal_vector = (
        "version=1\n"
        "generation=4\n"
        "stage=3\n"
        "pair_id=0011223344556677\n"
        "server_pin=sha256//AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=\n"
        "client_pin=sha256//BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB=\n"
        "https_port=47984\n"
    )
    require(
        hashlib.sha256(journal_vector.encode("ascii")).hexdigest()
        == "0fce05c053b614a90322d3aa8ab36e6e383527da209e8b7131dbbc49205c4e15",
        "pairing journal canonical SHA-256 vector changed",
    )
    authenticated_stage = pair.find("PAIRING_STAGE_AUTHENTICATED")
    authorizing_stage = pair.find("PAIRING_STAGE_AUTHORIZING", authenticated_stage)
    authorization_request = pair.find(
        "http_request_with_timeout(\n"
        "          url, data, HTTP_TIMEOUT_PAIRING_USER_SECONDS)",
        authorizing_stage,
    )
    authorization_stages = [
        authenticated_stage,
        authorizing_stage,
        authorization_request,
        pair.find("PAIRING_STAGE_AUTHORIZED"),
        pair.find("http_set_server_pin(authenticatedServerPin, true)"),
        pair.find("PAIRING_STAGE_LOCAL_COMMITTED"),
        pair.find("finish_pairing_challenge(server, pairingUniqueId)"),
    ]
    require(
        all(step >= 0 for step in authorization_stages)
        and authorization_stages == sorted(authorization_stages)
        and "pairingSessionOpen = false;" in pair,
        "libgamestream/client.c: journal stages must bracket the ambiguous Sunshine authorization call, local commit, and final pinned proof",
    )
    require(
        "load_serverinfo(server, true)" in pairing_reconcile
        and "ret == GS_CLIENT_UNAUTHORIZED" in pairing_reconcile
        and "if (!server->paired)" in pairing_reconcile
        and "http_set_server_pin(pairing_journal.serverPin, true)"
        in pairing_reconcile
        and "persist_unique_id_atomic(" in pairing_reconcile
        and "PAIRING_STAGE_LOCAL_COMMITTED" in pairing_reconcile
        and "finish_pairing_challenge(" in pairing_reconcile
        and "PAIRING_STAGE_COMPLETE" in pairing_reconcile
        and "http_init_with_recovery(" in gs_init
        and "recoveryActive ? pairing_journal.serverPin : NULL" in gs_init,
        "libgamestream/client.c: AUTHORIZING/AUTHORIZED/LOCAL_COMMITTED restarts must reconcile current Sunshine's pinned 401 or complete both local commit and final proof",
    )
    require(
        "GS_CLIENT_UNAUTHORIZED -12" in error_codes
        and "query.status == 401 ? GS_CLIENT_UNAUTHORIZED : GS_ERROR" in xml
        and "ret == GS_CLIENT_UNAUTHORIZED" in load_server_status
        and load_server_status.count("load_serverinfo(server, false)") >= 2
        and "server->securePairingRequired = true;" in load_server_status
        and "pairingWasRequired || !http_has_server_pin()" in pair,
        "libgamestream: current Sunshine XML 401 must be typed and recover a saved host into durable secure re-pairing without weakening other pinned failures",
    )
    require(
        "gs_crypto_random(buffer, size)" in client
        and "secure_random" in client
        and "RAND_bytes(" not in client,
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
        and "Sunshine returned an invalid PairStatus field" in client
        and "Sunshine returned an invalid ServerCodecModeSupport field" in client
        and "Sunshine returned an invalid HttpsPort field" in client
        and "Sunshine returned an invalid appversion field" in client
        and "Sunshine returned an invalid numeric server field" not in client,
        "libgamestream/client.c: bounded server-field parsing must accept Sunshine's signed build sentinel and identify field failures",
    )
    require(
        "parse_unsigned_decimal_field(pairedText, 1u, &pairStatus)" in client
        and "server->paired = pairStatus == 1u;" in client
        and 'strcmp(pairedText, "1") == 0' not in client,
        "libgamestream/client.c: PairStatus must be exactly numeric 0 or 1 instead of treating malformed values as safely unpaired",
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
        "HOST_PROBE_SECURE_PAIRING_REQUIRED" in host_probe
        and "ret == GS_OK && probe.securePairingRequired" in host_probe
        and "== HOST_PROBE_PAIRED" in strict_connection_probe
        and "HOST_PROBE_SECURE_PAIRING_REQUIRED" in paired_device_connect
        and migration_flag >= 0
        and migration_flag < migration_save < migration_menu,
        "Vita UI: a saved pre-pin host must enter a durable one-time secure-pairing migration without weakening authenticated address checks",
    )
    require(
        "GS_IDENTITY_CHANGED -11" in error_codes
        and "return GS_IDENTITY_CHANGED;" in http_request_transport
        and re.search(
            r"http_request_with_timeout\(\s*url,\s*data,\s*"
            r"HTTP_TIMEOUT_ORDINARY_SECONDS\s*\)",
            load_serverinfo,
        )
        and "ret = GS_IO_ERROR" not in load_serverinfo
        and "HOST_PROBE_IDENTITY_CHANGED" in host_probe
        and "ret == GS_IDENTITY_CHANGED" in host_probe
        and "probe_result == HOST_PROBE_IDENTITY_CHANGED" in paired_device_connect
        and "display_identity_changed(info->name)" in paired_device_connect
        and "strstr(gs_error" not in connect,
        "Vita UI: a pinned certificate change must be a typed probe result that keeps the saved host visible and never relies on error-string matching",
    )
    require(
        "if (!info->paired)" in paired_device_connect
        and "(void)ui_connect_and_pairing(info);" in paired_device_connect
        and "This PC remains saved as Pairing required" in connect,
        "Vita UI: an interrupted or failed first pair must remain visible and directly retryable",
    )
    require(
        "SERVER_PIN_FILE_NAME" in http_header and "http_cleanup(void)" in http_header,
        "libgamestream/http.h: pin persistence/cleanup contract is incomplete",
    )

    forbidden_crypto_patterns = (
        r"#\s*include\s*[<\"]openssl/",
        r"\bOPENSSL_[A-Za-z0-9_]*",
        r"\bEVP_[A-Za-z0-9_]*",
        r"\bX509_[A-Za-z0-9_]*",
        r"\bRAND_bytes\b",
        r"\bBIO_(?:new|free|puts)\b",
    )
    migrated_sources = {
        "libgamestream/client.c": client,
        "libgamestream/http.c": http,
        "libgamestream/mkcert.c": mkcert,
        "libgamestream/mkcert.h": read("libgamestream/mkcert.h"),
        "src/main.c": main_source,
        "src/input/vita.c": vita_input,
    }
    for path, source in migrated_sources.items():
        require(
            not any(re.search(pattern, source) for pattern in forbidden_crypto_patterns),
            f"{path}: direct OpenSSL API/include remains after the mbedTLS migration",
        )
    require(
        "-lssl" not in root_cmake
        and "-lcrypto" not in root_cmake
        and "find_package(OpenSSL" not in standalone_cmake
        and "-lmbedtls" in root_cmake
        and "-lmbedx509" in root_cmake
        and "-lmbedcrypto" in root_cmake
        and "USE_MBEDTLS ON" in root_cmake
        and "target_compile_definitions(moonlight-common-c PRIVATE USE_MBEDTLS)"
        in root_cmake
        and "target_compile_definitions(moonlight-common PRIVATE USE_MBEDTLS)"
        in standalone_cmake,
        "CMake: Vita client, curl, and moonlight-common must use the mbedTLS backend exclusively",
    )
    require(
        "sceKernelGetRandomNumber(output, chunk)" in crypto
        and "outputLength > 64 ? 64u" in crypto
        and "mbedtls_sha1_update" in crypto
        and "mbedtls_sha256_update" in crypto
        and "mbedtls_aes_crypt_ecb" in crypto
        and "mbedtls_pk_sign" in crypto
        and "mbedtls_pk_verify" in crypto
        and "mbedtls_pk_check_pair" in crypto
        and "mbedtls_pk_write_pubkey_der" in crypto
        and "mbedtls_base64_encode" in crypto
        and "else if (segments[i].length != 0)" in crypto
        and "mbedtls_psa_crypto_free()" in crypto
        and "MBEDTLS_PRIVATE" not in crypto,
        "libgamestream/crypto.c: mbedTLS RNG/hash/AES/RSA/SPKI contract is incomplete or accesses private ABI",
    )
    require(
        "mbedtls_rsa_gen_key" in mkcert
        and "CLIENT_RSA_BITS 2048" in mkcert
        and "MBEDTLS_MD_SHA256" in mkcert
        and 'CLIENT_CERTIFICATE_NAME "CN=Vita Moonlight Client"' in mkcert
        and "mbedtls_x509write_crt_pem" in mkcert
        and "mbedtls_pk_write_key_pem" in mkcert,
        "libgamestream/mkcert.c: self-signed RSA/SHA-256 PEM identity generation is incomplete",
    )
    require(
        "unsigned char serial[16] = {0};" in mkcert
        and "if (!gs_crypto_random(serial, sizeof(serial)))" in mkcert
        and mkcert.find("if (!gs_crypto_random(serial, sizeof(serial)))")
        < mkcert.find("serial[0] &= 0x7f"),
        "libgamestream/mkcert.c: certificate serial must be initialized and normalized only after successful secure RNG",
    )
    require(
        "curlVersion->ssl_version" in http_init
        and 'strstr(curlVersion->ssl_version, "mbedTLS")' in http_init
        and "gs_crypto_spki_pin_from_pem" in http,
        "libgamestream/http.c: curl-mbedTLS runtime selection and SPKI pinning must fail closed",
    )
    require(
        "gs_crypto_init()" in main_source
        and "runtime_state.crypto_initialized = true" in main_source
        and "gs_crypto_cleanup()" in main_source,
        "src/main.c: mbedTLS pairing runtime must initialize and clean up explicitly",
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
        "delete this saved PC" in connect
        and "add it again" in connect
        and "Pair securely" in connect,
        "Vita UI: Sunshine identity mismatch recovery must give explicit delete/add/pair steps",
    )
    require(
        "if (server->currentGame != 0)" in pair
        and "Stop the running Sunshine application before pairing" in pair,
        "libgamestream/client.c: pairing must not begin while Sunshine is streaming an application",
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
