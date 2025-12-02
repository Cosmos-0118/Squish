# Bundled Tool Licenses

This folder lists the upstream licenses for the third-party utilities that
`scripts/fetch-tools.ps1` downloads into `tools/<name>/`. The binaries
are redistributed verbatim from their original publishers; no source
changes are applied.

| Component   | Upstream URL                             | License                                       | Notes                                                                                 |
| ----------- | ---------------------------------------- | --------------------------------------------- | ------------------------------------------------------------------------------------- |
| ffmpeg      | https://ffmpeg.org/                      | GNU GPL/LGPL (depending on configured codecs) | Bundled via BtbN's `ffmpeg-master-latest-win64-gpl` archive which enables GPL codecs. |
| mozjpeg     | https://github.com/mozilla/mozjpeg       | IJG / BSD-style                               | Windows build sourced from `garyzyg/mozjpeg-windows` (`mozjpeg-x64.zip`).             |
| libwebp     | https://developers.google.com/speed/webp | BSD 3-Clause                                  | Use Google's official `libwebp-1.4.0-windows-x64` distribution.                       |
| libavif     | https://github.com/AOMediaCodec/libavif  | BSD 2-Clause                                  | Alliance for Open Media's `windows-artifacts.zip` provides `avifenc.exe`.             |
| Ghostscript | https://www.ghostscript.com/             | GNU AGPL v3.0                                 | Commercial licenses available from Artifex.                                           |
| qpdf        | https://github.com/qpdf/qpdf             | Apache License 2.0                            | Official `qpdf-12.2.0-msvc64` archives include NOTICE + DLL dependencies.             |

Each entry below reproduces the text required by the upstream license.

---

## ffmpeg — GNU General Public License / Lesser GPL

FFmpeg is licensed under the GNU Lesser General Public License (LGPL) version 2.1
or later. However, some configurations enable optional GPL components which bring
the entire binary under the GNU General Public License version 2 or later. The
`ffmpeg-master-latest-win64-gpl` includes GPL code. Consult
<https://ffmpeg.org/legal.html> for the authoritative terms.

---

## mozjpeg — IJG / BSD-style License

mozjpeg inherits the Independent JPEG Group license with BSD-style terms. The
source is available at <https://github.com/mozilla/mozjpeg>. The Windows build
used here is an unmodified binary from <https://github.com/garyzyg/mozjpeg-windows>.
Redistributions must retain copyright notices from IJG, the Mozilla project,
and the libjpeg-turbo contributors.

---

## libwebp — BSD 3-Clause License

libwebp is distributed under the BSD 3-Clause license. The official Windows
prebuilt archives, including `cwebp.exe`, are published at
<https://developers.google.com/speed/webp/download>. Keep the LICENSE and NOTICE
files that ship with the archive.

---

## libavif — BSD 2-Clause License

libavif is licensed under the BSD 2-Clause license by the Alliance for Open Media.
Releases (including `windows-artifacts.zip`) are available at
<https://github.com/AOMediaCodec/libavif>. Redistributions must retain the
copyright notice and license text.

---

## Ghostscript — GNU Affero General Public License v3.0

Ghostscript Community Edition is made available under the AGPL v3.0. Refer to
<https://www.gnu.org/licenses/agpl-3.0.txt>. Commercial licenses may be purchased
from Artifex (<https://artifex.com/licensing/>).

---

## qpdf — Apache License 2.0

qpdf is licensed under the Apache License 2.0. Source code and Windows binaries
are published at <https://github.com/qpdf/qpdf>. Redistributions must include the
Apache 2.0 license text and the upstream NOTICE file.
