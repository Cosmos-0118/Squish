# Optimisation pipeline

## Images

1. Decode safely with Magick.NET (magic-byte sniffing, explicit format, resource limits).
2. Lossless pass first: `jpegtran -optimize -progressive` for JPEG, `oxipng` for PNG.
3. Lossy pass guarded by a perceptual score (SSIMULACRA2, built in). The quality is bisected on sampled tiles until the score is at the threshold (normal 74, aggressive 68; the score is relative to the decoded source, so already-compressed downloads need the lower normal target to shrink meaningfully). Encoders: mozjpeg `cjpeg`, `pngquant` (256-colour palette; PNGs that are already indexed are only re-quantised in aggressive mode), optional WebP/AVIF conversion (`cwebp`, `avifenc`).
   - The search runs on a mosaic of sampled tiles, but the shipped file is verified on every pixel (1024 px tiles covering the frame). Small sampled tiles miss low-frequency error (chroma re-subsampling, palette banding), which SSIMULACRA2 only sees at its coarse scales. When verification fails, the tile search is re-run against a target raised by the measured bias.
   - PNG palettes are searched and judged on every pixel for the same reason; probes run concurrently.
   - JPEG encoders: every chroma layout is searched with both mozjpeg and jpegli (`cjpegli`, adaptive quantisation off) and the smallest passing file wins. jpegli is 5-15% smaller on most photographs and faster; mozjpeg still wins on some.
   - JPEG chroma: photos whose source is already 4:2:0 are re-encoded 4:2:0. Sources that kept more chroma (4:4:4, 4:2:2, PNG/HEIC conversions) are searched at both 4:2:0 and 4:4:4, and the smaller passing result wins. Re-subsampling those often cannot reach the target at any quality.
   - GIF: `gifsicle -O3` first, then a lossy search over `--lossy` levels (8-200). Eight evenly spaced frames from the first 72 are composited and scored with SSIMULACRA2 against the original, matched by animation time because gifsicle merges identical neighbouring frames; the strongest level whose mean stays above the target ships. The header is walked by hand, so GIFs with more frames than ImageMagick will hold in a list are still handled.
   - Conversions (WebP/AVIF/HEIC/BMP to JPEG, TIFF to PNG) only ship when the result is smaller than the source. WebP and AVIF that would grow as JPEG are re-encoded in their own format, and kept as they are when that is not smaller either.
4. Metadata: EXIF/GPS/XMP/IPTC stripped by default; non-sRGB ICC profiles and orientation are kept. A Squish marker prevents repeated re-compression.
5. When a tool is missing, built-in Magick.NET encoders are used as a fallback.

## Video

- ffmpeg software encoders. SVT-AV1 (10-bit, in MP4/MKV) is the default: at equal SSIMULACRA2 it is 40-60% smaller than x265 and the GPU encoders at about x265's speed. Falls back to x265 (tagged `hvc1`) when ffmpeg has no libsvtav1; the CPU encoder preset keeps x265/x264 for compatibility, and a GPU preset keeps hardware HEVC. VP9 stays for WebM sources.
- CRF is found by a secant search on sampled footage scored with SSIMULACRA2 (normal 74, aggressive 64), with a bitrate cap for grainy sources. The cap may lower the sampled score by at most 10 points below the target (`QualitySearchCapAllowance`); past that the source counts as already efficient and is kept.
- GPU encoders (AMF, NVENC, QSV) go through the same search over their constant-quality quantiser (`-rc cqp` on AMF, `-rc vbr -cq` on NVENC) instead of a fixed fraction of the source bitrate, which cannot tell grainy footage from clean footage. Samples are decoded in software and encoded with the same arguments as the final run.
- A re-encode that keeps the codec never replaces the original with a larger file, even when the container changes.
- Frame rate is preserved unless a cap is configured, audio is copied when possible, `+faststart` is applied to MP4/MOV, and 10-bit sources keep 10-bit.

## PDF

