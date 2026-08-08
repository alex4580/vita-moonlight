#!/usr/bin/env python3
"""Guard the Vita stream reliability backport and low-overhead diagnostics."""

from __future__ import annotations

import importlib.util
import shutil
import sys
import tempfile
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
SOURCE_DIR = ROOT / "third_party" / "moonlight-common-c"
PATCH = ROOT / "patches" / "moonlight-common-c" / "07c32c8-stream-reliability.patch"
PREPARE = ROOT / "tools" / "prepare-moonlight-common-stream-reliability.py"
errors: list[str] = []


def require(condition: bool, message: str) -> None:
    if not condition:
        errors.append(message)


def read(relative: str) -> str:
    try:
        return (ROOT / relative).read_text(encoding="utf-8-sig")
    except OSError as exc:
        errors.append(f"{relative}: could not read file: {exc}")
        return ""


def body(source: str, start: str, end: str) -> str:
    if start not in source:
        return ""
    result = source.split(start, 1)[1]
    return result.split(end, 1)[0] if end in result else result


def load_prepare_module():
    spec = importlib.util.spec_from_file_location("stream_backport", PREPARE)
    if spec is None or spec.loader is None:
        raise RuntimeError("could not load stream backport preparation tool")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def main() -> int:
    try:
        prepare = load_prepare_module()
        outputs = prepare.apply_verified_patch(SOURCE_DIR, PATCH)
    except Exception as exc:  # contract failure should report one concise cause
        errors.append(f"backport did not verify against the pinned source: {exc}")
        outputs = {}

    generated = {
        path: data.decode("utf-8") for path, data in outputs.items()
    }
    rtp = generated.get("src/RtpVideoQueue.c", "")
    depacketizer = generated.get("src/VideoDepacketizer.c", "")
    stream = generated.get("src/VideoStream.c", "")
    control = generated.get("src/ControlStream.c", "")
    internal_header = generated.get("src/Limelight-internal.h", "")
    public_header = read("libgamestream/video_diagnostics.h")

    require(
        prepare.UPSTREAM_COMMIT == "07c32c80f98bb0d7214c577bd080eea3ce64a856"
        and len(outputs) == 5,
        "stream backport must remain pinned to the reviewed five-file upstream base",
    )
    require(
        "VideoStreamDiagnostics" in public_header
        and "LiGetVideoStreamDiagnosticsSnapshot" in public_header
        and "VIDEO_DIAGNOSTIC_COUNTER" in internal_header
        and '#include "video_diagnostics.h"' in internal_header,
        "moonlight-common-c must expose a typed caller-owned diagnostics snapshot",
    )
    require(
        "__atomic_fetch_add(destination, amount, __ATOMIC_RELAXED)" in stream
        and stream.count("__atomic_load_n(") == 7
        and "return false;" in body(
            stream,
            "bool LiGetVideoStreamDiagnosticsSnapshot(",
            "\n}",
        ),
        "video counters and the snapshot API must be null-safe and atomic",
    )
    require(
        "if (!isFecRecovery)" in rtp
        and "VIDEO_DIAGNOSTIC_RTP_PACKET_OUT_OF_SEQUENCE" in rtp,
        "only accepted non-synthetic RTP packets may increment OOS",
    )
    recovery_guard = rtp.find("if (ret == 0 && recoveredDataPackets != 0)")
    sanity_checks_end = rtp.find("cleanup:", recovery_guard)
    require(
        recovery_guard >= 0
        and sanity_checks_end > recovery_guard
        and "VIDEO_DIAGNOSTIC_FEC_DATA_PACKET_RECOVERED"
        in rtp[recovery_guard:sanity_checks_end],
        "FEC data packets must be counted only after final reconstruction validation",
    )
    abandoned = rtp.find("This nonempty block is about to be abandoned")
    report = rtp.find("reportFinalFrameFecStatus(queue);", abandoned)
    require(
        abandoned >= 0
        and report > abandoned
        and rtp[abandoned:report].count("VIDEO_DIAGNOSTIC_FEC_BLOCK_FAILED") == 1,
        "each abandoned nonempty FEC block must be counted exactly once",
    )
    require(
        rtp.count("VIDEO_DIAGNOSTIC_NETWORK_FRAME_LOST") == 3
        and "VIDEO_DIAGNOSTIC_DEPACKETIZER_CORRUPT_FRAME" in depacketizer
        and "VIDEO_DIAGNOSTIC_DECODE_UNIT_QUEUE_OVERFLOW" in depacketizer
        and "VIDEO_DIAGNOSTIC_IDR_REQUEST_SENT" in control,
        "lost, corrupt, overflow, and successfully sent IDR events need exact aggregate hooks",
    )

    diagnostics = read("src/gui/ui_diagnostics.c")
    diagnostics_header = read("src/gui/ui_diagnostics.h")
    video = read("src/video/vita.c")
    scaling = read("src/video/scaling.c")
    scaling_header = read("src/video/scaling.h")
    sps = read("libgamestream/sps.c")
    motion = read("src/input/motion.c")
    motion_header = read("src/input/motion.h")
    vita_input = read("src/input/vita.c")
    vita_main = read("src/main.c")
    connection = read("src/connection.c")
    cmake = read("CMakeLists.txt")
    vita_ci = read(".github/workflows/cmake-psvita.yml")
    require(
        "LiGetVideoStreamDiagnosticsSnapshot" in diagnostics
        and "LiGetRTPVideoStats" not in diagnostics
        and "fec_failed_blocks" in diagnostics
        and "network_lost_frames" in diagnostics
        and "depacketizer_corrupt_frames" in diagnostics
        and "decode_queue_overflows" in diagnostics
        and "idr_requests_sent" in diagnostics,
        "Vita diagnostics and support logs must use precise aggregate labels",
    )
    require(
        "void ui_diagnostics_tick(uint64_t now_us)" in diagnostics
        and "void ui_diagnostics_tick(uint64_t now_us);" in diagnostics_header
        and "ui_diagnostics_tick(now);" in video,
        "the one-second diagnostics window must advance during a total video freeze",
    )
    bitstream_fix = body(
        sps,
        "if ((flags & GS_SPS_BITSTREAM_FIXUP) == GS_SPS_BITSTREAM_FIXUP) {",
        "\n  }\n\n  memcpy",
    )
    restriction_end = bitstream_fix.find(
        "h264_stream->sps->vui.num_reorder_frames = 0;"
    )
    require(
        restriction_end > bitstream_fix.find(
            "h264_stream->sps->vui.bitstream_restriction_flag = 1;"
        )
        and restriction_end < bitstream_fix.find(
            "h264_stream->sps->vui.max_dec_frame_buffering = 1;"
        ),
        "SPS fixup must always force zero reorder frames with a one-frame buffer",
    )
    begin_motion = body(
        motion, "bool vita_motion_begin_stream(bool allow_motion) {",
        "bool vita_motion_end_stream(void)",
    )
    set_motion = body(
        motion, "bool vita_motion_set_state(uint8_t motion_type, uint16_t report_rate) {",
        "void vita_motion_get_status(",
    )
    require(
        "sceMotionStartSampling" not in begin_motion
        and "sampling=deferred" in begin_motion
        and "report_rate != 0" in set_motion
        and "sceMotionStartSampling" in set_motion,
        "Vita motion hardware must remain idle until Sunshine requests a sensor",
    )
    require(
        "!motion_state.motion_type_gyro_enabled" in set_motion
        and "!motion_state.motion_type_accel_enabled" in set_motion
        and "sceMotionStopSampling" in set_motion
        and "return actually_enabled;" in set_motion
        and "bool vita_motion_set_state" in motion_header
        and "bool motion_enabled = vita_motion_set_state" in connection
        and "if (report_rate != 0 &&" in connection,
        "motion sampling must stop when idle and report actual startup state",
    )

    input_poll = body(
        vita_input, "inline void vitainput_process(void) {", "int vitainput_thread(",
    )
    input_init = body(vita_input, "bool vitainput_init() {", "bool vitainput_shutdown(")
    require(
        "sceCtrlSetSamplingModeExt" not in input_poll
        and "sceCtrlSetSamplingModeExt(SCE_CTRL_MODE_ANALOG_WIDE)" in input_init
        and vita_input.count("sceCtrlSetSamplingModeExt(") == 1,
        "controller sampling mode must be configured once, not every 2 ms poll",
    )

    require(
        "#define VITA_NET_MEM_SIZE (4 * 1024 * 1024)" in vita_main
        and "2,129,920-byte video receive buffer" in vita_main,
        "the Vita network pool must fit moonlight-common's requested video "
        "receive buffer plus audio/control headroom",
    )

    require(
        "VitaScalingSettings" in scaling_header
        and "bool vita_scaling_calculate" in scaling
        and "src/video/scaling.c" in cmake
        and "vita2d_draw_texture_part_scale" in video
        and "image_scaling.source_width" in video
        and "VITA_DECODER_RESOLUTION((scaled_" not in video,
        "decoder alignment and bounded Vita2D fit/crop geometry must remain separate",
    )

    show_poor_network = body(
        video, "void vitavideo_show_poor_net_indicator() {",
        "void vitavideo_hide_poor_net_indicator() {",
    )
    hide_poor_network = body(
        video, "void vitavideo_hide_poor_net_indicator() {",
        "int vitavideo_initialized() {",
    )
    require(
        "atomic_load_u32(&poor_net_indicator_requested) != 0" in video
        and "vitavideo_request_redraw();" in show_poor_network
        and "vitavideo_request_redraw();" in hide_poor_network,
        "poor-network indicator transitions must redraw even during a frozen stream",
    )

    require(
        "tests/video_scaling_test.c src/video/scaling.c" in vita_ci
        and "tests/sps_reorder_test.c" in vita_ci
        and '"$RUNNER_TEMP/vita-video-scaling-test"' in vita_ci
        and '"$RUNNER_TEMP/vita-sps-reorder-test"' in vita_ci,
        "Vita CI must execute native geometry and SPS latency regressions",
    )

    # Fail-closed reproducibility checks: neither a changed patch nor a changed
    # pinned source may be accepted by the preparation tool.
    if outputs:
        with tempfile.TemporaryDirectory(prefix="vita-stream-contract-") as temp:
            temp_path = Path(temp)
            bad_patch = temp_path / "changed.patch"
            bad_patch.write_bytes(PATCH.read_bytes() + b"\n")
            try:
                prepare.apply_verified_patch(SOURCE_DIR, bad_patch)
            except prepare.BackportError:
                pass
            else:
                errors.append("stream backport accepted a changed patch")

            changed_source = temp_path / "source"
            first_path = next(iter(prepare.EXPECTED_FILES))
            for path in prepare.EXPECTED_FILES:
                destination = changed_source / path
                destination.parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(SOURCE_DIR / path, destination)
            with (changed_source / first_path).open("ab") as output:
                output.write(b"\n")
            try:
                prepare.apply_verified_patch(changed_source, PATCH)
            except prepare.BackportError:
                pass
            else:
                errors.append("stream backport accepted a changed base source")

    if errors:
        print("Vita stream reliability contract FAILED:", file=sys.stderr)
        for error in errors:
            print(f"- {error}", file=sys.stderr)
        return 1
    print("Vita stream reliability contract passed.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
