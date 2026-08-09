#include "scaling.h"

#include <math.h>
#include <stdbool.h>
#include <stdio.h>
#include <stdlib.h>

#define EPSILON 0.02f

static void fail(const char *message) {
  fprintf(stderr, "video scaling test failed: %s\n", message);
  exit(1);
}

static void require_true(bool value, const char *message) {
  if (!value) fail(message);
}

static void require_near(float actual, float expected, const char *message) {
  if (fabsf(actual - expected) > EPSILON) {
    fprintf(stderr,
            "video scaling test failed: %s (actual %.4f, expected %.4f)\n",
            message, actual, expected);
    exit(1);
  }
}

static void require_safe_bounds(const VitaScalingSettings *settings) {
  require_true(settings->source_x >= 0.0f, "source x must be nonnegative");
  require_true(settings->source_y >= 0.0f, "source y must be nonnegative");
  require_true(settings->source_width > 0.0f, "source width must be positive");
  require_true(settings->source_height > 0.0f,
               "source height must be positive");
  require_true(
      settings->source_x + settings->source_width <=
          settings->texture_width + EPSILON,
      "source rectangle must fit texture width");
  require_true(
      settings->source_y + settings->source_height <=
          settings->texture_height + EPSILON,
      "source rectangle must fit texture height");
}

static void test_native_geometry(void) {
  VitaScalingSettings settings;
  require_true(vita_scaling_calculate(960, 544, false, &settings),
               "native fit calculation");
  require_true(settings.texture_width == 960, "native texture width");
  require_true(settings.texture_height == 544, "native texture height");
  require_near(settings.destination_x, 0.0f, "native destination x");
  require_near(settings.destination_y, 0.0f, "native destination y");
  require_near(settings.source_width, 960.0f, "native source width");
  require_near(settings.source_height, 544.0f, "native source height");
  require_near(settings.scale_x, 1.0f, "native x scale");
  require_near(settings.scale_y, 1.0f, "native y scale");
  require_safe_bounds(&settings);
}

static void test_sixteen_nine_fit(void) {
  VitaScalingSettings settings;
  require_true(vita_scaling_calculate(960, 540, false, &settings),
               "16:9 fit calculation");
  require_true(settings.texture_width == 960, "16:9 fit texture width");
  require_true(settings.texture_height == 544, "16:9 fit texture height");
  require_near(settings.destination_x, 0.0f, "16:9 fit destination x");
  require_near(settings.destination_y, 2.0f, "16:9 fit destination y");
  require_near(settings.source_width, 960.0f, "16:9 fit source width");
  require_near(settings.source_height, 544.0f, "16:9 fit source height");
  require_near(settings.scale_x, 1.0f, "16:9 fit x scale");
  require_near(settings.scale_y, 540.0f / 544.0f, "16:9 fit y scale");
  require_safe_bounds(&settings);
}

static void test_sixteen_nine_crop(void) {
  VitaScalingSettings settings;
  require_true(vita_scaling_calculate(960, 540, true, &settings),
               "16:9 crop calculation");
  require_true(settings.texture_width == 960, "16:9 crop texture width");
  require_true(settings.texture_height == 544, "16:9 crop texture height");
  require_near(settings.destination_x, 0.0f, "16:9 crop destination x");
  require_near(settings.destination_y, 0.0f, "16:9 crop destination y");
  require_near(settings.source_x, 60.0f / 17.0f,
               "16:9 crop source x");
  require_near(settings.source_width, 16200.0f / 17.0f,
               "16:9 crop source width");
  require_near(settings.source_height, 544.0f,
               "16:9 crop source height");
  require_near(settings.source_width * settings.scale_x, 960.0f,
               "16:9 crop output width");
  require_near(settings.source_height * settings.scale_y, 544.0f,
               "16:9 crop output height");
  require_safe_bounds(&settings);
}

static void test_four_three_fit(void) {
  VitaScalingSettings settings;
  require_true(vita_scaling_calculate(800, 600, false, &settings),
               "4:3 fit calculation");
  require_true(settings.texture_width == 720, "4:3 fit texture width");
  require_true(settings.texture_height == 544, "4:3 fit texture height");
  require_near(settings.destination_x, 352.0f / 3.0f,
               "4:3 fit destination x");
  require_near(settings.destination_y, 0.0f, "4:3 fit destination y");
  require_near(settings.source_width * settings.scale_x, 2176.0f / 3.0f,
               "4:3 fit output width");
  require_near(settings.source_height * settings.scale_y, 544.0f,
               "4:3 fit output height");
  require_safe_bounds(&settings);
}

static void test_four_three_crop(void) {
  VitaScalingSettings settings;
  require_true(vita_scaling_calculate(800, 600, true, &settings),
               "4:3 crop calculation");
  require_true(settings.texture_width == 960, "4:3 crop texture width");
  require_true(settings.texture_height == 720, "4:3 crop texture height");
  require_near(settings.source_x, 0.0f, "4:3 crop source x");
  require_near(settings.source_y, 88.0f, "4:3 crop source y");
  require_near(settings.source_width, 960.0f, "4:3 crop source width");
  require_near(settings.source_height, 544.0f, "4:3 crop source height");
  require_near(settings.scale_x, 1.0f, "4:3 crop x scale");
  require_near(settings.scale_y, 1.0f, "4:3 crop y scale");
  require_safe_bounds(&settings);
}

int main(void) {
  VitaScalingSettings settings;
  require_true(!vita_scaling_calculate(0, 544, false, &settings),
               "zero-width input must fail");
  require_true(!vita_scaling_calculate(960, -1, false, &settings),
               "negative-height input must fail");
  require_true(!vita_scaling_calculate(960, 544, false, NULL),
               "null output must fail");
  test_native_geometry();
  test_sixteen_nine_fit();
  test_sixteen_nine_crop();
  test_four_three_fit();
  test_four_three_crop();
  puts("Vita video scaling tests passed.");
  return 0;
}
