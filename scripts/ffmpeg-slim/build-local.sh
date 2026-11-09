#!/bin/bash
# Builds the slim FFmpeg that Squish ships, natively with MSYS2 (UCRT64). No Docker.
#
# Normally you do not run this by hand: scripts/fetch-tools.ps1 runs it for the "ffmpeg" manifest entry (locally and in
# CI). Run it yourself to rebuild or debug:
#   MSYSTEM=UCRT64 /c/msys64/usr/bin/bash.exe -l scripts/ffmpeg-slim/build-local.sh [ffmpeg-tag] [work-dir] [stage-dir]
#
# With a stage-dir the finished bundle (ffmpeg.exe, ffprobe.exe, every DLL, LICENSE.txt, SOURCE.txt) is copied there and
# no zip is made; without one the bundle is zipped inside work-dir and its SHA256 printed.
#
# Setup: MSYS2 fully upgraded (pacman -Syu, twice if it asks to close), then
#   grep -v -E '^\s*(#|$)' scripts/ffmpeg-slim/msys2-packages.txt | xargs pacman -S --needed --noconfirm
#
# What is kept and why (see src/Core/Optimizers/VideoOptimiser*.cs):
#   libx264 / libx265 / libsvtav1 / libvpx-vp9   software video encoders
#   h264|hevc|av1 _nvenc / _amf / _qsv           hardware encoders (ffnvcodec + AMF headers, libvpl)
#   libopus + native aac                          audio encoders
#   libwebp (libwebp_anim)                        animated WebP output
#   libdav1d                                      fast AV1 decode
#   lavfi input                                   lives in libavdevice; the tests build their fixtures with -f lavfi
# Everything else the stock builds link in (whisper + Vulkan shaders, Flite voices, rav1e, libaom, VVC, AVS, libplacebo,
# the network and disc stack, capture devices) is left out, which is what takes 230 MB down to about 74 MB.
#
# The codec libraries are MSYS2's shared DLLs, so they are bundled next to FFmpeg (nothing is needed from MSYS2 at run time).
set -euo pipefail

tag="${1:-n9.0.2}"
work="${2:-$(pwd)/ffmpeg-slim-build}"
stage_out="${3:-}"
mingw="${MINGW_PREFIX:-/ucrt64}"
here="$(cd "$(dirname "$0")" && pwd)"

command -v gcc >/dev/null && command -v nasm >/dev/null || { echo "Run this from an MSYS2 UCRT64 shell (MSYSTEM=UCRT64) with the packages in msys2-packages.txt installed." >&2; exit 1; }
missing="$(grep -v -E '^\s*(#|$)' "$here/msys2-packages.txt" | xargs pacman -Q 2>&1 >/dev/null | sed -n "s/^error: package '\(.*\)' was not found$/\1/p" | tr '\n' ' ')"
[ -z "$missing" ] || { echo "Missing MSYS2 packages: $missing" >&2; echo "Install: grep -v -E '^\\s*(#|\$)' scripts/ffmpeg-slim/msys2-packages.txt | xargs pacman -S --needed --noconfirm" >&2; exit 1; }

mkdir -p "$work"
cd "$work"
# Fetch the tag directly instead of "git clone --branch <tag>": that prints "refs/tags/... is not a commit!" for FFmpeg's
# annotated tags and a long detached-HEAD hint.
if [ ! -d ffmpeg ]; then
  git init -q ffmpeg
  git -C ffmpeg remote add origin https://github.com/FFmpeg/FFmpeg.git
  git -C ffmpeg fetch -q --depth 1 origin "refs/tags/$tag"
  git -C ffmpeg -c advice.detachedHead=false checkout -q FETCH_HEAD
fi
prefix="$work/prefix"

# The explicit --disable-* flags stop MSYS2 libraries that happen to be installed (SDL2, lzma, ...) from being picked up.
(cd ffmpeg && ./configure --prefix="$prefix" --pkg-config-flags="--static" \
  --enable-gpl --enable-version3 --disable-debug --enable-shared --disable-static \
  --disable-indevs --disable-outdevs --enable-indev=lavfi --disable-network --disable-ffplay --disable-doc \
  --enable-iconv --enable-zlib --enable-amf --enable-libdav1d --enable-ffnvcodec \
  --enable-libopus --enable-libvpx --enable-libwebp --enable-libvpl \
  --enable-libsvtav1 --enable-libx264 --enable-libx265 \
  --disable-sdl2 --disable-lzma --disable-bzlib --disable-libmfx --disable-schannel --disable-vaapi \
  > "$work/configure.log" 2>&1) || { tail -40 "$work/configure.log"; exit 1; }
(cd ffmpeg && make -j"$(nproc)" > "$work/make.log" 2>&1 && make install > "$work/install.log" 2>&1) || { tail -40 "$work/make.log"; exit 1; }

# Stage ffmpeg.exe, ffprobe.exe, the FFmpeg DLLs and every DLL they need from the MinGW prefix (repeat until closed).
stage="${stage_out:-$work/stage}"
rm -rf "$stage" && mkdir -p "$stage"
cp "$prefix"/bin/*.exe "$prefix"/bin/*.dll "$stage/"
changed=1
while [ "$changed" = 1 ]; do
  changed=0
  for f in "$stage"/*.dll "$stage"/*.exe; do
    for d in $(objdump -p "$f" | awk '/DLL Name:/{print $3}'); do
      if [ ! -e "$stage/$d" ] && [ -e "$mingw/bin/$d" ]; then cp "$mingw/bin/$d" "$stage/"; changed=1; fi
    done
  done
done
cp ffmpeg/COPYING.GPLv3 "$stage/LICENSE.txt"
cat > "$stage/SOURCE.txt" <<EOF
This FFmpeg is licensed under the GPL (version 3). Built by the Squish project from FFmpeg $tag
(https://github.com/FFmpeg/FFmpeg) with scripts/ffmpeg-slim/build-local.sh. The bundled codec DLLs (x264, x265,
SVT-AV1, libvpx, dav1d, opus, libwebp, ...) are the MSYS2 mingw-w64 packages of the same names; their sources are
listed at https://packages.msys2.org/.
EOF

echo "unpacked: $(du -sh "$stage" | cut -f1)"
if [ -z "$stage_out" ]; then
  archive="$work/ffmpeg-slim-${tag}-win64.zip"
  rm -f "$archive"
  (cd "$stage" && powershell.exe -NoProfile -Command "Compress-Archive -Path * -DestinationPath '$(cygpath -w "$archive")' -Force")
  echo "zip     : $(du -h "$archive" | cut -f1)  $archive"
  echo "sha256  : $(sha256sum "$archive" | cut -d' ' -f1)"
fi
