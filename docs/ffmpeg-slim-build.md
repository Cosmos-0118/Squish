# Slim FFmpeg build

Squish builds its own FFmpeg. The public Windows builds (gyan.dev, BtbN) link in far more than Squish ever calls, so
`tools/ffmpeg` was about 230 MB of the 250 MB `tools/` folder. The build in `scripts/ffmpeg-slim` is about **74 MB
unpacked (26 MB zipped)** with the same encoders and filters Squish uses.

## Where the bytes went

Measured on the gyan.dev 9.0.2 `full_build-shared` DLLs that used to be staged:

| File | Size | Squish uses | Bulk of the rest |
| :-- | --: | :-- | :-- |
| `avfilter-12.dll` | 102 MB | `mpdecimate`, `scale`, `fps`, `atempo`, `loudnorm`, `palettegen`, `paletteuse`, `select`, `split` (about 2 MB) | whisper.cpp with ggml Vulkan/OpenCL shaders, Flite voice banks, libplacebo, VMAF |
| `avcodec-63.dll` | 94 MB | libx264, libx265, libsvtav1, libvpx-vp9, libopus, native AAC, hardware wrappers (about 38 MB) | librav1e, libaom, vvenc, xeve/xevd, xavs2/davs2/uavs3d, SVT-JPEG-XS, libjxl |
| `avformat-63.dll` | 22 MB | local file and pipe I/O, MP4/MKV/WebM/GIF mux (about 5 MB) | gnutls, srt, rist, ssh, zmq, libbluray, libdvdnav, libopenmpt |
| `avdevice-63.dll` | 6 MB | only the `lavfi` input (used by the tests) | webcam, screen and audio capture and playback devices |

Every library is linked into these DLLs, so nothing can be stripped afterwards; the fix is a build that leaves them out.
(BtbN's current recipe has whisper switched off, but their prebuilt zip still carries rav1e, vvenc, libaom, Vulkan and the
network stack, and is not much smaller.)

## How it is built

`tools/tools-manifest.json` has no download URL for ffmpeg. Its entry is `"archiveType": "msys2-build"`, and
`scripts/fetch-tools.ps1` runs `scripts/ffmpeg-slim/build-local.sh` under MSYS2 (UCRT64), which:

- compiles FFmpeg from the source tag in the manifest (`build.tag`);
- enables libx264, libx265, libsvtav1, libvpx, libopus, libwebp, dav1d, and the NVENC (`ffnvcodec`), AMF and QSV
  (`libvpl`) hardware paths;
- disables capture/playback devices, the network stack, ffplay, and any stray MSYS2 library that configure would
  otherwise pick up (SDL2, lzma, bzip2, schannel, libmfx, VA-API);
- keeps the `lavfi` input. It lives in libavdevice, and the test suite builds its fixtures with `-f lavfi`. A first
  version of the recipe used `--disable-avdevice`, and running the tests against a local build showed that breaks them;
- bundles the codec DLLs MSYS2 provides (the build is dynamically linked), plus `LICENSE.txt` and `SOURCE.txt`.

`libx265` is 22 MB of the 74 MB because MSYS2's build holds the 8, 10 and 12-bit encoders; SVT-AV1 is 8 MB.

`.toolinfo.json` records a hash of every file in `scripts/ffmpeg-slim/`, so changing the script or the package list
rebuilds; otherwise a staged `tools/ffmpeg` is reused like any other tool.

### In CI

`ci`, `release` and `tool-updates` restore the `tools` cache first (its key includes the manifest, `fetch-tools.ps1`
and `scripts/ffmpeg-slim/*`). Only on a cache miss do they run `.github/actions/prepare-ffmpeg-build`, which installs
MSYS2 and the packages in `scripts/ffmpeg-slim/msys2-packages.txt`, and then `fetch-tools.ps1` builds ffmpeg together
with the other tools. Nothing is uploaded or hosted anywhere, and the tests run against the exact binaries that get
released. A GitHub cache entry is evicted after 7 days without use, so a rebuild with newer MSYS2 packages can happen on
any run; the tests catch a breaking change before it ships.

### Locally

```powershell
# one-time: MSYS2 installed in C:\msys64 (or set MSYS2_ROOT), fully upgraded, with the packages installed:
#   pacman -Syu     (twice, if it asks to close)
#   grep -v -E '^\s*(#|$)' scripts/ffmpeg-slim/msys2-packages.txt | xargs pacman -S --needed --noconfirm
pwsh scripts/fetch-tools.ps1 -Name ffmpeg      # builds into tools/ffmpeg (several minutes the first time)
pwsh scripts/fetch-tools.ps1 -Name ffmpeg -Force
```

Without MSYS2, run `fetch-tools.ps1 -Exclude ffmpeg` and point `SQUISH_FFMPEG` / `SQUISH_FFPROBE` at any ffmpeg.

## Updating FFmpeg

Bump `version` and `build.tag` together in the manifest (for example `9.0.3` / `n9.0.3`), then run the tests. There is
no `updateSource`, so `tool-updates` does not propose ffmpeg changes. If Squish starts using another library (burned-in
subtitles need libass, HDR tone mapping needs zimg, ...), add its MSYS2 package to `msys2-packages.txt` and its
`--enable-*` flag to `build-local.sh`.

## Licence

The result is GPL (x264, x265). `SOURCE.txt` inside `tools/ffmpeg` names the FFmpeg tag and the recipe, and
`THIRD_PARTY_NOTICES.md` links the sources; the codec DLLs are the MSYS2 mingw-w64 packages of the same names.
