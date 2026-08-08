add_library(enet STATIC
    enet/callbacks.c
    enet/compress.c
    enet/host.c
    enet/list.c
    enet/packet.c
    enet/peer.c
    enet/protocol.c
    enet/unix.c
)

target_include_directories(enet PUBLIC enet/include)

add_library(h264bitstream STATIC
    h264bitstream/h264_nal.c
    h264bitstream/h264_sei.c
    h264bitstream/h264_stream.c
)
target_include_directories(h264bitstream PUBLIC h264bitstream)

add_library(inih STATIC
    inih/ini.c
)
target_include_directories(inih PUBLIC inih)

add_library(libuuid STATIC
    libuuid/src/clear.c
    libuuid/src/compare.c
    libuuid/src/copy.c
    libuuid/src/gen_uuid.c
    libuuid/src/isnull.c
    libuuid/src/pack.c
    libuuid/src/parse.c
    libuuid/src/unpack.c
    libuuid/src/unparse.c
    libuuid/src/uuid_time.c
)
target_include_directories(libuuid PUBLIC libuuid/src)

find_package(PythonInterp 3 REQUIRED)

set(MOONLIGHT_RTSP_BASE_SOURCE
    "${CMAKE_CURRENT_SOURCE_DIR}/moonlight-common-c/src/RtspConnection.c"
)
set(MOONLIGHT_RTSP_BACKPORT_PATCH
    "${CMAKE_SOURCE_DIR}/patches/moonlight-common-c/7b026e7-rtsp-hardening.patch"
)
set(MOONLIGHT_RTSP_BACKPORT_TOOL
    "${CMAKE_SOURCE_DIR}/tools/prepare-moonlight-common-rtsp.py"
)
set(MOONLIGHT_RTSP_GENERATED_SOURCE
    "${CMAKE_CURRENT_BINARY_DIR}/generated/moonlight-common-c/RtspConnection.c"
)

add_custom_command(
    OUTPUT "${MOONLIGHT_RTSP_GENERATED_SOURCE}"
    COMMAND "${CMAKE_COMMAND}" -E make_directory
            "${CMAKE_CURRENT_BINARY_DIR}/generated/moonlight-common-c"
    COMMAND "${PYTHON_EXECUTABLE}" "${MOONLIGHT_RTSP_BACKPORT_TOOL}"
            --source "${MOONLIGHT_RTSP_BASE_SOURCE}"
            --patch "${MOONLIGHT_RTSP_BACKPORT_PATCH}"
            --output "${MOONLIGHT_RTSP_GENERATED_SOURCE}"
    DEPENDS
        "${MOONLIGHT_RTSP_BASE_SOURCE}"
        "${MOONLIGHT_RTSP_BACKPORT_PATCH}"
        "${MOONLIGHT_RTSP_BACKPORT_TOOL}"
    COMMENT "Preparing verified moonlight-common-c RTSP hardening backport"
    VERBATIM
)
set_source_files_properties("${MOONLIGHT_RTSP_GENERATED_SOURCE}" PROPERTIES GENERATED TRUE)

# This target intentionally runs on every moonlight-common-c build. It prevents
# an old generated file from bypassing hash validation after a submodule drift.
add_custom_target(verify-moonlight-common-rtsp-backport
    COMMAND "${PYTHON_EXECUTABLE}" "${MOONLIGHT_RTSP_BACKPORT_TOOL}"
            --source "${MOONLIGHT_RTSP_BASE_SOURCE}"
            --patch "${MOONLIGHT_RTSP_BACKPORT_PATCH}"
            --check
    DEPENDS
        "${MOONLIGHT_RTSP_BASE_SOURCE}"
        "${MOONLIGHT_RTSP_BACKPORT_PATCH}"
        "${MOONLIGHT_RTSP_BACKPORT_TOOL}"
    COMMENT "Verifying moonlight-common-c RTSP hardening inputs"
    VERBATIM
)

set(MOONLIGHT_STREAM_SOURCE_DIR
    "${CMAKE_CURRENT_SOURCE_DIR}/moonlight-common-c"
)
set(MOONLIGHT_STREAM_RELIABILITY_PATCH
    "${CMAKE_SOURCE_DIR}/patches/moonlight-common-c/07c32c8-stream-reliability.patch"
)
set(MOONLIGHT_STREAM_RELIABILITY_TOOL
    "${CMAKE_SOURCE_DIR}/tools/prepare-moonlight-common-stream-reliability.py"
)
set(MOONLIGHT_STREAM_GENERATED_ROOT
    "${CMAKE_CURRENT_BINARY_DIR}/generated/moonlight-common-stream"
)
set(MOONLIGHT_STREAM_GENERATED_SOURCES
    "${MOONLIGHT_STREAM_GENERATED_ROOT}/src/ControlStream.c"
    "${MOONLIGHT_STREAM_GENERATED_ROOT}/src/RtpVideoQueue.c"
    "${MOONLIGHT_STREAM_GENERATED_ROOT}/src/VideoDepacketizer.c"
    "${MOONLIGHT_STREAM_GENERATED_ROOT}/src/VideoStream.c"
)
set(MOONLIGHT_STREAM_GENERATED_HEADER
    "${MOONLIGHT_STREAM_GENERATED_ROOT}/src/Limelight-internal.h"
)

