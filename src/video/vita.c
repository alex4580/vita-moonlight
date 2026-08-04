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

#include "../video.h"
#include "../config.h"
#include "../debug.h"
#include "../gui/guilib.h"
#include "../gui/ui_diagnostics.h"
#include "../gui/ui_stream_overlay.h"
#include "../util.h"
#include "../input/vita.h"
#include "vita.h"
#include "sps.h"

#include <Limelight.h>

#include <pthread.h>
#include <stdbool.h>
#include <stdint.h>
#include <psp2/kernel/sysmem.h>
#include <psp2/kernel/threadmgr.h>
#include <psp2/display.h>
#include <psp2/videodec.h>
#include <vita2d.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>


#define printf vita_debug_log

extern void gs_sps_stop();

extern double_click_tracker dc_tracker;

void draw_streaming(vita2d_texture *frame_texture);
void draw_indicators();

enum {
  VITA_VIDEO_INIT_OK                    = 0,
  VITA_VIDEO_ERROR_NO_MEM               = 0x80010001,
  VITA_VIDEO_ERROR_INIT_LIB             = 0x80010002,
  VITA_VIDEO_ERROR_QUERY_DEC_MEMSIZE    = 0x80010003,
  VITA_VIDEO_ERROR_ALLOC_MEM            = 0x80010004,
  VITA_VIDEO_ERROR_GET_MEMBASE          = 0x80010005,
  VITA_VIDEO_ERROR_CREATE_DEC           = 0x80010006,
  VITA_VIDEO_ERROR_CREATE_PACER_THREAD  = 0x80010007,
  VITA_VIDEO_ERROR_CREATE_RENDER_MUTEX  = 0x80010008,
  VITA_VIDEO_ERROR_CLEANUP_BLOCKED      = 0x80010009,
};

#define DECODER_BUFFER_SIZE (128 * 1024)

#define AV_INPUT_BUFFER_PADDING_SIZE 64

#define PACER_SAMPLE_INTERVAL_US 1000000ULL
#define UI_WATCHDOG_POLL_US 16000
#define UI_WATCHDOG_IDLE_POLL_US 50000
#define UI_WATCHDOG_STALE_US 75000ULL
#define UI_WATCHDOG_REFRESH_US 100000ULL
#define UI_REQUEST_MIN_INTERVAL_US 16000ULL
#define VIDEO_CLEANUP_WAIT_US 2000000
#define VIDEO_CLEANUP_LOCK_POLL_US 1000
#define DECODER_ERROR_LOG_INTERVAL_US 10000000ULL

static char* decoder_buffer = NULL;

static size_t decoder_buffer_size = 0;

static bool decoder_buffer_requirement(PDECODE_UNIT decode_unit,
                                       size_t* payload_size,
                                       size_t* allocation_size) {
  if (decode_unit == NULL || decode_unit->bufferList == NULL ||
      decode_unit->fullLength <= 0 || payload_size == NULL ||
      allocation_size == NULL) {
    return false;
  }

  size_t source_size = 0;
  size_t required_size = 0;
  size_t entry_count = 0;
  for (PLENTRY entry = decode_unit->bufferList;
       entry != NULL; entry = entry->next) {
    /* Each valid entry contains at least one source byte, so this also bounds
     * traversal if a malformed list contains a cycle. */
    entry_count++;
    if (entry_count > (size_t)decode_unit->fullLength ||
        entry->data == NULL || entry->length <= 0) {
      return false;
    }

    size_t entry_source_size = (size_t)entry->length;
    if (source_size > SIZE_MAX - entry_source_size) return false;
    source_size += entry_source_size;

    size_t entry_output_size = entry->bufferType == BUFFER_TYPE_SPS
        ? (size_t)GS_SPS_MAX_REWRITTEN_SIZE
        : entry_source_size;
    if (required_size > SIZE_MAX - entry_output_size) return false;
    required_size += entry_output_size;
  }

  if (source_size != (size_t)decode_unit->fullLength ||
      required_size > UINT32_MAX ||
      required_size > SIZE_MAX - AV_INPUT_BUFFER_PADDING_SIZE) {
    return false;
  }

  *payload_size = required_size;
  *allocation_size = required_size + AV_INPUT_BUFFER_PADDING_SIZE;
  return true;
}

enum {
  SCREEN_WIDTH = 960,
  SCREEN_HEIGHT = 544,
  LINE_SIZE = 960,
  FRAMEBUFFER_SIZE = 2 * 1024 * 1024,
  FRAMEBUFFER_ALIGNMENT = 256 * 1024
};

enum VideoStatus {
  NOT_INIT,
  INIT_GS,
  INIT_FRAMEBUFFER,
  INIT_AVC_LIB,
  INIT_DECODER_MEMBLOCK,
  INIT_AVC_DEC,
  INIT_FRAME_PACER_THREAD,
};

