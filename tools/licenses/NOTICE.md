# Bundled Tool Licenses

This folder lists the upstream licenses for the third-party utilities that
`scripts/fetch-tools.ps1` downloads into `tools/<name>/`. The binaries
are redistributed verbatim from their original publishers; no source
changes are applied.

| Component               | Upstream URL                         | License                                       | Notes                                                                     |
| ----------------------- | ------------------------------------ | --------------------------------------------- | ------------------------------------------------------------------------- |
| pngquant                | https://pngquant.org/                | GNU GPL v3.0                                  | Commercial license required if you do not comply with GPL/AGPL terms.     |
| ffmpeg                  | https://ffmpeg.org/                  | GNU GPL/LGPL (depending on configured codecs) | The Windows “release essentials” package from gyan.dev contains GPL code. |
| gifski                  | https://github.com/ImageOptim/gifski | GNU AGPL v3.0                                 | ImageOptim offers commercial relicensing.                                 |
| libvips (vipsthumbnail) | https://libvips.github.io/libvips/   | LGPL v2.1                                     | Re-linking permitted so long as LGPL terms are met.                       |
| Ghostscript             | https://www.ghostscript.com/         | GNU AGPL v3.0                                 | Commercial licenses available from Artifex.                               |
| ExifTool                | https://exiftool.org/                | GNU GPL v3.0                                  | Authored by Phil Harvey; do not remove attribution.                       |

Each entry below reproduces the text required by the upstream license.

---

## pngquant — GNU GPL v3.0

The pngquant Windows package is distributed under the GNU General Public
License version 3.0. See the top-level `LICENSE` file for the full text or
visit <https://www.gnu.org/licenses/gpl-3.0.txt>.

---

## ffmpeg — GNU General Public License / Lesser GPL

FFmpeg is licensed under the GNU Lesser General Public License (LGPL) version 2.1
or later. However, some configurations enable optional GPL components which bring
the entire binary under the GNU General Public License version 2 or later. The
"release-essentials" ZIP from gyan.dev includes GPL code. Consult
<https://ffmpeg.org/legal.html> for the authoritative terms.

---

## gifski — GNU Affero General Public License v3.0

Gifski is licensed under the GNU AGPL v3.0. Full text available at
<https://www.gnu.org/licenses/agpl-3.0.txt>. Commercial licensing is available
from ImageOptim (<https://gif.ski/>).

---

## libvips / vipsthumbnail — GNU Lesser General Public License v2.1

Libvips (including the `vipsthumbnail` utility) is licensed under the LGPL v2.1.
The license text is published at <https://www.gnu.org/licenses/old-licenses/lgpl-2.1.txt>.

---

## Ghostscript — GNU Affero General Public License v3.0

Ghostscript Community Edition is made available under the AGPL v3.0. Refer to
<https://www.gnu.org/licenses/agpl-3.0.txt>. Commercial licenses may be purchased
from Artifex (<https://artifex.com/licensing/>).

---

## ExifTool — GNU General Public License v3.0

ExifTool is licensed under the GNU GPL v3.0. Source code and documentation are
available at <https://exiftool.org/>. Redistributions must retain the copyright
notice and this license statement.
