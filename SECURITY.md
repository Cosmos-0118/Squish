# Security Policy

## Reporting a vulnerability

Please report security issues privately through GitHub's "Report a vulnerability" button
(Security tab → Advisories) rather than opening a public issue. Include the affected version,
reproduction steps and impact. You can expect an initial response within a few days.

## Supported versions

Only the latest release receives security fixes.

## Hardening notes

- Optimisation inputs are decoded with Magick.NET using magic-byte sniffing and resource limits.
- The loopback automation API requires a bearer token and validates `Host`, `Origin` and `Sec-Fetch-*` headers.
- Named pipes are restricted to the current user and message sizes are capped.
- Bundled third-party tools are downloaded by `scripts/fetch-tools.ps1` against pinned SHA-256 hashes; the script fails closed when a hash is missing.
- NuGet vulnerability auditing is enabled and treated as a build error.
