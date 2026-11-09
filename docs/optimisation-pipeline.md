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
2. One qpdf rewrite of the source, `--recompress-flate --compression-level=9 --object-streams=generate` (objects nothing points to, including old incremental-update revisions, are garbage collected; a linearised input stays linearised). The structure is read once with `--json-output --json-stream-data=none`; there is no separate "structural" pass whose output the picture pass would unpack and deflate again. With "Strip metadata" the information dictionary, XMP metadata and page thumbnails are dropped, and so is `/PieceInfo` and per-object XMP on the catalog, pages, form XObjects and pictures.
3. Pictures, through qpdf's JSON interface (`--json-output --json-stream-data=file --decode-level=generalized`, object selectors passed in an `@` argument file, then `--update-from-json`). Only pictures reachable from the trailer are looked at; pictures are optimised on up to three workers, and identical pictures (same bytes, dictionary and placement size) are optimised once.
   - DCT (JPEG) pictures: lossless `jpegtran` recompression first, then the same perceptual re-encode as stand-alone images (SSIMULACRA2 at least 74 normal, 68 aggressive). `/Filter [/DCTDecode]`, an explicit default `/DecodeParms << /ColorTransform 1 >>` and an identity `/Decode` array are read like their plain forms.
   - 8-bit RGB/Gray Flate bitmaps (including ICC-based, `/CalRGB` and `/CalGray`) that the content analyser calls photographic become JPEG. Everything else (screenshots, charts, logos) goes through the PNG optimisers (oxipng, and pngquant under the perceptual guard) and the result is carried into the PDF as it is: the PNG's IDAT stream is exactly a `/FlateDecode` stream with `/Predictor 15`, and a palette becomes an `/Indexed` colour space on top of the original one. Grey pictures stay single-component (`jpegtran -grayscale`).
   - 8-bit soft masks (`/SMask`) stay lossless but shrink with their picture and are stored with PNG row filters.
   - Resolution: how large each picture is drawn comes from a small content-stream interpreter (`q`/`Q`/`cm`/`Do`, nested forms, rotation, page `/UserUnit`). Pictures drawn at more than 220 dpi (normal) or 150 dpi (aggressive) are downsampled to that, only when it removes at least 15% of the linear resolution. A picture that is also used somewhere the scan cannot see (annotation appearances, patterns) or that is clipped by a group is never resampled.
   - Left untouched: CCITT/JBIG2/JPX, CMYK, indexed and 16-bit data, 1-bit scans, image masks, colour-key `/Mask` arrays, non-identity `/Decode` arrays, soft masks with `/Matte` or shared by several pictures, JPEGs with an EXIF rotation or a non-default `/DecodeParms`, inline images (`BI ... EI`).
4. qpdf's `--recompress-flate` decodes and re-deflates every Flate stream, and drops PNG/TIFF predictors while doing so. When the file has predictor-coded streams (already, or written by step 3) it is written both ways at the same time and the smaller file wins.
5. The result is checked with `qpdf --check` and only replaces the original when it is at least the configured minimum smaller.

## Documents (Office, OpenDocument, EPUB)

`DocumentOptimiser` is entirely in-process (`System.IO.Compression`, `System.Xml`, the image optimiser); it adds no binaries. Handles `.xlsx .xlsm .docx .docm .pptx .pptm .ods .odt .odp .epub`, recognised by content (`[Content_Types].xml`, `META-INF/manifest.xml`, `META-INF/container.xml`), not extension.

1. Skipped untouched: OLE2 files (legacy `.doc/.xls/.ppt`, password-protected files), digitally signed or encrypted packages, DRM EPUBs, and Office lock files (`~$name.docx`).
2. Media parts (`word|xl|ppt/media/`) that no relationship points to are dropped, and parts with the same bytes in the same folder are merged into one that all their relationships point to (structural, so it also happens with pictures switched off). The remaining embedded pictures (`word|xl|ppt/media/`, ODF `Pictures/`, EPUB images) are extracted, de-duplicated by SHA-256 and run through the image pipeline in their own format on up to three workers, so file names and relationships do not change.
   - Office files, both modes: pictures drawn at over ~259 dpi (normal, target 220) or ~225 dpi (aggressive, target 150) are downsampled to the target. The display size is read from the drawing parts: pictures, stretched picture fills of shapes, slide/layout/master backgrounds (against `sldSz`) and pictures inside groups (through the group's extent / child extent), with crop and rotation. Tiled fills, rotated groups, diagram drawings and bullets count as unknown and stay at full resolution.
   - Aggressive mode (Office files): opaque photographic PNG/BMP/TIFF become JPEG, with `_rels/*.rels` and `[Content_Types].xml` updated.
3. Structure: all OOXML parts, ODF `meta/settings/manifest` and EPUB package files are re-written without indentation or comments (whitespace inside text runs is preserved). Excel: empty cells whose format draws nothing are removed, and so are rows left empty whose only attributes are a style that draws nothing and the sheet's default height (hidden, grouped and resized rows stay), `<dimension>` is corrected in the same pass that cleans the sheet, `xl/calcChain.xml` is dropped with its relationship and content type. Aggressive mode also drops formatted blanks, custom heights and fills of rows below the last row with data (merged ranges and auto-filters count as data; a sheet with no data at all is a template and is left alone).
4. "Strip metadata": author, last-modified-by, revision, company and manager are removed; embedded thumbnails are dropped.
5. Repacked at maximum deflate (`mimetype` first and stored for ODF/EPUB, even when the source had it elsewhere). The package is re-validated (zip integrity, every XML part parses, no new dangling relationship or content-type override) and only replaces the original when it is meaningfully smaller.

## Tools

`scripts/fetch-tools.ps1` (PowerShell 7) downloads the pinned tools from `tools/tools-manifest.json` and fails if a SHA-256 is missing or does not match.
