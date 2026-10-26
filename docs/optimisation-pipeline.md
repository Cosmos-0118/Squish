# Optimisation pipeline

## Images

1. Decode safely with Magick.NET (magic-byte sniffing, explicit format, resource limits).
2. Lossless pass first: `jpegtran -optimize -progressive` for JPEG, `oxipng` for PNG.
3. Lossy pass guarded by a perceptual score (SSIMULACRA2, built in). The quality is bisected on sampled tiles until the score is at the threshold (normal 80, aggressive 68). Encoders: mozjpeg `cjpeg`, `pngquant` (aggressive), optional WebP/AVIF conversion (`cwebp`, `avifenc`).
4. Metadata: EXIF/GPS/XMP/IPTC stripped by default; non-sRGB ICC profiles and orientation are kept. A Clop marker prevents repeated re-compression.
5. When a tool is missing, built-in Magick.NET encoders are used as a fallback.

## Video

- ffmpeg software encoders (x264, x265 tagged `hvc1`, SVT-AV1, VP9).
- CRF is found by a secant search on sampled footage scored with SSIMULACRA2 (normal 74, aggressive 64), with a bitrate cap for grainy sources.
- Frame rate is preserved unless a cap is configured, audio is copied when possible, `+faststart` is applied to MP4/MOV, and 10-bit sources keep 10-bit.

## Tools

`scripts/fetch-tools.ps1` (PowerShell 7) downloads the pinned tools from `tools/tools-manifest.json` and fails if a SHA-256 is missing or does not match.
