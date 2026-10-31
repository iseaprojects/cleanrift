# Package manager packaging

This directory contains the source files used to publish CleanRift through
package managers.

Current release: **1.0 Beta 3.1** (`1.0.0-beta.3.1`).

| Channel | Package | Status |
| --- | --- | --- |
| Chocolatey | `cleanrift 1.0.0-beta3-1` | Published in the community repository |
| Scoop | `iseaprojects/scoop-cleanrift` | Published and tested |
| winget | `CleanRift.CleanRift 1.0.0-beta.3.1` | Submitted; all checks passed, awaiting moderator merge |

Chocolatey uses SemVer v1 for prereleases (`1.0.0-beta3-1`) because the
community repository does not accept SemVer v2 versions such as
`1.0.0-beta3.1`; it rejects them with an unhelpful 504/500 response.

## Winget

Manifests live in `packaging/winget/`. They are submitted to
[`microsoft/winget-pkgs`](https://github.com/microsoft/winget-pkgs).

```powershell
winget validate --manifest packaging\winget
winget install --manifest packaging\winget
```

Installing from a local manifest requires enabling `LocalManifestFiles` as
administrator. The winget-pkgs validation pipeline checks the submitted manifest
as well.

Once merged, users can install with:

```powershell
winget install CleanRift.CleanRift
```

The first winget-pkgs pull request may require signing the Microsoft
Contributor License Agreement in the browser.

## Chocolatey

The package source is in `packaging/chocolatey/`.

```powershell
cd packaging\chocolatey
choco pack
```

Publishing needs a Chocolatey community account and API key. Never commit the
key:

```powershell
choco apikey --key <your-api-key> --source https://push.chocolatey.org/
choco push cleanrift.1.0.0-beta3-1.nupkg --source https://push.chocolatey.org/
```

The package is published. Users install the beta with:

```powershell
choco install cleanrift --pre
```

For a new release, pack and push the new version; it goes through automated
checks and moderation again.

## Scoop

The bucket manifest is in `packaging/scoop/cleanrift.json` and is also published
in the `iseaprojects/scoop-cleanrift` bucket repository.

```powershell
scoop bucket add cleanrift https://github.com/iseaprojects/scoop-cleanrift
scoop install cleanrift
```

## Updating for a new release

1. Bump the version in every manifest.
2. Replace the installer/portable URLs and SHA-256 hashes.
3. Run `winget validate --manifest packaging\winget`.
4. Run `choco pack` in `packaging\chocolatey`.
5. Update the Scoop bucket repository.

Do not commit API keys, tokens or private certificates.
