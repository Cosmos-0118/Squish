#!/bin/bash
# Turns a checkout of BtbN/FFmpeg-Builds into a "slim" Squish build recipe.
#
# BtbN's recipe builds everything it can. Squish only needs the encoders, decoders and filters listed in KEEP below,
# and every extra library is linked into libavcodec/libavformat/libavfilter, so the stock result is ~200 MB of DLLs.
# This script changes only the *selection*, never how a kept library is built:
#   1. scripts.d/zz-final.sh is the root of the dependency graph. Its dependency list is replaced by KEEP, so every
#      script outside the closure of KEEP is neither built nor passed to ./configure.
#   2. variants/win64-slim-shared.sh is the stock win64-gpl-shared variant plus a few --disable flags.
#
# Usage: apply-slim.sh <path-to-FFmpeg-Builds-checkout>
set -euo pipefail

root="${1:?usage: apply-slim.sh <FFmpeg-Builds checkout>}"
cd "$root"

# What Squish actually runs (see src/Core/Optimizers/VideoOptimiser*.cs):
#   libx264 / libx265 / libsvtav1 / libvpx-vp9   software video encoders
#   h264|hevc|av1 _nvenc / _amf / _qsv           hardware encoders (ffnvcodec headers, AMF headers, libvpl)
#   libopus + native aac                          audio encoders
#   libwebp (libwebp_anim)                        animated WebP output
#   dav1d                                         fast AV1 decode (also used by the quality search)
# libiconv and zlib are listed because the base layers and several FFmpeg components expect them.
KEEP=(libiconv zlib amf dav1d ffnvcodec libopus libvpx libwebp onevpl svtav1 x264 x265)

for name in "${KEEP[@]}"; do
  compgen -G "scripts.d/??-${name}.sh" >/dev/null || compgen -G "scripts.d/??-${name}" >/dev/null \
    || { echo "KEEP entry '$name' has no script in scripts.d - upstream renamed it? Update KEEP." >&2; exit 1; }
done

final=scripts.d/zz-final.sh
grep -q '^ffbuild_depends() {' "$final" || { echo "$final has no ffbuild_depends - upstream layout changed." >&2; exit 1; }

# Drop every "    echo <dep>" line, then add the allowlist right after the function header.
sed -i '/^    echo /d' "$final"
deps_file="$(mktemp)"
printf '    echo %s\n' "${KEEP[@]}" >"$deps_file"
sed -i "/^ffbuild_depends() {/r $deps_file" "$final"
rm -f "$deps_file"

cat > variants/win64-slim-shared.sh <<'VARIANT'
#!/bin/bash
source "$(dirname "$BASH_SOURCE")"/win64-gpl-shared.sh
# avdevice (webcam / screen capture / audio out) and the network stack are never used: Squish reads local files and
# talks to ffmpeg over pipes. Both are linked into the DLLs, so switching them off is free size.
FF_CONFIGURE+=" --disable-avdevice --disable-network --disable-ffplay"
VARIANT

echo "Slim recipe applied. Kept: ${KEEP[*]}"