vita2d_texture *frame_texture = NULL;
enum VideoStatus video_status = NOT_INIT;

SceAvcdecCtrl *decoder = NULL;
SceUID displayblock = -1;
SceUID decoderblock = -1;
SceUID pacer_thread = -1;
SceVideodecQueryInitInfoHwAvcdec *init = NULL;
SceAvcdecQueryDecoderInfo *decoder_info = NULL;

typedef struct {
  uint8_t alpha;
  bool plus;
} indicator_status;

static indicator_status poor_net_indicator = {0};
static uint32_t poor_net_indicator_requested = 0;
/*
 * The hardware decoder writes directly into frame_texture. This mutex covers
 * that write and every Vita2D draw so the watchdog can only reuse a fully
 * decoded texture and can never overlap the normal presentation path.
 */
static pthread_mutex_t video_render_mutex;
static uint32_t video_render_mutex_initialized = 0;
static uint32_t active_video_thread = 0;
static uint32_t active_pacer_thread = 0;
static uint32_t video_cleanup_blocked = 0;
static uint32_t redraw_request_generation = 0;
static uint32_t rendered_redraw_generation = 0;
static bool decoded_frame_available = false;
static uint64_t last_video_activity_us = 0;
static uint64_t last_render_us = 0;
static uint64_t last_decoder_error_log_us = 0;
static uint32_t suppressed_decoder_errors = 0;

static uint32_t frame_count = 0;
static uint32_t need_drop = 0;
static uint32_t fps_snapshot = 0;
float carry = 0;

static uint32_t atomic_load_u32(const uint32_t *value) {
  return __atomic_load_n(value, __ATOMIC_ACQUIRE);
}

static void atomic_store_u32(uint32_t *value, uint32_t next) {
  __atomic_store_n(value, next, __ATOMIC_RELEASE);
}

static uint32_t atomic_exchange_u32(uint32_t *value, uint32_t next) {
  return __atomic_exchange_n(value, next, __ATOMIC_ACQ_REL);
}

static void atomic_add_u32(uint32_t *value, uint32_t amount) {
  __atomic_fetch_add(value, amount, __ATOMIC_ACQ_REL);
}

static void atomic_sub_u32(uint32_t *value, uint32_t amount) {
  __atomic_fetch_sub(value, amount, __ATOMIC_ACQ_REL);
}

static void atomic_store_fps(uint32_t rendered, uint32_t target) {
  /* One 32-bit publication keeps the rendered/target pair self-consistent. */
  uint32_t packed = (rendered & 0xffffU) | ((target & 0xffffU) << 16);
  atomic_store_u32(&fps_snapshot, packed);
}

typedef struct {
  unsigned int texture_width;
  unsigned int texture_height;
  float origin_x;
  float origin_y;
  float region_x1;
  float region_y1;
  float region_x2;
  float region_y2;
} image_scaling_settings;

static image_scaling_settings image_scaling = {0};

static void draw_stream_surface(bool count_video_frame) {
  uint32_t request_generation =
      atomic_load_u32(&redraw_request_generation);
  vita2d_start_drawing();

  if (decoded_frame_available && frame_texture != NULL) {
    draw_streaming(frame_texture);
  } else {
    vita2d_clear_screen();
  }
  draw_indicators();
  if (!stream_overlay_is_open()) {
    ui_diagnostics_draw_overlay();
  }
  stream_overlay_draw();

  vita2d_end_drawing();
  vita2d_wait_rendering_done();
  vita2d_swap_buffers();

  last_render_us = sceKernelGetSystemTimeWide();
  atomic_store_u32(&rendered_redraw_generation, request_generation);
  if (count_video_frame) {
    atomic_add_u32(&frame_count, 1);
  }
}

