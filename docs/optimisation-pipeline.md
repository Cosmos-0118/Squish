# Optimisation pipeline

## Images

1. Decode safely with Magick.NET (magic-byte sniffing, explicit format, resource limits).
2. Lossless pass first: `jpegtran -optimize -progressive` for JPEG, `oxipng` for PNG.
3. Lossy pass guarded by a perceptual score (SSIMULACRA2, built in). The quality is bisected on sampled tiles until the score is at the threshold (normal 80, aggressive 68). Encoders: mozjpeg `cjpeg`, `pngquant` (256-colour palette; PNGs that are already indexed are only re-quantised in aggressive mode), optional WebP/AVIF conversion (`cwebp`, `avifenc`).
   - The search runs on a mosaic of sampled tiles, but the shipped file is verified on every pixel (1024 px tiles covering the frame). Small sampled tiles miss low-frequency error (chroma re-subsampling, palette banding), which SSIMULACRA2 only sees at its coarse scales. When verification fails, the tile search is re-run against a target raised by the measured bias.
   - PNG palettes are searched and judged on every pixel for the same reason; probes run concurrently.
   - JPEG encoders: every chroma layout is searched with both mozjpeg and jpegli (`cjpegli`, adaptive quantisation off) and the smallest passing file wins. jpegli is 5-15% smaller on most photographs and faster; mozjpeg still wins on some.
   - JPEG chroma: photos whose source is already 4:2:0 are re-encoded 4:2:0. Sources that kept more chroma (4:4:4, 4:2:2, PNG/HEIC conversions) are searched at both 4:2:0 and 4:4:4, and the smaller passing result wins. Re-subsampling those often cannot reach the target at any quality.
4. Metadata: EXIF/GPS/XMP/IPTC stripped by default; non-sRGB ICC profiles and orientation are kept. A Squish marker prevents repeated re-compression.
5. When a tool is missing, built-in Magick.NET encoders are used as a fallback.

## Video

- ffmpeg software encoders. SVT-AV1 (10-bit, in MP4/MKV) is the default: at equal SSIMULACRA2 it is 40-60% smaller than x265 and the GPU encoders at about x265's speed. Falls back to x265 (tagged `hvc1`) when ffmpeg has no libsvtav1; the CPU encoder preset keeps x265/x264 for compatibility, and a GPU preset keeps hardware HEVC. VP9 stays for WebM sources.
- CRF is found by a secant search on sampled footage scored with SSIMULACRA2 (normal 74, aggressive 64), with a bitrate cap for grainy sources. The cap may lower the sampled score by at most 10 points below the target (`QualitySearchCapAllowance`); past that the source counts as already efficient and is kept.
- GPU encoders (AMF, NVENC, QSV) go through the same search over their constant-quality quantiser (`-rc cqp` on AMF, `-rc vbr -cq` on NVENC) instead of a fixed fraction of the source bitrate, which cannot tell grainy footage from clean footage. Samples are decoded in software and encoded with the same arguments as the final run.
- A re-encode that keeps the codec never replaces the original with a larger file, even when the container changes.
- Frame rate is preserved unless a cap is configured, audio is copied when possible, `+faststart` is applied to MP4/MOV, and 10-bit sources keep 10-bit.

## Tools

`scripts/fetch-tools.ps1` (PowerShell 7) downloads the pinned tools from `tools/tools-manifest.json` and fails if a SHA-256 is missing or does not match.
