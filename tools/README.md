# Clop Third-Party Tool Drop Zone

Populate the subdirectories below with the binaries referenced in `docs/windows-deps.md` before running the installer build. Each folder should contain:

- The executable or library payload required by Clop.
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

## Subfolders

- `ffmpeg/`
- `ghostscript/`
- `mozjpeg/`
- `libwebp/`
- `libavif/`
- `qpdf/`

Keep large binaries out of Git when possible; commit only the README, checksum, and license files while storing the actual payloads in release artifacts or package feeds.
