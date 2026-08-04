/*
 * This file is part of Moonlight Embedded.
 *
 * Copyright (C) 2016 Ilya Zhuravlev
 *
 * Moonlight is free software; you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation; either version 3 of the License, or
 * (at your option) any later version.
 *
 * Moonlight is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with Moonlight; if not, see <http://www.gnu.org/licenses/>.
 */

#include "../audio.h"
#include "../debug.h"

#include <stdio.h>
#include <stdint.h>
#include <string.h>
#include <opus/opus_multistream.h>
#include <psp2/audioout.h>

enum {
  VITA_AUDIO_INIT_OK        = 0,
  VITA_AUDIO_ERROR_BAD_OPUS = 0x80020001,
  VITA_AUDIO_ERROR_PORT     = 0x80020002,
};

#define FRAME_SIZE 240
#define VITA_SAMPLES 960
#define BUFFER_SIZE (2 * VITA_SAMPLES)
static int decode_offset = 0;
static int port = -1;

/* This flag is written by the UI thread and read by moonlight-common's
 * queued audio worker. Publish it atomically so pause/resume cannot race. */
static uint32_t active_audio_thread = 1U;
static OpusMSDecoder* decoder = NULL;

static short buffer[BUFFER_SIZE];

static uint32_t atomic_load_u32(const uint32_t *value) {
  return __atomic_load_n(value, __ATOMIC_ACQUIRE);
}

static void atomic_store_u32(uint32_t *value, uint32_t next) {
  __atomic_store_n(value, next, __ATOMIC_RELEASE);
}

static void vita_renderer_cleanup() {
  /* Cleanup may follow a partial init or be called more than once. Keep the
   * Vita audio port and Opus decoder under a single idempotent lifecycle. */
  if (port >= 0) {
    int release_result = sceAudioOutReleasePort(port);
    if (release_result < 0) {
      vita_debug_log("Failed to release audio port 0x%x: 0x%x\n",
                     port, release_result);
    }
  }
  port = -1;

  if (decoder != NULL) {
    opus_multistream_decoder_destroy(decoder);
    decoder = NULL;
  }

  decode_offset = 0;
  memset(buffer, 0, sizeof(buffer));
}

static int vita_renderer_init(int audioConfiguration, POPUS_MULTISTREAM_CONFIGURATION opusConfig, void* audioContext, int arFlags) {
  (void)audioConfiguration;
  (void)audioContext;
  (void)arFlags;

  /* Recover cleanly if Moonlight retries initialization after a partial or
   * interrupted session. */
  vita_renderer_cleanup();

  int rc;
  decoder = opus_multistream_decoder_create(opusConfig->sampleRate,
                                            opusConfig->channelCount,
                                            opusConfig->streams,
                                            opusConfig->coupledStreams,
                                            opusConfig->mapping,
                                            &rc);

  if (rc < 0 || decoder == NULL) {
      if (decoder != NULL) {
        opus_multistream_decoder_destroy(decoder);
        decoder = NULL;
      }
      return VITA_AUDIO_ERROR_BAD_OPUS;
  }

  port = sceAudioOutOpenPort(SCE_AUDIO_OUT_PORT_TYPE_MAIN, VITA_SAMPLES, 48000, SCE_AUDIO_OUT_MODE_STEREO);

  if (port < 0) {
      vita_renderer_cleanup();
      return VITA_AUDIO_ERROR_PORT;
  }

  vita_debug_log("open port 0x%x\n", port);
  return VITA_AUDIO_INIT_OK;
}

static void vita_renderer_decode_and_play_sample(char* data, int length) {
  if (!data || length <= 0 || decoder == NULL || port < 0)
    return;

  int decodeLen = opus_multistream_decode(decoder, data, length, buffer + 2 * decode_offset, FRAME_SIZE, 0);
  if (decodeLen > 0) {
    if (decodeLen != FRAME_SIZE)
      return;
    decode_offset += decodeLen;

    if (decode_offset == VITA_SAMPLES) {
      decode_offset = 0;
      if (atomic_load_u32(&active_audio_thread)) {
        sceAudioOutOutput(port, buffer);
      }
    }
  } else {
    vita_debug_log("Opus error from decode: %d\n", decodeLen);
  }
}

AUDIO_RENDERER_CALLBACKS audio_callbacks_vita = {
  .init = vita_renderer_init,
  .cleanup = vita_renderer_cleanup,
  .decodeAndPlaySample = vita_renderer_decode_and_play_sample,
  /* Opus decode and sceAudioOutOutput() may block. Let moonlight-common use
   * its audio renderer queue instead of stalling the network receive path. */
  .capabilities = 0,
};


void vitaaudio_start() {
  atomic_store_u32(&active_audio_thread, 1U);
}

void vitaaudio_stop() {
  atomic_store_u32(&active_audio_thread, 0U);
}
