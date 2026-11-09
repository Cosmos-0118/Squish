# Slim FFmpeg build

`tools/ffmpeg` is the largest thing Squish ships: about 230 MB of the 250 MB `tools/` folder, with the gyan.dev
`full_build-shared` archive that `tools/tools-manifest.json` points at today. Almost all of it is code Squish never calls.

## Where the bytes go

Measured on the staged 9.0.2 `full_build-shared` DLLs:

| File | Size | Squish uses | Bulk of the rest |
| :-- | --: | :-- | :-- |
| `avfilter-12.dll` | 102 MB | `mpdecimate`, `scale`, `fps`, `atempo`, `loudnorm`, `palettegen`, `paletteuse`, `select`, `split` (about 2 MB) | whisper.cpp with ggml Vulkan/OpenCL shaders, Flite voice banks, libplacebo, VMAF |
| `avcodec-63.dll` | 94 MB | libx264, libx265, libsvtav1, libvpx-vp9, libopus, native AAC, hardware wrappers (about 38 MB) | librav1e, libaom, vvenc, xeve/xevd, xavs2/davs2/uavs3d, SVT-JPEG-XS, libjxl |
| `avformat-63.dll` | 22 MB | local file and pipe I/O, MP4/MKV/WebM/GIF mux (about 5 MB) | gnutls, srt, rist, ssh, zmq, libbluray, libdvdnav, libopenmpt |
| `avdevice-63.dll` | 6 MB | nothing | capture and playback devices |

Every library is linked into these DLLs, so there is nothing to strip afterwards; the only fix is a build that leaves
them out.

Checked while preparing this: BtbN's current `master` recipe has whisper switched off (`50-whisper.sh` returns
"not enabled"), so their prebuilt `win64-gpl-shared` zip does not carry the whisper bloat, but it still carries
librav1e, vvenc, libaom, libplacebo/Vulkan and the network stack, and is not much smaller than the gyan build.

## The slim recipe

`scripts/ffmpeg-slim/apply-slim.sh` takes a checkout of [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds)
and changes only which libraries are built:

- keeps libx264, libx265 (8/10/12-bit), libsvtav1, libvpx, libopus, libwebp, dav1d, and the NVENC (`ffnvcodec`), AMF
  and QSV (`libvpl`) paths;
- adds `--disable-avdevice --disable-network --disable-ffplay`;
- fails loudly if upstream renamed a script it expects.

Dry-running BtbN's `generate.sh win64 slim-shared 9.0` against the patched tree (done when this was written) produces
this configure line and 14 build stages:

```
--enable-gpl --enable-version3 --disable-debug --enable-shared --disable-static --disable-avdevice --disable-network
--disable-ffplay --disable-w32threads --enable-pthreads --enable-iconv --enable-zlib --enable-amf --enable-libdav1d
--enable-ffnvcodec --enable-cuda-llvm --enable-libopus --enable-libvpx --enable-libwebp --enable-libvpl
--enable-libsvtav1 --enable-libx264 --enable-libx265
```

If Squish starts using another library (burned-in subtitles need libass, HDR tone mapping needs zimg, ...), add it to
`KEEP` in the script.

## Building and publishing

Run **build-ffmpeg-slim** from the Actions tab (`workflow_dispatch`). It pins the BtbN commit, builds the toolchain
image and FFmpeg in Docker (expect one to a few hours the first time), then a Windows job checks that:

- the encoders (software and NVENC/AMF/QSV wrappers) and filters Squish uses are present;
- libx264, libx265, libsvtav1, libvpx-vp9, libopus and libwebp_anim actually encode;
- `ffprobe` reads the result;
- the unpacked size is under 90 MB.

Tick **publish** and give a `release_tag` to attach the zip to a GitHub release. The job summary prints the SHA256.

## Switching the app to it

Replace the `ffmpeg` entry of `tools/tools-manifest.json`:

```json
{
  "name": "ffmpeg",
  "version": "9.0.2",
  "url": "https://github.com/<owner>/<repo>/releases/download/<release_tag>/<archive>.zip",
  "sha256": "<sha256 from the job summary>",
  "archiveType": "zip",
  "destination": "ffmpeg",
  "include": ["ffmpeg.exe", "ffprobe.exe", "*.dll", "LICENSE.txt", "SOURCE.txt"]
}
```

Notes:

- Remove the `updateSource` block, or `scripts/check-tool-updates.ps1` will keep proposing the gyan.dev release.
  Updating becomes a manual workflow run.
- The build is GPL: keep `SOURCE.txt` (shipped inside the archive) and mention the source in
  `THIRD_PARTY_NOTICES.md` when you switch.
- Nothing in the app changes: `ExternalToolResolver` still finds `tools/ffmpeg/ffmpeg.exe` and `ffprobe.exe`, and
  `VideoHardwareDetector` keeps probing the encoder list.

## Why not the other options

- **gyan.dev essentials, no ffprobe:** about 74 MB with no build step, but it needs `VideoMetadataProbe` rewritten
  around `ffmpeg -i` text output, and its encoder list (notably `libsvtav1`) has not been verified, which decides
  whether the CPU AV1 path survives.
- **BtbN prebuilt:** see above; roughly the same size as what ships now.