`PdfOptimiser` shells out to `qpdf` (Apache-2.0, `qpdf.exe` + `qpdf30.dll`, about 7 MB) and never rewrites text, fonts, forms, annotations or bookmarks.

1. Skipped untouched: digitally signed PDFs (a `/ByteRange` anywhere in the file) and encrypted PDFs.
2. Structural pass: `qpdf --recompress-flate --compression-level=9 --object-streams=generate` (unreferenced objects and old incremental-update revisions are garbage collected, a linearised input stays linearised). With "Strip metadata" the information dictionary, XMP metadata, page thumbnails and `/PieceInfo` are dropped too.
3. Picture pass, through qpdf's JSON interface (`--json-output --json-stream-data=file --decode-level=generalized`, then `--update-from-json`):
   - DCT (JPEG) pictures: lossless `jpegtran` recompression first, then the same perceptual re-encode as stand-alone images (SSIMULACRA2 at least 74 normal, 68 aggressive).
   - 8-bit RGB/Gray Flate bitmaps (including ICC-based) that the content analyser calls photographic become JPEG; graphics stay lossless. Grey pictures stay single-component (`jpegtran -grayscale`).
   - Resolution: how large each picture is drawn comes from a small content-stream interpreter (`q`/`Q`/`cm`/`Do`, nested forms, rotation). Pictures drawn at more than 220 dpi (normal) or 150 dpi (aggressive) are downsampled to that, only when it removes at least 15% of the linear resolution. A picture that is also used somewhere the scan cannot see (annotation appearances, patterns) or that is clipped by a group is never resampled.
   - Left untouched: CCITT/JBIG2/JPX, CMYK, indexed and 16-bit data, 1-bit scans, image masks, colour-key `/Mask` arrays, `/Decode` arrays, pictures that are themselves soft masks, JPEGs with an EXIF rotation or `/DecodeParms`.
4. The result is checked with `qpdf --check` and only replaces the original when it is at least the configured minimum smaller.

## Documents (Office, OpenDocument, EPUB)

`DocumentOptimiser` is entirely in-process (`System.IO.Compression`, `System.Xml`, the image optimiser); it adds no binaries. Handles `.xlsx .xlsm .docx .docm .pptx .pptm .ods .odt .odp .epub`, recognised by content (`[Content_Types].xml`, `META-INF/manifest.xml`, `META-INF/container.xml`), not extension.

1. Skipped untouched: OLE2 files (legacy `.doc/.xls/.ppt`, password-protected files), digitally signed or encrypted packages, DRM EPUBs, and Office lock files (`~$name.docx`).
2. Embedded pictures (`word|xl|ppt/media/`, ODF `Pictures/`, EPUB images) are extracted, de-duplicated by SHA-256 and run through the image pipeline in their own format, so file names and relationships do not change.
   - Aggressive mode (Office files): pictures drawn at over ~225 dpi are downsampled to 150 dpi (display size read from the drawing parts, with crop and rotation; unknown or grouped placements are skipped), and opaque photographic PNG/BMP/TIFF become JPEG, with `_rels/*.rels` and `[Content_Types].xml` updated.
3. Structure: all OOXML parts, ODF `meta/settings/manifest` and EPUB package files are re-written without indentation or comments (whitespace inside text runs is preserved). Excel: empty cells whose format draws nothing (and rows left empty) are removed, `<dimension>` is corrected, `xl/calcChain.xml` is dropped with its relationship and content type. Aggressive mode also drops formatted blanks below the last row with data.
4. "Strip metadata": author, last-modified-by, revision, company and manager are removed; embedded thumbnails are dropped.
5. Repacked at maximum deflate (`mimetype` first and stored for ODF/EPUB). The package is re-validated (zip integrity, every XML part parses, no new dangling relationship or content-type override) and only replaces the original when it is meaningfully smaller.

## Tools

`scripts/fetch-tools.ps1` (PowerShell 7) downloads the pinned tools from `tools/tools-manifest.json` and fails if a SHA-256 is missing or does not match.
