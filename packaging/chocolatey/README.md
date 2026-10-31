# Chocolatey package

Package ID: `cleanrift`
Version: `1.0.0-beta3-1`

## Build

```powershell
choco pack
```

This creates `cleanrift.1.0.0-beta3-1.nupkg`.

## Test

Run in an elevated shell and use a disposable machine or VM:

```powershell
choco install cleanrift --pre --source . --yes
choco uninstall cleanrift --yes
```

## Publish

Publishing to the Chocolatey community repository requires a Chocolatey
account and API key. Never commit the key.

```powershell
choco apikey --key <your-api-key> --source https://push.chocolatey.org/
choco push cleanrift.1.0.0-beta3-1.nupkg --source https://push.chocolatey.org/
```

The package is submitted for moderation. After approval, users can install
with:

```powershell
choco install cleanrift --pre
```
