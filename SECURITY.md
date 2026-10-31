# Security policy

## Supported versions

Only the latest beta receives fixes.

## Reporting a vulnerability

Please report security issues privately through
[GitHub Security Advisories](https://github.com/iseaprojects/cleanrift/security/advisories/new)
instead of opening a public issue. Include:

- CleanRift version and Windows build.
- A description of the issue and its impact.
- Reproduction steps or a minimal proof of concept.
- Whether the issue requires elevation or user interaction.

Do not include personal data, registry exports or full logs with private paths.

## Scope

CleanRift runs elevated and can delete files and registry keys. Relevant areas:

- Command-line job handling (`--job`, `/tasksch`, `/admin`).
- Elevation and the SkipUAC scheduled task.
- Registry path validation in removal jobs.
- HTML/trace output encoding.
- Installer and uninstaller behavior.

## Out of scope

- Vulnerabilities in third-party uninstallers that CleanRift launches.
- Issues caused by running CleanRift and another uninstaller simultaneously.
- Data loss caused by using Force Removal on production machines without a
  backup.
