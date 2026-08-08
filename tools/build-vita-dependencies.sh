#!/usr/bin/env bash
# Vita Moonlight source-only dependency build recipe.
# Copyright (C) 2026 Vita Moonlight contributors
# SPDX-License-Identifier: GPL-3.0-or-later
set -euo pipefail

if [[ $# -ne 2 ]]; then
  echo "Usage: $0 VERIFIED_SOURCE_DIRECTORY EMPTY_BUILD_DIRECTORY" >&2
  exit 2
fi
if [[ -z "${VITASDK:-}" ]]; then
  echo "VITASDK must point to the pinned SDK root." >&2
  exit 2
fi
if [[ ! -d "$1" ]]; then
  echo "Verified source directory does not exist: $1" >&2
  exit 2
fi
if [[ -e "$2" ]]; then
  echo "Refusing to reuse dependency build directory: $2" >&2
  exit 1
fi

source_root="$(cd "$1" && pwd -P)"
mkdir -p "$2"
build_root="$(cd "$2" && pwd -P)"
recipe_root="$(cd "$(dirname "$0")/.." && pwd -P)"
install_prefix="$VITASDK/arm-vita-eabi"
toolchain="$VITASDK/share/vita.toolchain.cmake"
jobs="${VITA_BUILD_JOBS:-2}"

case "$jobs" in
  ''|*[!0-9]*) echo "VITA_BUILD_JOBS must be a positive integer." >&2; exit 2 ;;
  0) echo "VITA_BUILD_JOBS must be a positive integer." >&2; exit 2 ;;
esac

export PATH="$VITASDK/bin:$PATH"
test -f "$toolchain"
test -x "$VITASDK/bin/arm-vita-eabi-gcc"

cmake_vita() {
  local source_directory="$1"
  local build_directory="$2"
  shift 2
  cmake -S "$source_directory" -B "$build_directory" \
    -DCMAKE_TOOLCHAIN_FILE="$toolchain" \
    -DCMAKE_INSTALL_PREFIX="$install_prefix" \
    -DCMAKE_BUILD_TYPE=Release \
    "$@"
}

build_install() {
  local build_directory="$1"
  cmake --build "$build_directory" --parallel "$jobs" --target install
}

echo "[1/11] Building zlib 1.3.2"
cmake_vita "$source_root/zlib-1.3.2" "$build_root/zlib" \
  -DZLIB_BUILD_SHARED=OFF \
  -DZLIB_BUILD_STATIC=ON \
  -DZLIB_BUILD_TESTING=OFF
build_install "$build_root/zlib"

echo "[2/11] Building bzip2 1.0.8"
make -C "$source_root/bzip2-1.0.8" --jobs "$jobs" \
  CC="$VITASDK/bin/arm-vita-eabi-gcc" \
  AR="$VITASDK/bin/arm-vita-eabi-ar" \
  RANLIB="$VITASDK/bin/arm-vita-eabi-ranlib" \
  CFLAGS="-Wall -Winline -O2 -D_FILE_OFFSET_BITS=64" \
  libbz2.a
install -d "$install_prefix/lib" "$install_prefix/include"
install -m 0644 "$source_root/bzip2-1.0.8/libbz2.a" "$install_prefix/lib/libbz2.a"
install -m 0644 "$source_root/bzip2-1.0.8/bzlib.h" "$install_prefix/include/bzlib.h"

echo "[3/11] Building zstd 1.5.7"
cmake_vita "$source_root/zstd-1.5.7/build/cmake" "$build_root/zstd" \
  -DZSTD_BUILD_PROGRAMS=OFF \
  -DZSTD_BUILD_SHARED=OFF \
  -DZSTD_BUILD_STATIC=ON \
  -DZSTD_BUILD_TESTS=OFF \
  -DZSTD_LEGACY_SUPPORT=OFF
build_install "$build_root/zstd"

echo "[4/11] Building libpng 1.6.58"
cmake_vita "$source_root/libpng-1.6.58" "$build_root/libpng" \
  -DPNG_SHARED=OFF \
  -DPNG_STATIC=ON \
  -DPNG_TESTS=OFF \
  -DPNG_TOOLS=OFF \
  -DPNG_ARM_NEON=on \
  -DSKIP_INSTALL_EXECUTABLES=ON \
  -DSKIP_INSTALL_PROGRAMS=ON
build_install "$build_root/libpng"

echo "[5/11] Building libjpeg-turbo 3.2.0"
cmake_vita "$source_root/libjpeg-turbo-3.2.0" "$build_root/libjpeg-turbo" \
  -DENABLE_SHARED=OFF \
  -DENABLE_STATIC=ON \
  -DWITH_SIMD=OFF \
  -DWITH_TURBOJPEG=OFF \
  -DWITH_TOOLS=OFF \
  -DWITH_TESTS=OFF \
  -DWITH_JAVA=OFF
build_install "$build_root/libjpeg-turbo"

echo "[6/11] Building FreeType 2.14.3"
cmake_vita "$source_root/freetype-2.14.3" "$build_root/freetype" \
  -DBUILD_SHARED_LIBS=OFF \
  -DFT_REQUIRE_ZLIB=ON \
  -DFT_REQUIRE_BZIP2=ON \
  -DFT_REQUIRE_PNG=ON \
  -DFT_DISABLE_HARFBUZZ=ON \
  -DFT_DISABLE_BROTLI=ON