void update_scaling_settings(int width, int height) {
  image_scaling.texture_width = SCREEN_WIDTH;
  image_scaling.texture_height = SCREEN_HEIGHT;
  image_scaling.origin_x = 0;
  image_scaling.origin_y = 0;
  image_scaling.region_x1 = 0;
  image_scaling.region_y1 = 0;
  image_scaling.region_x2 = image_scaling.texture_width;
  image_scaling.region_y2 = image_scaling.texture_height;

  double scaled_width = (double) SCREEN_HEIGHT * width / height;
  double scaled_height = (double) SCREEN_WIDTH * height / width;

  if (SCREEN_WIDTH * height == SCREEN_HEIGHT * width) {
    // streaming resolution ratio matches Vita's screen ratio
    // use default setting
  } else if (SCREEN_WIDTH * height > SCREEN_HEIGHT * width) {
    // host ratio example: 4:3, 16:10
    // Vita ratio range: 2:16 (64 x 544) - native (960 x 544)
    if (config.center_region_only) {
      image_scaling.texture_height = VITA_DECODER_RESOLUTION(scaled_height);
      image_scaling.region_y1 = VITA_DECODER_RESOLUTION((scaled_height - SCREEN_HEIGHT) / 2);
      image_scaling.region_y2 = VITA_DECODER_RESOLUTION((scaled_height + SCREEN_HEIGHT) / 2);
    } else {
      image_scaling.texture_width = VITA_DECODER_RESOLUTION(scaled_width);
      image_scaling.region_x2 = VITA_DECODER_RESOLUTION(scaled_width);
      image_scaling.origin_x = round((double) (SCREEN_WIDTH - image_scaling.texture_width) / 2);
    }
  } else {
    // host ratio example: 16:9, 21:9, 32:9
    // Vita ratio range: native (960 x 544) - 15:1 (960 x 64)
    if (config.center_region_only) {
      image_scaling.texture_width = VITA_DECODER_RESOLUTION(scaled_width);
      image_scaling.region_x1 = VITA_DECODER_RESOLUTION((scaled_width - SCREEN_WIDTH) / 2);
      image_scaling.region_x2 = VITA_DECODER_RESOLUTION((scaled_width + SCREEN_WIDTH) / 2);
    } else {
      image_scaling.texture_height = VITA_DECODER_RESOLUTION(scaled_height);
      image_scaling.region_y2 = VITA_DECODER_RESOLUTION(scaled_height);
      image_scaling.origin_y = round((double) (SCREEN_HEIGHT - image_scaling.texture_height) / 2);
    }
  }

  printf("update_scaling_settings: width = %u\n", width);
  printf("update_scaling_settings: height = %u\n", height);
  printf("update_scaling_settings: scaled_width = %f\n", scaled_width);
  printf("update_scaling_settings: scaled_height = %f\n", scaled_height);
  printf("update_scaling_settings: image_scaling.texture_width = %u\n", image_scaling.texture_width);
  printf("update_scaling_settings: image_scaling.texture_height = %u\n", image_scaling.texture_height);
  printf("update_scaling_settings: image_scaling.origin_x = %f\n", image_scaling.origin_x);
  printf("update_scaling_settings: image_scaling.origin_y = %f\n", image_scaling.origin_y);
  printf("update_scaling_settings: image_scaling.region_x1 = %f\n", image_scaling.region_x1);
  printf("update_scaling_settings: image_scaling.region_y1 = %f\n", image_scaling.region_y1);
  printf("update_scaling_settings: image_scaling.region_x2 = %f\n", image_scaling.region_x2);
  printf("update_scaling_settings: image_scaling.region_y2 = %f\n", image_scaling.region_y2);
}

static int vita_pacer_thread_main(SceSize args, void *argp) {
  int max_fps = config.stream.fps;
  uint64_t last_check_time = sceKernelGetSystemTimeWide();
  atomic_store_u32(&need_drop, 0);
  atomic_store_u32(&frame_count, 0);

  while (atomic_load_u32(&active_pacer_thread)) {
    uint64_t now = sceKernelGetSystemTimeWide();

    if (now - last_check_time >= PACER_SAMPLE_INTERVAL_US) {
      uint32_t curr_frame_count =
          atomic_exchange_u32(&frame_count, 0);

      if (atomic_load_u32(&active_video_thread) &&
          config.enable_frame_pacer &&
          curr_frame_count > max_fps) {
        atomic_add_u32(&need_drop, curr_frame_count - max_fps);
      }

      atomic_store_fps(curr_frame_count, (uint32_t)max_fps);
      last_check_time = now;
    }

    bool live_ui =
        stream_overlay_is_open() ||
        ui_diagnostics_get_overlay_mode() != UI_DIAGNOSTICS_OVERLAY_OFF;
    bool redraw_pending =
        atomic_load_u32(&redraw_request_generation) !=
        atomic_load_u32(&rendered_redraw_generation);
    /*
     * A try-lock keeps the watchdog out of the latency-sensitive decode path.
     * It redraws only after video has gone quiet, or once for an input-driven
     * request such as opening/closing or changing the stream menu.
     */
    if (atomic_load_u32(&active_video_thread) &&
        atomic_load_u32(&video_render_mutex_initialized) &&
        (redraw_pending || live_ui) &&
        pthread_mutex_trylock(&video_render_mutex) == 0) {
      now = sceKernelGetSystemTimeWide();
      redraw_pending =
          atomic_load_u32(&redraw_request_generation) !=
          atomic_load_u32(&rendered_redraw_generation);
      bool video_stalled =
          !decoded_frame_available ||
          now - last_video_activity_us >= UI_WATCHDOG_STALE_US;
      bool requested_redraw =
          redraw_pending &&
          now - last_render_us >= UI_REQUEST_MIN_INTERVAL_US;
      bool periodic_redraw =
          live_ui &&
          video_stalled &&
          now - last_render_us >= UI_WATCHDOG_REFRESH_US;

      if (requested_redraw || periodic_redraw) {
        draw_stream_surface(false);
      }
      pthread_mutex_unlock(&video_render_mutex);
    }

    sceKernelDelayThread(
        (atomic_load_u32(&redraw_request_generation) !=
             atomic_load_u32(&rendered_redraw_generation) ||
         live_ui)
            ? UI_WATCHDOG_POLL_US
            : UI_WATCHDOG_IDLE_POLL_US);
  }
  return 0;
}

