# Third-Party Notices

Clop for Windows is distributed under the GNU General Public License v3.0 (see `LICENSE`).
To comply with GPL/AGPL/LGPL requirements for bundled dependencies, the following components
and licenses are shipped within the installer and portable packages.

| Component   | Version / Source                                                 | License  | Notes                                                                                                          |
| ----------- | ---------------------------------------------------------------- | -------- | -------------------------------------------------------------------------------------------------------------- |
| pngquant    | https://pngquant.org/pngquant-windows.zip                        | GPL-3.0  | Lossy PNG compressor used for raster optimisation. Commercial license required to embed in proprietary builds. |
| ffmpeg      | https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip | GPL/LGPL | Audio/video transcoder used for GIF/video pipelines. Distribution follows GPL build terms.                     |
| gifski      | https://github.com/ImageOptim/gifski/releases                    | AGPL-3.0 | High-quality GIF encoder. Requires source disclosure or commercial license from ImageOptim.                    |
| libvips     | https://github.com/libvips/build-win64-mxe/releases              | LGPL-2.1 | `vipsthumbnail.exe` and DLL set for smart cropping.                                                            |
| Ghostscript | https://github.com/ArtifexSoftware/ghostpdl-downloads            | AGPL-3.0 | PDF optimisation and rasterisation backend. Commercial licensing available from Artifex.                       |
| ExifTool    | https://exiftool.org/                                            | GPL-3.0  | Metadata inspection/manipulation. Bundled as Phil Harvey's Windows package.                                    |

Each tool’s original LICENSE/NOTICE text is placed alongside the binary under `tools/<name>/` inside the installer payload. Refer to those files for the exact terms required by each upstream author.
