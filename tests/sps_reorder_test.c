#include "sps.h"
#include "h264_stream.h"

#include <stdint.h>
#include <stdio.h>
#include <string.h>


static int build_restricted_sps(uint8_t *buffer, size_t capacity) {
  if (capacity < GS_SPS_MAX_REWRITTEN_SIZE) return -1;

  h264_stream_t *stream = h264_new();
  if (!stream) return -1;
  stream->nal->nal_ref_idc = 3;
  stream->nal->nal_unit_type = NAL_UNIT_TYPE_SPS;
  stream->sps->profile_idc = 66;
  stream->sps->level_idc = 31;
  stream->sps->seq_parameter_set_id = 0;
  stream->sps->log2_max_frame_num_minus4 = 0;
  stream->sps->pic_order_cnt_type = 0;
  stream->sps->log2_max_pic_order_cnt_lsb_minus4 = 0;
  stream->sps->num_ref_frames = 2;
  stream->sps->pic_width_in_mbs_minus1 = 59;
  stream->sps->pic_height_in_map_units_minus1 = 33;
  stream->sps->frame_mbs_only_flag = 1;
  stream->sps->direct_8x8_inference_flag = 1;
  stream->sps->vui_parameters_present_flag = 1;
  stream->sps->vui.bitstream_restriction_flag = 1;
  stream->sps->vui.motion_vectors_over_pic_boundaries_flag = 1;
  stream->sps->vui.max_bytes_per_pic_denom = 2;
  stream->sps->vui.max_bits_per_mb_denom = 1;
  stream->sps->vui.log2_max_mv_length_horizontal = 16;
  stream->sps->vui.log2_max_mv_length_vertical = 16;
  stream->sps->vui.num_reorder_frames = 2;
  stream->sps->vui.max_dec_frame_buffering = 4;

  buffer[0] = 0;
  buffer[1] = 0;
  buffer[2] = 0;
  buffer[3] = 1;
  int length = write_nal_unit(stream, buffer + 4, 128);
  h264_free(stream);
  return length > 0 ? length + 4 : -1;
}


int main(void) {
  uint8_t input[GS_SPS_MAX_REWRITTEN_SIZE] = {0};
  int input_length = build_restricted_sps(input, sizeof(input));
  if (input_length <= 4) {
    fprintf(stderr, "could not construct SPS fixture\n");
    return 1;
  }

  LENTRY entry = {
      .next = NULL,
      .data = (char *)input,
      .length = input_length,
      .bufferType = BUFFER_TYPE_SPS,
  };
  uint8_t output[GS_SPS_MAX_REWRITTEN_SIZE] = {0};
  uint32_t output_length = 0;

  gs_sps_init(960, 544);
  bool fixed = gs_sps_fix(
      &entry, GS_SPS_BITSTREAM_FIXUP, output, sizeof(output),
      &output_length);
  gs_sps_stop();
  if (!fixed || output_length <= 4 ||
      memcmp(output, "\x00\x00\x00\x01", 4) != 0) {
    fprintf(stderr, "SPS fixup failed\n");
    return 1;
  }

  h264_stream_t *parsed = h264_new();
  if (!parsed || read_nal_unit(parsed, output + 4, output_length - 4) <= 0) {
    fprintf(stderr, "rewritten SPS could not be parsed\n");
    if (parsed) h264_free(parsed);
    return 1;
  }

  int result = 0;
  if (!parsed->sps->vui.bitstream_restriction_flag ||
      parsed->sps->num_ref_frames != 1 ||
      parsed->sps->vui.num_reorder_frames != 0 ||
      parsed->sps->vui.max_dec_frame_buffering != 1) {
    fprintf(stderr,
            "unexpected SPS: restriction=%d refs=%d reorder=%d buffer=%d\n",
            parsed->sps->vui.bitstream_restriction_flag,
            parsed->sps->num_ref_frames,
            parsed->sps->vui.num_reorder_frames,
            parsed->sps->vui.max_dec_frame_buffering);
    result = 1;
  }
  h264_free(parsed);
  if (result == 0) puts("SPS reorder test passed.");
  return result;
}