static bool stop_pacer_thread(void) {
  if (video_status != INIT_FRAME_PACER_THREAD) return true;

  atomic_store_u32(&active_pacer_thread, 0);
  SceUInt timeout = VIDEO_CLEANUP_WAIT_US;
  int thread_status = 0;
  int wait_result =
      sceKernelWaitThreadEnd(pacer_thread, &thread_status, &timeout);
  if (wait_result < 0) {
    /*
     * Vita's user-mode thread API has no forced termination primitive.
     * Quarantine all video resources instead of risking a use-after-free if
     * the watchdog is still inside Vita2D or waiting for the display.
     */
    vita_debug_event(
        VITA_DEBUG_LEVEL_ERROR, "decoder.state",
        "state=quarantined phase=pacer_stop code=0x%08x",
        (unsigned int)wait_result);
    atomic_store_u32(&video_cleanup_blocked, 1);
    return false;
  }

  int delete_result = sceKernelDeleteThread(pacer_thread);
  if (delete_result < 0) {
    vita_debug_event(
        VITA_DEBUG_LEVEL_WARNING, "decoder.state",
        "state=cleanup_warning phase=pacer_delete code=0x%08x",
        (unsigned int)delete_result);
  }
  pacer_thread = -1;
  video_status--;
  return true;
}

static bool lock_video_render_bounded(const char *operation) {
  if (!atomic_load_u32(&video_render_mutex_initialized)) return false;
  uint64_t deadline =
      sceKernelGetSystemTimeWide() + VIDEO_CLEANUP_WAIT_US;
  while (pthread_mutex_trylock(&video_render_mutex) != 0) {
    if (sceKernelGetSystemTimeWide() >= deadline) {
      (void)operation;
      vita_debug_event(
          VITA_DEBUG_LEVEL_ERROR, "decoder.state",
          "state=quarantined phase=render_lock code=timeout");
      atomic_store_u32(&video_cleanup_blocked, 1);
      return false;
    }
    sceKernelDelayThread(VIDEO_CLEANUP_LOCK_POLL_US);
  }
  return true;
}

static void vita_cleanup() {
  enum VideoStatus previous_status = video_status;
  atomic_store_u32(&active_video_thread, 0);
  if (!stop_pacer_thread()) return;
  bool render_mutex_locked =
      atomic_load_u32(&video_render_mutex_initialized) != 0;
  if (render_mutex_locked &&
      !lock_video_render_bounded("Video cleanup")) {
    return;
  }

  /* video_status records completed platform stages, while the pointers and
   * UIDs below may be acquired partway through the next stage. Release by
   * ownership so every setup failure is retry-safe. */
  if (video_status >= INIT_AVC_DEC && decoder != NULL) {
    sceAvcdecDeleteDecoder(decoder);
  }

  if (decoderblock >= 0) {
    sceKernelFreeMemBlock(decoderblock);
    decoderblock = -1;
  }
  if (decoder != NULL) {
    free(decoder);
    decoder = NULL;
  }
  if (decoder_info != NULL) {
    free(decoder_info);
    decoder_info = NULL;
  }

  if (video_status >= INIT_AVC_LIB) {
    sceVideodecTermLibrary(SCE_VIDEODEC_TYPE_HW_AVCDEC);
  }
  if (init != NULL) {
    free(init);
    init = NULL;
  }

  if (frame_texture != NULL) {
    vita2d_free_texture(frame_texture);
    frame_texture = NULL;
  }
  if (decoder_buffer != NULL) {
    free(decoder_buffer);
    decoder_buffer = NULL;
  }
  decoder_buffer_size = 0;

  if (video_status >= INIT_GS) {
    gs_sps_stop();
  }
  video_status = NOT_INIT;

  decoded_frame_available = false;
  atomic_store_u32(&redraw_request_generation, 0);
  atomic_store_u32(&rendered_redraw_generation, 0);
  atomic_store_u32(&frame_count, 0);
  atomic_store_u32(&need_drop, 0);
  atomic_store_fps(0, 0);
  atomic_store_u32(&poor_net_indicator_requested, 0);
  poor_net_indicator.alpha = 0;
  poor_net_indicator.plus = false;
  last_video_activity_us = 0;
  last_render_us = 0;

  if (render_mutex_locked) {
    pthread_mutex_unlock(&video_render_mutex);
    int destroy_result = pthread_mutex_destroy(&video_render_mutex);
    if (destroy_result == 0) {
      atomic_store_u32(&video_render_mutex_initialized, 0);
      atomic_store_u32(&video_cleanup_blocked, 0);
    } else {
      vita_debug_event(
          VITA_DEBUG_LEVEL_WARNING, "decoder.state",
          "state=cleanup_warning phase=render_mutex_destroy code=0x%08x",
          (unsigned int)destroy_result);
      atomic_store_u32(&video_cleanup_blocked, 1);
    }
  }
  if (previous_status != NOT_INIT) {
    vita_debug_event(
        VITA_DEBUG_LEVEL_INFO, "decoder.state",
        "state=stopped previous_state_id=%d", previous_status);
  }
}

