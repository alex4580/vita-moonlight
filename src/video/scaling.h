#ifndef VITA_VIDEO_SCALING_H
#define VITA_VIDEO_SCALING_H

#include <stdbool.h>

typedef struct VitaScalingSettings {
  unsigned int texture_width;
  unsigned int texture_height;
  float destination_x;
  float destination_y;
  float source_x;
  float source_y;
  float source_width;
  float source_height;
  float scale_x;
  float scale_y;
} VitaScalingSettings;

/*
 * Calculate decoder allocation and Vita2D presentation geometry separately.
 * The hardware decoder requires 16-pixel-aligned output dimensions, but those
 * aligned dimensions must never be reused as crop coordinates. The source
 * rectangle returned here is always contained by the decoder texture.
 */
bool vita_scaling_calculate(int source_width, int source_height,
                            bool crop_to_fill,
                            VitaScalingSettings *settings);

#endif