add_custom_command(
    OUTPUT
        ${MOONLIGHT_STREAM_GENERATED_SOURCES}
        "${MOONLIGHT_STREAM_GENERATED_HEADER}"
    COMMAND "${CMAKE_COMMAND}" -E make_directory
            "${MOONLIGHT_STREAM_GENERATED_ROOT}"
    COMMAND "${PYTHON_EXECUTABLE}" "${MOONLIGHT_STREAM_RELIABILITY_TOOL}"
            --source-dir "${MOONLIGHT_STREAM_SOURCE_DIR}"
            --patch "${MOONLIGHT_STREAM_RELIABILITY_PATCH}"
            --output-dir "${MOONLIGHT_STREAM_GENERATED_ROOT}"
    DEPENDS
        "${MOONLIGHT_STREAM_SOURCE_DIR}/src/ControlStream.c"
        "${MOONLIGHT_STREAM_SOURCE_DIR}/src/Limelight-internal.h"
        "${MOONLIGHT_STREAM_SOURCE_DIR}/src/RtpVideoQueue.c"
        "${MOONLIGHT_STREAM_SOURCE_DIR}/src/VideoDepacketizer.c"
        "${MOONLIGHT_STREAM_SOURCE_DIR}/src/VideoStream.c"
        "${MOONLIGHT_STREAM_RELIABILITY_PATCH}"
        "${MOONLIGHT_STREAM_RELIABILITY_TOOL}"
    COMMENT "Preparing verified moonlight-common-c stream-reliability backport"
    VERBATIM
)
set_source_files_properties(
    ${MOONLIGHT_STREAM_GENERATED_SOURCES}
    "${MOONLIGHT_STREAM_GENERATED_HEADER}"
    PROPERTIES GENERATED TRUE
)

# This verification is intentionally always run. A cached generated source
# must never conceal a changed submodule checkout or an unreviewed patch.
add_custom_target(verify-moonlight-common-stream-reliability
    COMMAND "${PYTHON_EXECUTABLE}" "${MOONLIGHT_STREAM_RELIABILITY_TOOL}"
            --source-dir "${MOONLIGHT_STREAM_SOURCE_DIR}"
            --patch "${MOONLIGHT_STREAM_RELIABILITY_PATCH}"
            --check
    DEPENDS
        "${MOONLIGHT_STREAM_SOURCE_DIR}/src/ControlStream.c"
        "${MOONLIGHT_STREAM_SOURCE_DIR}/src/Limelight-internal.h"
        "${MOONLIGHT_STREAM_SOURCE_DIR}/src/RtpVideoQueue.c"
        "${MOONLIGHT_STREAM_SOURCE_DIR}/src/VideoDepacketizer.c"
        "${MOONLIGHT_STREAM_SOURCE_DIR}/src/VideoStream.c"
        "${MOONLIGHT_STREAM_RELIABILITY_PATCH}"
        "${MOONLIGHT_STREAM_RELIABILITY_TOOL}"
    COMMENT "Verifying moonlight-common-c stream-reliability inputs"
    VERBATIM
)

add_library(moonlight-common-c STATIC
    moonlight-common-c/src/AudioStream.c
    moonlight-common-c/src/ByteBuffer.c
    moonlight-common-c/src/Connection.c
    moonlight-common-c/src/ConnectionTester.c
    "${MOONLIGHT_STREAM_GENERATED_ROOT}/src/ControlStream.c"
    moonlight-common-c/src/FakeCallbacks.c
    moonlight-common-c/src/InputStream.c
    moonlight-common-c/src/LinkedBlockingQueue.c
    moonlight-common-c/src/Misc.c
    moonlight-common-c/src/Platform.c
    moonlight-common-c/src/PlatformCrypto.c
    moonlight-common-c/src/PlatformSockets.c
    moonlight-common-c/src/RecorderCallbacks.c
    moonlight-common-c/src/RtpAudioQueue.c
    "${MOONLIGHT_STREAM_GENERATED_ROOT}/src/RtpVideoQueue.c"
    "${MOONLIGHT_RTSP_GENERATED_SOURCE}"
    moonlight-common-c/src/RtspParser.c
    moonlight-common-c/src/SdpGenerator.c
    moonlight-common-c/src/SimpleStun.c
    "${MOONLIGHT_STREAM_GENERATED_ROOT}/src/VideoDepacketizer.c"
    "${MOONLIGHT_STREAM_GENERATED_ROOT}/src/VideoStream.c"
    moonlight-common-c/reedsolomon/rs.c
)
target_include_directories(moonlight-common-c BEFORE PRIVATE
    "${MOONLIGHT_STREAM_GENERATED_ROOT}/src"
)
target_include_directories(moonlight-common-c
    PUBLIC moonlight-common-c/src moonlight-common-c/reedsolomon
    PRIVATE "${CMAKE_SOURCE_DIR}/libgamestream"
)
target_compile_definitions(moonlight-common-c PRIVATE _POSIX_C_SOURCE=200809L)
add_dependencies(moonlight-common-c verify-moonlight-common-rtsp-backport)
add_dependencies(moonlight-common-c verify-moonlight-common-stream-reliability)

add_library(mdnsniff STATIC
    mdnsniff/mdns_parser.c
    mdnsniff/udp_sniffer_vita.c
)
target_include_directories(mdnsniff PUBLIC mdnsniff)