static int vita_setup(int videoFormat, int width, int height, int redrawRate, void* context, int drFlags) {
  int ret;
  (void)context;
  vita_debug_event(
      VITA_DEBUG_LEVEL_INFO, "decoder.state",
      "state=initializing backend=vita_hw_h264 format_mask=0x%x "
      "width=%d height=%d refresh_hz=%d flags=0x%x",
      (unsigned int)videoFormat, width, height, redrawRate,
      (unsigned int)drFlags);
  last_decoder_error_log_us = 0;
  suppressed_decoder_errors = 0;

  if (atomic_load_u32(&video_cleanup_blocked)) {
    vita_debug_event(
        VITA_DEBUG_LEVEL_ERROR, "decoder.state",
        "state=error phase=preflight code=cleanup_blocked");
    return VITA_VIDEO_ERROR_CLEANUP_BLOCKED;
  }

  if (!atomic_load_u32(&video_render_mutex_initialized)) {
    ret = pthread_mutex_init(&video_render_mutex, NULL);
    if (ret != 0) {
      printf("pthread_mutex_init: 0x%x\n", ret);
      vita_debug_event(
          VITA_DEBUG_LEVEL_ERROR, "decoder.state",
          "state=error phase=setup code=0x%08x",
          (unsigned int)ret);
      return VITA_VIDEO_ERROR_CREATE_RENDER_MUTEX;
    }
    atomic_store_u32(&video_render_mutex_initialized, 1);
  }
  decoded_frame_available = false;
  atomic_store_u32(&active_video_thread, 0);
  atomic_store_u32(&redraw_request_generation, 0);
  atomic_store_u32(&rendered_redraw_generation, 0);
  atomic_store_u32(&frame_count, 0);
  atomic_store_u32(&need_drop, 0);
  atomic_store_fps(0, 0);
  atomic_store_u32(&poor_net_indicator_requested, 0);
  poor_net_indicator.alpha = 0;
  poor_net_indicator.plus = false;
  last_video_activity_us = sceKernelGetSystemTimeWide();
  last_render_us = 0;

  if (video_status == NOT_INIT) {
    // INIT_GS
    gs_sps_init(width, height);
    video_status++;
  }

  if (video_status == INIT_GS) {
    // INIT_FRAMEBUFFER
    update_scaling_settings(width, height);

    decoder_buffer_size = DECODER_BUFFER_SIZE + AV_INPUT_BUFFER_PADDING_SIZE;
    decoder_buffer = malloc(decoder_buffer_size);
    if (decoder_buffer == NULL) {
      printf("decoder_buffer: not enough memory\n");
      ret = VITA_VIDEO_ERROR_NO_MEM;
      goto cleanup;
    } 

    frame_texture = vita2d_create_empty_texture_format(image_scaling.texture_width, image_scaling.texture_height, SCE_GXM_TEXTURE_FORMAT_U8U8U8U8_ABGR);
    if (frame_texture == NULL) {
      printf("frame_texture: not enough memory\n");
      ret = VITA_VIDEO_ERROR_NO_MEM;
      goto cleanup;
    }

    printf("Initalized Framebuffer");

    video_status++;
  }

  if (video_status == INIT_FRAMEBUFFER) {
    // INIT_AVC_LIB
    if (init == NULL) {
      init = calloc(1, sizeof(SceVideodecQueryInitInfoHwAvcdec));
      if (init == NULL) {
        printf("init_framebuffer: not enough memory\n");
        ret = VITA_VIDEO_ERROR_NO_MEM;
        goto cleanup;
      }
    }
    init->size = sizeof(SceVideodecQueryInitInfoHwAvcdec);
    init->horizontal = VITA_DECODER_RESOLUTION(width);
    init->vertical = VITA_DECODER_RESOLUTION(height);
    init->numOfRefFrames = 4;
    init->numOfStreams = 1;

    ret = sceVideodecInitLibrary(SCE_VIDEODEC_TYPE_HW_AVCDEC, init);
    if (ret < 0) {
      printf("sceVideodecInitLibrary 0x%x\n", ret);
      ret = VITA_VIDEO_ERROR_INIT_LIB;
      goto cleanup;
    }
    printf("Initalized AVC library");
    video_status++;
  }

  if (video_status == INIT_AVC_LIB) {
    // INIT_DECODER_MEMBLOCK
    if (decoder_info == NULL) {
      decoder_info = calloc(1, sizeof(SceAvcdecQueryDecoderInfo));
      if (decoder_info == NULL) {
        printf("decoder_info: not enough memory\n");
        ret = VITA_VIDEO_ERROR_NO_MEM;
        goto cleanup;
      }
    }
    decoder_info->horizontal = init->horizontal;
    decoder_info->vertical = init->vertical;
    decoder_info->numOfRefFrames = init->numOfRefFrames;

    SceAvcdecDecoderInfo decoder_info_out = {0};

    ret = sceAvcdecQueryDecoderMemSize(SCE_VIDEODEC_TYPE_HW_AVCDEC, decoder_info, &decoder_info_out);
    if (ret < 0) {
      printf("sceAvcdecQueryDecoderMemSize 0x%x size 0x%x\n", ret, decoder_info_out.frameMemSize);
      ret = VITA_VIDEO_ERROR_QUERY_DEC_MEMSIZE;
      goto cleanup;
    }

    decoder = calloc(1, sizeof(SceAvcdecCtrl));
    if (decoder == NULL) {
      printf("not enough memory\n");
      ret = VITA_VIDEO_ERROR_ALLOC_MEM;
      goto cleanup;
    }

    size_t sz = (decoder_info_out.frameMemSize + 0xFFFFF) & ~0xFFFFF;
    decoder->frameBuf.size = sz;
    printf("allocating size 0x%x\n", sz);

    decoderblock = sceKernelAllocMemBlock("decoder", SCE_KERNEL_MEMBLOCK_TYPE_USER_MAIN_PHYCONT_NC_RW, sz, NULL);
    if (decoderblock < 0) {
      printf("decoderblock: 0x%08x\n", decoderblock);
      ret = VITA_VIDEO_ERROR_ALLOC_MEM;
      goto cleanup;
    }

    ret = sceKernelGetMemBlockBase(decoderblock, &decoder->frameBuf.pBuf);
    if (ret < 0) {
      printf("sceKernelGetMemBlockBase: 0x%x\n", ret);
      ret = VITA_VIDEO_ERROR_GET_MEMBASE;
      goto cleanup;
    }
    video_status++;
  }

  if (video_status == INIT_DECODER_MEMBLOCK) {
    // INIT_AVC_DEC
    printf("base: 0x%08x\n", (unsigned int)decoder->frameBuf.pBuf);

    ret = sceAvcdecCreateDecoder(SCE_VIDEODEC_TYPE_HW_AVCDEC, decoder, decoder_info);
    if (ret < 0) {
      printf("sceAvcdecCreateDecoder 0x%x\n", ret);
      ret = VITA_VIDEO_ERROR_CREATE_DEC;
      goto cleanup;
    }
    video_status++;
  }

  if (video_status == INIT_AVC_DEC) {
    // INIT_FRAME_PACER_THREAD
    ret = sceKernelCreateThread("frame_pacer", vita_pacer_thread_main, 0, 0x10000, 0, 0, NULL);
    if (ret < 0) {
      printf("sceKernelCreateThread 0x%x\n", ret);
      ret = VITA_VIDEO_ERROR_CREATE_PACER_THREAD;
      goto cleanup;
    }
    pacer_thread = ret;
    atomic_store_u32(&active_pacer_thread, 1);
    ret = sceKernelStartThread(pacer_thread, 0, NULL);
    if (ret < 0) {
      atomic_store_u32(&active_pacer_thread, 0);
      sceKernelDeleteThread(pacer_thread);
      pacer_thread = -1;
      printf("sceKernelStartThread 0x%x\n", ret);
      ret = VITA_VIDEO_ERROR_CREATE_PACER_THREAD;
      goto cleanup;
    }
    video_status++;
  }

  vita_debug_event(
      VITA_DEBUG_LEVEL_INFO, "decoder.state",
      "state=ready backend=vita_hw_h264 codec=h264 format_mask=0x%x "
      "width=%d height=%d texture_width=%u texture_height=%u "
      "refresh_hz=%d ref_frames=%u frame_pacer=%d",
      (unsigned int)videoFormat, width, height,
      (unsigned int)image_scaling.texture_width,
      (unsigned int)image_scaling.texture_height, redrawRate,
      (unsigned int)(init ? init->numOfRefFrames : 0),
      config.enable_frame_pacer ? 1 : 0);
  return VITA_VIDEO_INIT_OK;

cleanup:
  vita_debug_event(
      VITA_DEBUG_LEVEL_ERROR, "decoder.state",
      "state=error phase=setup code=0x%08x format_mask=0x%x "
      "width=%d height=%d",
      (unsigned int)ret, (unsigned int)videoFormat, width, height);
  vita_cleanup();
  return ret;
}