build_install "$build_root/freetype"

echo "[7/11] Building libvita2d a8f15ab"
make -C "$source_root/libvita2d-a8f15ab/libvita2d" --jobs "$jobs" \
  VITASDK="$VITASDK" \
  CC="$VITASDK/bin/arm-vita-eabi-gcc" \
  AR="$VITASDK/bin/arm-vita-eabi-ar" \
  CFLAGS="-std=gnu11 -Wl,-q -Wall -O3 -Iinclude -I$install_prefix/include/freetype2" \
  libvita2d.a
install -m 0644 \
  "$source_root/libvita2d-a8f15ab/libvita2d/libvita2d.a" \
  "$install_prefix/lib/libvita2d.a"
install -m 0644 \
  "$source_root/libvita2d-a8f15ab/libvita2d/include/vita2d.h" \
  "$install_prefix/include/vita2d.h"

echo "[8/11] Building Expat 2.8.2"
cmake_vita "$source_root/expat-2.8.2" "$build_root/expat" \
  -DEXPAT_SHARED_LIBS=OFF \
  -DEXPAT_BUILD_TOOLS=OFF \
  -DEXPAT_BUILD_EXAMPLES=OFF \
  -DEXPAT_BUILD_TESTS=OFF \
  -DEXPAT_BUILD_DOCS=OFF \
  -DEXPAT_BUILD_FUZZERS=OFF \
  -DEXPAT_WITH_ARC4RANDOM=OFF \
  -DEXPAT_WITH_ARC4RANDOM_BUF=OFF \
  -DEXPAT_WITH_GETENTROPY=ON \
  -DEXPAT_WITH_GETRANDOM=OFF \
  -DEXPAT_WITH_SYS_GETRANDOM=OFF
build_install "$build_root/expat"
grep --fixed-strings '#define HAVE_GETENTROPY' "$install_prefix/include/expat_config.h"

echo "[9/11] Building Opus 1.6.1 with Vita NEON"
cmake_vita "$source_root/opus-1.6.1" "$build_root/opus" \
  -DOPUS_BUILD_SHARED_LIBRARY=OFF \
  -DOPUS_BUILD_TESTING=OFF \
  -DOPUS_BUILD_PROGRAMS=OFF \
  -DOPUS_MAY_HAVE_NEON=OFF \
  -DOPUS_PRESUME_NEON=ON
build_install "$build_root/opus"
grep --fixed-strings 'OPUS_ARM_MAY_HAVE_NEON_INTR' \
  "$build_root/opus/CMakeFiles/opus.dir/flags.make"
grep --fixed-strings 'OPUS_ARM_PRESUME_NEON_INTR' \
  "$build_root/opus/CMakeFiles/opus.dir/flags.make"

echo "[10/11] Building Mbed TLS 3.6.7"
bash "$recipe_root/tools/build-vita-mbedtls.sh" \
  "$source_root/mbedtls-3.6.7-vita" "$build_root/mbedtls"

echo "[11/11] Building curl 8.21.0"
cmake_vita "$source_root/curl-8.21.0" "$build_root/curl" \
  -DCMAKE_C_FLAGS=-D__vita__ \
  -DBUILD_SHARED_LIBS=OFF \
  -DBUILD_STATIC_LIBS=ON \
  -DBUILD_CURL_EXE=OFF \
  -DBUILD_TESTING=OFF \
  -DBUILD_EXAMPLES=OFF \
  -DBUILD_LIBCURL_DOCS=OFF \
  -DBUILD_MISC_DOCS=OFF \
  -DENABLE_CURL_MANUAL=OFF \
  -DHTTP_ONLY=ON \
  -DCURL_USE_MBEDTLS=ON \
  -DCURL_USE_OPENSSL=OFF \
  -DCURL_USE_LIBPSL=OFF \
  -DCURL_USE_LIBSSH2=OFF \
  -DCURL_USE_LIBSSH=OFF \
  -DCURL_ZLIB=ON \
  -DCURL_ZSTD=ON \
  -DCURL_BROTLI=OFF \
  -DENABLE_THREADED_RESOLVER=OFF \
  -DENABLE_IPV6=OFF \
  -DCURL_DISABLE_SOCKETPAIR=ON \
  -DCURL_DISABLE_DOH=ON \
  -DCURL_CA_BUNDLE=none \
  -DHAVE_PIPE2=0
build_install "$build_root/curl"

required_outputs=(
  "lib/libz.a"
  "lib/libbz2.a"
  "lib/libzstd.a"
  "lib/libpng16.a"
  "lib/libjpeg.a"
  "lib/libfreetype.a"
  "lib/libvita2d.a"
  "lib/libexpat.a"
  "lib/libopus.a"
  "lib/libmbedcrypto.a"
  "lib/libmbedx509.a"
  "lib/libmbedtls.a"
  "lib/libcurl.a"
)
for relative in "${required_outputs[@]}"; do
  test -s "$install_prefix/$relative"
done

echo "Installed the complete verified Vita dependency chain into $install_prefix"
