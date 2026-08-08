#include "scaling.h"

#include <math.h>
#include <stdint.h>
#include <string.h>

enum {
  VITA_SCREEN_WIDTH = 960,
  VITA_SCREEN_HEIGHT = 544,
  VITA_DECODER_ALIGNMENT = 16,
  VITA_DECODER_MINIMUM_DIMENSION = 64,
};

static unsigned int decoder_dimension(double value) {
  double aligned = floor(
      value / VITA_DECODER_ALIGNMENT + 0.5) * VITA_DECODER_ALIGNMENT;
  if (aligned < VITA_DECODER_MINIMUM_DIMENSION) {
    aligned = VITA_DECODER_MINIMUM_DIMENSION;
  }
  return (unsigned int)aligned;
}

static double clamp_double(double value, double minimum, double maximum) {
  if (value < minimum) return minimum;
  if (value > maximum) return maximum;
  return value;
}

bool vita_scaling_calculate(int source_width, int source_height,
                            bool crop_to_fill,
                            VitaScalingSettings *settings) {
  if (settings == NULL || source_width <= 0 || source_height <= 0) {
    return false;
  }

  memset(settings, 0, sizeof(*settings));
  settings->texture_width = VITA_SCREEN_WIDTH;
  settings->texture_height = VITA_SCREEN_HEIGHT;

  double scaled_width =
      (double)VITA_SCREEN_HEIGHT * source_width / source_height;
  double scaled_height =
      (double)VITA_SCREEN_WIDTH * source_height / source_width;
  int64_t screen_product = (int64_t)VITA_SCREEN_WIDTH * source_height;
  int64_t source_product = (int64_t)VITA_SCREEN_HEIGHT * source_width;

  if (screen_product > source_product) {
    /* The stream is narrower than the Vita display. */
    if (crop_to_fill) {
      settings->texture_height = decoder_dimension(scaled_height);
    } else {
      settings->texture_width = decoder_dimension(scaled_width);
    }
  } else if (screen_product < source_product) {
    /* The stream is wider than the Vita display. */
    if (crop_to_fill) {
      settings->texture_width = decoder_dimension(scaled_width);
    } else {
      settings->texture_height = decoder_dimension(scaled_height);
    }
  }

  if (!crop_to_fill) {
    double presentation_scale_x =
        (double)VITA_SCREEN_WIDTH / source_width;
    double presentation_scale_y =
        (double)VITA_SCREEN_HEIGHT / source_height;
    double presentation_scale =
        presentation_scale_x < presentation_scale_y
            ? presentation_scale_x
            : presentation_scale_y;
    double destination_width = source_width * presentation_scale;
    double destination_height = source_height * presentation_scale;

    settings->destination_x =
        (float)((VITA_SCREEN_WIDTH - destination_width) / 2.0);
    settings->destination_y =
        (float)((VITA_SCREEN_HEIGHT - destination_height) / 2.0);
    settings->source_width = (float)settings->texture_width;
    settings->source_height = (float)settings->texture_height;
    settings->scale_x =
        (float)(destination_width / settings->texture_width);
    settings->scale_y =
        (float)(destination_height / settings->texture_height);
    return true;
  }

  double presentation_scale_x =
      (double)VITA_SCREEN_WIDTH / source_width;
  double presentation_scale_y =
      (double)VITA_SCREEN_HEIGHT / source_height;
  double presentation_scale =
      presentation_scale_x > presentation_scale_y
          ? presentation_scale_x
          : presentation_scale_y;
  double visible_source_width = VITA_SCREEN_WIDTH / presentation_scale;
  double visible_source_height = VITA_SCREEN_HEIGHT / presentation_scale;
  double texture_source_width = visible_source_width *
      settings->texture_width / source_width;
  double texture_source_height = visible_source_height *
      settings->texture_height / source_height;

  texture_source_width = clamp_double(
      texture_source_width, 0.0, settings->texture_width);
  texture_source_height = clamp_double(
      texture_source_height, 0.0, settings->texture_height);
  if (texture_source_width <= 0.0 || texture_source_height <= 0.0) {
    return false;
  }

  settings->source_x =
      (float)((settings->texture_width - texture_source_width) / 2.0);
  settings->source_y =
      (float)((settings->texture_height - texture_source_height) / 2.0);
  settings->source_width = (float)texture_source_width;
  settings->source_height = (float)texture_source_height;
  settings->scale_x = (float)(VITA_SCREEN_WIDTH / texture_source_width);
  settings->scale_y = (float)(VITA_SCREEN_HEIGHT / texture_source_height);
  return true;
}
