# Third-Party Notices

Clop for Windows is distributed under the GNU General Public License v3.0 (see `LICENSE`).
To comply with GPL/AGPL/LGPL requirements for bundled dependencies, the following components
and licenses are shipped within the installer and portable packages.

| Component   | Version / Source                                                                                     | License            | Notes                                                                                                   |
| ----------- | ---------------------------------------------------------------------------------------------------- | ------------------ | ------------------------------------------------------------------------------------------------------- |
| ffmpeg      | https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip    | GPL/LGPL           | Audio/video transcoder used for GIF/video pipelines and ffprobe metadata probes.                        |
| mozjpeg     | https://github.com/garyzyg/mozjpeg-windows/releases                                                  | IJG/BSD            | `cjpeg-static.exe` build of mozjpeg used for high-quality JPEG output when advanced codecs are enabled. |
| libwebp     | https://storage.googleapis.com/downloads.webmproject.org/releases/webp/libwebp-1.4.0-windows-x64.zip | BSD-3              | Provides `cwebp.exe` + DLLs for WebP export and animated WebP replacements.                             |
| libavif     | https://github.com/AOMediaCodec/libavif/releases/download/v1.3.0/windows-artifacts.zip               | BSD-2              | Supplies `avifenc.exe` for AVIF output in the advanced codec pipeline.                                  |
| Ghostscript | https://github.com/ArtifexSoftware/ghostpdl-downloads                                                | AGPL-3.0           | PDF optimisation and rasterisation backend. Commercial licensing available from Artifex.                |
| qpdf        | https://github.com/qpdf/qpdf/releases                                                                | Apache-2           | Used for optional PDF linearisation before Ghostscript.                                                 |
| LibreOffice | https://download.documentfoundation.org/libreoffice/stable/25.2.7/win/x86_64/                        | MPL 2.0 / LGPL v3+ | Provides headless `soffice` CLI used to convert Office documents to PDF before optimisation.            |

Each tool’s original LICENSE/NOTICE text is placed alongside the binary under `tools/<name>/` inside the installer payload. Refer to those files for the exact terms required by each upstream author.
