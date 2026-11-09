# Squish Third-Party Tool Drop Zone

Populate the subdirectories below with the binaries referenced in `docs/windows-deps.md` before running the installer build. Each folder should contain:

- The executable or library payload required by Squish.
- A `LICENSE.txt` (or equivalent) mirroring the upstream license.
- A `SHA256SUMS.txt` entry that records the checksum of every binary you place here.

## Automated download helper

Use `scripts/fetch-tools.ps1` to retrieve the vetted binaries listed in `tools-manifest.json`:

```powershell
pwsh scripts/fetch-tools.ps1               # download everything
pwsh scripts/fetch-tools.ps1 -ListOnly     # show current status
pwsh scripts/fetch-tools.ps1 -Name ffmpeg  # download a single tool
pwsh scripts/fetch-tools.ps1 -WriteMissingChecksums # auto-fill manifest SHA256 fields after manual verification
```

The script downloads each archive, verifies the SHA256 (when provided), extracts it into `tools/<name>/`, and writes `.toolinfo.json` metadata so CI can detect whether a specific version is already staged.

Nightly or rolling releases can specify dynamic hashes by adding a `checksumSource` block to the manifest entry:

```json
{
  "name": "ffmpeg",
  "url": "https://example/releases/download/latest/package.zip",
  "archiveType": "zip",
  "destination": "ffmpeg",
  "checksumSource": {
    "url": "https://example/releases/download/latest/checksums.sha256",
    "fileName": "package.zip"
  }
}
```

When this block is present, `fetch-tools.ps1` downloads the checksum manifest, parses the SHA256 that matches `fileName`, and enforces it automatically so future upstream rotations do not require manual manifest updates.

For pinned vendor releases, prefer immutable archive URLs over moving `stable`, `latest`, or redirect-based endpoints. That keeps historical CI runs reproducible after upstream promotes a newer point release.

The ffmpeg entry has no download: it is compiled from the FFmpeg source tag with MSYS2 (`"archiveType": "msys2-build"`, recipe in `scripts/ffmpeg-slim`), because every public build links in far more codecs than Squish uses (about 230 MB against about 74 MB). SVT-AV1 and the NVENC/AMF/QSV encoders are kept. See `docs/ffmpeg-slim-build.md`; use `-Exclude ffmpeg` to skip it.

## Subfolders

- `ffmpeg/`
- `mozjpeg/`
- `libwebp/`
- `libavif/`
- `qpdf/`

Keep large binaries out of Git when possible; commit only the README, checksum, and license files while storing the actual payloads in release artifacts or package feeds.
