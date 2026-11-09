# Third-Party Notices

Squish is distributed under the GNU General Public License v3.0 (see `LICENSE`).

Squish is derived from [Clop for Mac](https://github.com/FuzzyIdeas/Clop) by The Low Tech Guys,
which is also licensed under GPLv3. Squish is not affiliated with or endorsed by them, and does not
use the Clop name, logo, or artwork.
To comply with GPL/AGPL/LGPL requirements for bundled dependencies, the following components
and licenses are shipped within the installer and portable packages.

| Component   | Version / Source                                                                                     | License            | Notes                                                                                                   |
| ----------- | ---------------------------------------------------------------------------------------------------- | ------------------ | ------------------------------------------------------------------------------------------------------- |
| ffmpeg      | Built from the FFmpeg source tag in `tools/tools-manifest.json` (https://github.com/FFmpeg/FFmpeg) with `scripts/ffmpeg-slim`; codec DLLs from MSYS2 mingw-w64 packages (x264, x265, SVT-AV1, libvpx, dav1d, opus, libwebp) | GPL (v3)           | Audio/video transcoder used for GIF/video pipelines and ffprobe metadata probes. `tools/ffmpeg/SOURCE.txt` names the exact source and recipe. |
| mozjpeg     | https://github.com/garyzyg/mozjpeg-windows/releases                                                  | IJG/BSD            | `cjpeg-static.exe` build of mozjpeg used for high-quality JPEG output when advanced codecs are enabled. |
| libwebp     | https://storage.googleapis.com/downloads.webmproject.org/releases/webp/libwebp-1.4.0-windows-x64.zip | BSD-3              | Provides `cwebp.exe` + DLLs for WebP export and animated WebP replacements.                             |
| libavif     | https://github.com/AOMediaCodec/libavif/releases/download/v1.3.0/windows-artifacts.zip               | BSD-2              | Supplies `avifenc.exe` for AVIF output in the advanced codec pipeline.                                  |
| qpdf        | https://github.com/qpdf/qpdf/releases/download/v12.4.2/qpdf-12.4.2-msvc64.zip                        | Apache-2.0         | `qpdf.exe` + `qpdf30.dll` only; structural PDF recompression and image-stream rewriting for PDF optimisation. |

Each tool’s original LICENSE/NOTICE text is placed alongside the binary under `tools/<name>/` inside the installer payload. Refer to those files for the exact terms required by each upstream author.