static int vita_submit_decode_unit(PDECODE_UNIT decodeUnit) {
  SceAvcdecAu au = {0};
  SceAvcdecArrayPicture array_picture = {0};
  struct SceAvcdecPicture picture = {0};
  struct SceAvcdecPicture *pictures = { &picture };
  array_picture.numOfElm = 1;
  array_picture.pPicture = &pictures;

  //frame->time = decodeUnit->receiveTimeMs;

  picture.size = sizeof(picture);
  picture.frame.pixelType = 0;
  picture.frame.framePitch = image_scaling.texture_width;
  picture.frame.frameWidth = image_scaling.texture_width;
  picture.frame.frameHeight = image_scaling.texture_height;
  picture.frame.pPicture[0] = vita2d_texture_get_datap(frame_texture);

  size_t payload_capacity = 0;
  size_t required_allocation = 0;
  if (!decoder_buffer_requirement(
          decodeUnit, &payload_capacity, &required_allocation)) {
    vita_debug_event(
        VITA_DEBUG_LEVEL_ERROR, "decoder.state",
        "state=error phase=validate_input source_bytes=%d",
        decodeUnit != NULL ? decodeUnit->fullLength : 0);
    return DR_NEED_IDR;
  }

  if (decoder_buffer == NULL || decoder_buffer_size < required_allocation) {
    printf("Reallocating decoder buffer to %u bytes",
           (unsigned int)required_allocation);
    char* resized_buffer = realloc(decoder_buffer, required_allocation);
    if (resized_buffer == NULL) {
      vita_debug_event(
          VITA_DEBUG_LEVEL_ERROR, "decoder.state",
          "state=error phase=grow_input_buffer requested_bytes=%u",
          (unsigned int)required_allocation);
      return DR_NEED_IDR;
    }
    decoder_buffer = resized_buffer;
    decoder_buffer_size = required_allocation;
  }

  PLENTRY entry = decodeUnit->bufferList;
  uint32_t length = 0;
  while (entry != NULL) {
    if (entry->bufferType == BUFFER_TYPE_SPS) {
      if (!gs_sps_fix(entry, GS_SPS_BITSTREAM_FIXUP,
                      (uint8_t*)decoder_buffer, payload_capacity, &length)) {
        vita_debug_event(
            VITA_DEBUG_LEVEL_ERROR, "decoder.state",
            "state=error phase=rewrite_sps source_bytes=%d",
            decodeUnit->fullLength);
        return DR_NEED_IDR;
      }
    } else {
      size_t entry_length = (size_t)entry->length;
      if ((size_t)length > payload_capacity ||
          entry_length > payload_capacity - (size_t)length) {
        vita_debug_event(
            VITA_DEBUG_LEVEL_ERROR, "decoder.state",
            "state=error phase=copy_input source_bytes=%d output_bytes=%u",
            decodeUnit->fullLength, (unsigned int)length);
        return DR_NEED_IDR;
      }
      memcpy(decoder_buffer + length, entry->data, entry_length);
      length += (uint32_t)entry_length;
    }
    entry = entry->next;
  }

  /* Hardware decoders may read a small distance past the payload. Keep that
   * region allocated and deterministic after both copies and SPS rewrites. */
  memset(decoder_buffer + length, 0, AV_INPUT_BUFFER_PADDING_SIZE);

  au.es.pBuf = decoder_buffer;
  au.es.size = length;
  au.dts.lower = 0xFFFFFFFF;
  au.dts.upper = 0xFFFFFFFF;
  au.pts.lower = 0xFFFFFFFF;
  au.pts.upper = 0xFFFFFFFF;

  bool collect_diagnostics = ui_diagnostics_metrics_needed();
  pthread_mutex_lock(&video_render_mutex);
  uint64_t decode_started_us =
      collect_diagnostics ? sceKernelGetSystemTimeWide() : 0;
  int ret = 0;
  ret = sceAvcdecDecode(decoder, &au, &array_picture);
  uint64_t decode_elapsed_us = collect_diagnostics
      ? sceKernelGetSystemTimeWide() - decode_started_us
      : 0;
  uint32_t decode_time_us = decode_elapsed_us > UINT32_MAX
      ? UINT32_MAX
      : (uint32_t)decode_elapsed_us;
  if (ret < 0) {
    if (collect_diagnostics) {
      ui_diagnostics_record_video_frame(
          length, decode_time_us, false);
    }
    uint64_t now_us = sceKernelGetSystemTimeWide();
    if (last_decoder_error_log_us == 0 ||
        now_us - last_decoder_error_log_us >=
            DECODER_ERROR_LOG_INTERVAL_US) {
      vita_debug_event(
          VITA_DEBUG_LEVEL_ERROR, "decoder.state",
          "state=error phase=decode code=0x%08x source_bytes=%u "
          "unit_bytes=%u outputs=%d "
          "repeats_suppressed=%u",
          (unsigned int)ret, (unsigned int)decodeUnit->fullLength,
          (unsigned int)length, array_picture.numOfOutput,
          suppressed_decoder_errors);
      last_decoder_error_log_us = now_us;
      suppressed_decoder_errors = 0;
    } else {
      suppressed_decoder_errors++;
    }
    pthread_mutex_unlock(&video_render_mutex);
    return DR_NEED_IDR;
  }

  if (array_picture.numOfOutput != 1) {
    if (collect_diagnostics) {
      ui_diagnostics_record_video_frame(
          length, decode_time_us, false);
    }
    //printf("numOfOutput %d\n", array_picture.numOfOutput);
    pthread_mutex_unlock(&video_render_mutex);
    return DR_OK;
  }

  decoded_frame_available = true;
  last_video_activity_us = sceKernelGetSystemTimeWide();
  bool presented = false;

  //TODO: Seems silly to decode the unit if we're going to drop the frame?
  // Find out why we decode or if we even need to
  if (atomic_load_u32(&active_video_thread)) {
    uint32_t frames_to_drop = atomic_load_u32(&need_drop);
    if (frames_to_drop > 0) {
      // skip
      atomic_sub_u32(&need_drop, 1);
    } else {
      draw_stream_surface(true);
      presented = true;
    }
  }

  if (collect_diagnostics) {
    ui_diagnostics_record_video_frame(
        length, decode_time_us, presented);
  }
  pthread_mutex_unlock(&video_render_mutex);

  // if (numframes++ % 6 == 0)
  //   return DR_NEED_IDR;

  return DR_OK;
}

