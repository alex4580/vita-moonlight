#ifndef VIDEO_DIAGNOSTICS_H
#define VIDEO_DIAGNOSTICS_H

#include <stdbool.h>
#include <stdint.h>

/*
 * Thread-safe aggregate diagnostics for the Moonlight video receive and
 * recovery path. The implementation copies atomic counters into caller-owned
 * storage, so consumers never dereference mutable transport state.
 *
 * Counters wrap naturally at UINT32_MAX. Unsigned subtraction yields a valid
 * interval delta as long as no single sampling interval spans a complete wrap.
 */
typedef struct VideoStreamDiagnostics {
  uint32_t rtpPacketsOutOfSequence;
  uint32_t fecDataPacketsRecovered;
  uint32_t fecBlocksFailed;
  uint32_t networkFramesLost;
  uint32_t depacketizerCorruptFrames;
  uint32_t decodeUnitQueueOverflows;
  uint32_t idrRequestsSent;
} VideoStreamDiagnostics;

bool LiGetVideoStreamDiagnosticsSnapshot(
    VideoStreamDiagnostics *snapshot);

#endif
