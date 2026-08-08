#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 2 ]]; then
  echo "Usage: $0 VERIFIED_INPUT_DIRECTORY EMPTY_BUILD_DIRECTORY" >&2
  exit 2
fi
if [[ -z "${VITASDK:-}" ]]; then
  echo "VITASDK must point to the pinned SDK root." >&2
  exit 2
fi

if [[ ! -d "$1" ]]; then
  echo "Verified input directory does not exist: $1" >&2
  exit 2
fi
input_dir="$(cd "$1" && pwd -P)"
mkdir -p "$2"
build_root="$(cd "$2" && pwd -P)"
source_archive="$input_dir/mbedtls-3.6.7.tar.bz2"
vita_patch="$input_dir/mbedtls-3.6.7-vita.patch"
source_root="$build_root/mbedtls-3.6.7"
build_dir="$source_root/build"
install_prefix="$VITASDK/arm-vita-eabi"

test -f "$VITASDK/share/vita.toolchain.cmake"
test -f "$source_archive"
test -f "$vita_patch"
if [[ -e "$source_root" ]]; then
  echo "Refusing to reuse an existing mbedTLS source tree: $source_root" >&2
  exit 1
fi
tar -xjf "$source_archive" -C "$build_root"
test -f "$source_root/CMakeLists.txt"

# git apply has no fuzzy-match mode. The check and application therefore fail
# closed if any reviewed source context differs from the official archive.
# The ceiling is essential when BUILD_DIRECTORY happens to live beneath this
# repository: without it, git silently filters patch paths against the outer
# repository prefix and may report success without changing the extracted tree.
GIT_CEILING_DIRECTORIES="$build_root" git -C "$source_root" apply --check "$vita_patch"
GIT_CEILING_DIRECTORIES="$build_root" git -C "$source_root" apply "$vita_patch"
grep --fixed-strings 'defined(__vita__)' "$source_root/library/common.h"
grep --fixed-strings 'vita_getentropy_wrapper' "$source_root/library/entropy_poll.c"
grep --fixed-strings 'SO_NONBLOCK' "$source_root/library/net_sockets.c"
grep --fixed-strings 'defined(__HAIKU__) || defined(__vita__)' \
  "$source_root/library/platform_util.c"

python3 "$source_root/scripts/config.py" set MBEDTLS_THREADING_C
python3 "$source_root/scripts/config.py" set MBEDTLS_THREADING_PTHREAD
grep --fixed-strings --line-regexp '#define MBEDTLS_THREADING_C' \
  "$source_root/include/mbedtls/mbedtls_config.h"
grep --fixed-strings --line-regexp '#define MBEDTLS_THREADING_PTHREAD' \
  "$source_root/include/mbedtls/mbedtls_config.h"

cmake -S "$source_root" -B "$build_dir" \
  -DCMAKE_TOOLCHAIN_FILE="$VITASDK/share/vita.toolchain.cmake" \
  -DCMAKE_INSTALL_PREFIX="$install_prefix" \
  -DCMAKE_BUILD_TYPE=Release \
  -DCMAKE_C_FLAGS=-D__vita__ \
  -DENABLE_PROGRAMS=OFF \
  -DENABLE_TESTING=OFF \
  -DMBEDTLS_FATAL_WARNINGS=OFF
cmake --build "$build_dir" --parallel --target mbedtls
cmake --install "$build_dir"

test -f "$install_prefix/include/mbedtls/build_info.h"
test -f "$install_prefix/lib/libmbedcrypto.a"
test -f "$install_prefix/lib/libmbedx509.a"
test -f "$install_prefix/lib/libmbedtls.a"
grep --fixed-strings '#define MBEDTLS_VERSION_STRING_FULL    "Mbed TLS 3.6.7"' \
  "$install_prefix/include/mbedtls/build_info.h"
echo "Installed verified mbedTLS 3.6.7 for Vita into $install_prefix"