void draw_streaming(vita2d_texture *frame_texture) {
  // ui is still rendering in the background, clear the screen first
  vita2d_clear_screen();
  vita2d_draw_texture_part(frame_texture,
                           image_scaling.origin_x,
                           image_scaling.origin_y,
                           image_scaling.region_x1,
                           image_scaling.region_y1,
                           image_scaling.region_x2,
                           image_scaling.region_y2);
}

void draw_indicators() {
  if (atomic_load_u32(&poor_net_indicator_requested)) {
    vita2d_font_draw_text(font, 40, 500, RGBA8(0xFF, 0xFF, 0xFF, poor_net_indicator.alpha), 64, ICON_NETWORK);
    poor_net_indicator.alpha += (0x4 * (poor_net_indicator.plus ? 1 : -1));
    if (poor_net_indicator.alpha == 0) {
      poor_net_indicator.plus = !poor_net_indicator.plus;
      poor_net_indicator.alpha += (0x4 * (poor_net_indicator.plus ? 1 : -1));
    }
  } else {
    poor_net_indicator.alpha = 0;
    poor_net_indicator.plus = false;
  }

  if (dc_tracker.currently_sprinting) {
    vita2d_font_draw_text(font, 40, 50, RGBA8(0xFF, 0xFF, 0xFF, 0xAA), 48, ICON_SPRINTING);
  }

}

