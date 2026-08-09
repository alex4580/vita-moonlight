#!/usr/bin/env python3
"""Guard Vita media callbacks and H.264 recovery invariants.

These checks intentionally cover contracts that the Vita cross-build cannot
prove: blocking renderers must not run on Moonlight's receive threads, the
one-reference-frame SPS rewrite must not be combined with RFI, and rewritten
access units must be capacity checked and submitted with their actual size.
"""

from __future__ import annotations

import re
import sys
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
errors: list[str] = []


def read(relative_path: str) -> str:
    path = ROOT / relative_path
    try:
        return path.read_text(encoding="utf-8-sig")
    except OSError as error:
        errors.append(f"{relative_path}: could not read file: {error}")
        return ""


def require(condition: bool, message: str) -> None:
    if not condition:
        errors.append(message)


def callback_body(source: str, name: str, relative_path: str) -> str:
    match = re.search(
        rf"\b{re.escape(name)}\s*=\s*\{{(?P<body>.*?)\n\}}\s*;",
        source,
        re.DOTALL,
    )
    if match is None:
        errors.append(f"{relative_path}: cannot find {name} initializer")
        return ""
    return match.group("body")


def main() -> int:
    audio = read("src/audio/vita.c")
    video = read("src/video/vita.c")
    motion = read("src/input/motion.c")
    motion_header = read("src/input/motion.h")
    config = read("src/config.c")
    connect = read("src/gui/ui_connect.c")
    sps_header = read("libgamestream/sps.h")
    sps_source = read("libgamestream/sps.c")

    audio_callback = callback_body(
        audio, "audio_callbacks_vita", "src/audio/vita.c"
    )
    video_callback = callback_body(
        video, "decoder_callbacks_vita", "src/video/vita.c"
    )

    require(
        "CAPABILITY_DIRECT_SUBMIT" not in audio_callback,
        "src/audio/vita.c: blocking audio callback must not use DIRECT_SUBMIT",
    )
    require(
        re.search(r"\.capabilities\s*=\s*0\s*,", audio_callback) is not None,
        "src/audio/vita.c: audio callback must use moonlight-common's queue",
    )
    require(
        "CAPABILITY_DIRECT_SUBMIT" not in video_callback,
        "src/video/vita.c: blocking video callback must not use DIRECT_SUBMIT",
    )
    require(
        "CAPABILITY_SLICES_PER_FRAME(2)" in video_callback,
        "src/video/vita.c: two-slice Vita encoder request was lost",
    )

    require(
        re.search(r"static\s+int\s+port\s*=\s*-1\s*;", audio) is not None,
        "src/audio/vita.c: audio port must begin in the closed state",
    )
    require(
        "sceAudioOutReleasePort(port)" in audio,
        "src/audio/vita.c: cleanup must release the Vita audio port",
    )
    require(
        re.search(r"decode_offset\s*=\s*0\s*;", audio) is not None,
        "src/audio/vita.c: cleanup must reset the decode accumulator",
    )
    require(
        re.search(
            r"static\s+uint32_t\s+active_audio_thread\s*=\s*1U\s*;",
            audio,
        )
        is not None
        and "atomic_load_u32(&active_audio_thread)" in audio
        and audio.count("atomic_store_u32(&active_audio_thread,") == 2,
        "src/audio/vita.c: audio worker enable state must use 32-bit atomics",
    )

    require(
        "bool vita_motion_end_stream(void);" in motion_header
        and "if (!vita_motion_end_stream())" in motion,
        "src/input/motion.c: reconnect must refuse to replace an unjoined worker",
    )
    require(
        motion.count("return false;") >= 2
        and "Preserve the ended worker's handle until deletion succeeds." in motion
        and "motion_thread = -1;" in motion,
        "src/input/motion.c: failed worker join/delete must retain ownership",
    )

    require(
        "video_status >= INIT_AVC_DEC && decoder != NULL" in video
        and "video_status >= INIT_AVC_LIB" in video
        and "if (decoderblock >= 0)" in video
        and "if (decoder_info != NULL)" in video
        and "if (frame_texture != NULL)" in video
        and "if (decoder_buffer != NULL)" in video
        and "video_status = NOT_INIT;" in video,
        "src/video/vita.c: cleanup must release every partially owned resource",
    )

    rfi_occurrences = connect.count(
        "CAPABILITY_REFERENCE_FRAME_INVALIDATION_AVC"
    )
    require(
        rfi_occurrences == 1
        and re.search(
            r"capabilities\s*&=\s*\n?\s*~CAPABILITY_REFERENCE_FRAME_INVALIDATION_AVC",
            connect,
        )
        is not None,
        "src/gui/ui_connect.c: Vita must unconditionally clear AVC RFI",
    )
    require(
        re.search(
            r"config->enable_ref_frame_invalidation\s*=\s*false\s*;",
            config,
        )
        is not None,
        "src/config.c: legacy RFI settings must sanitize to false",
    )
    require(
        "config.enable_ref_frame_invalidation = true" not in config,
        "src/config.c: a built-in preset still enables unsafe RFI",
    )

    require(
        "#define GS_SPS_MAX_REWRITTEN_SIZE" in sps_header,
        "libgamestream/sps.h: rewritten SPS bound is missing",
    )
    require(
        re.search(
            r"bool\s+gs_sps_fix\([^;]*size_t\s+out_capacity",
            sps_header,
            re.DOTALL,
        )
        is not None,
        "libgamestream/sps.h: SPS rewrite must receive output capacity",
    )
    require(
        "GS_SPS_MAX_REWRITTEN_SIZE > out_capacity - initial_offset"
        in sps_source,
        "libgamestream/sps.c: SPS rewrite capacity check is missing",
    )
    require(
        "if (rewritten_length <= 0 || rewritten_length > 128)" in sps_source,
        "libgamestream/sps.c: SPS writer result is not validated",
    )
    require(
        "char* resized_buffer = realloc" in video
        and "decoder_buffer = realloc" not in video,
        "src/video/vita.c: realloc must preserve the old buffer on failure",
    )
    require(
        re.search(r"au\.es\.size\s*=\s*length\s*;", video) is not None,
        "src/video/vita.c: decoder must receive rewritten access-unit length",
    )
    require(
        "memset(decoder_buffer + length, 0, AV_INPUT_BUFFER_PADDING_SIZE)"
        in video,
        "src/video/vita.c: decoder input padding must be zeroed",
    )
    require(
        "exit(1)" not in video,
        "src/video/vita.c: malformed/OOM stream input must not terminate the app",
    )

    if errors:
        print("Vita media contract check FAILED:", file=sys.stderr)
        for error in errors:
            print(f"- {error}", file=sys.stderr)
        return 1

    print("Vita media contract check passed.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