void vitavideo_get_fps(uint32_t *rendered, uint32_t *target) {
  uint32_t packed = atomic_load_u32(&fps_snapshot);
  if (rendered) *rendered = packed & 0xffffU;
  if (target) *target = (packed >> 16) & 0xffffU;
}

void vitavideo_start() {
  if (atomic_load_u32(&video_render_mutex_initialized)) {
    if (!lock_video_render_bounded("Video start")) return;
    vita2d_set_vblank_wait(config.enable_vita_vblank_wait);
    atomic_store_u32(&active_video_thread, 1);
    pthread_mutex_unlock(&video_render_mutex);
  } else {
    vita2d_set_vblank_wait(config.enable_vita_vblank_wait);
    atomic_store_u32(&active_video_thread, 1);
  }
  vitavideo_request_redraw();
}

void vitavideo_stop() {
  atomic_store_u32(&active_video_thread, 0);
  if (atomic_load_u32(&video_render_mutex_initialized)) {
    if (!lock_video_render_bounded("Video stop")) return;
    vita2d_set_vblank_wait(true);
    pthread_mutex_unlock(&video_render_mutex);
  } else {
    vita2d_set_vblank_wait(true);
  }
}

void vitavideo_request_redraw() {
  if (atomic_load_u32(&video_render_mutex_initialized)) {
    atomic_add_u32(&redraw_request_generation, 1);
  }
}

void vitavideo_show_poor_net_indicator() {
  atomic_store_u32(&poor_net_indicator_requested, 1);
}

void vitavideo_hide_poor_net_indicator() {
  atomic_store_u32(&poor_net_indicator_requested, 0);
}

int vitavideo_initialized() {
  return video_status != NOT_INIT;
}

DECODER_RENDERER_CALLBACKS decoder_callbacks_vita = {
  .setup = vita_setup,
  .cleanup = vita_cleanup,
  .submitDecodeUnit = vita_submit_decode_unit,
  /* Decode, draw, and buffer swap may block. Keep them off the receive thread
   * and let moonlight-common's renderer queue absorb short scheduling jitter. */
  .capabilities = CAPABILITY_SLICES_PER_FRAME(2)
};
