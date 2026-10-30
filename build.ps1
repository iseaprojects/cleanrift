param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$InnoPath = '',
    [switch]$SkipInstaller,
    [switch]$SkipAssets
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
Set-Location $root

$version = '1.0-beta1'
$portableName = "CleanRift-$version-portable"
$releaseRoot = Join-Path $root 'artifacts\release'
$portable = Join-Path $releaseRoot $portableName
$zip = Join-Path $releaseRoot "$portableName.zip"

Write-Host "== CleanRift $version ==" -ForegroundColor Cyan

if (-not $SkipAssets) {
    Write-Host '-- Generating icon and language files'
    & (Join-Path $root 'tools\Build-BrandAssets.ps1')
    & (Join-Path $root 'tools\Build-Languages.ps1')
}

Write-Host '-- Building solution'
& dotnet build (Join-Path $root 'CleanRift.sln') -c $Configuration -v:q
if ($LASTEXITCODE -ne 0) { throw 'dotnet build failed' }

if (Test-Path $portable) { Remove-Item $portable -Recurse -Force }
New-Item -ItemType Directory -Path $portable -Force | Out-Null

Write-Host '-- Publishing portable build'
& dotnet publish (Join-Path $root 'src\CleanRift\CleanRift.csproj') `
    -c $Configuration -r $Runtime --self-contained true `
    -p:PublishSingleFile=false -p:PublishTrimmed=false `
    -o $portable -v:q
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

Get-ChildItem $portable -Filter *.pdb -File -Recurse | Remove-Item -Force
Copy-Item (Join-Path $root 'THIRD-PARTY-NOTICES.md') $portable -Force

Write-Host '-- Creating portable ZIP'
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $portable '*') -DestinationPath $zip -CompressionLevel Optimal

$setup = Join-Path $releaseRoot "CleanRift-$version-setup.exe"
if (-not $SkipInstaller) {
    $iscc = $null
    if ($InnoPath) {
        if (Test-Path $InnoPath) { $iscc = $InnoPath }
        else { throw "ISCC.exe not found at $InnoPath" }
    }
    if (-not $iscc) {
        foreach ($candidate in @(
            (Join-Path $root 'tools\innosetup\ISCC.exe'),
            'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
            'C:\Program Files\Inno Setup 6\ISCC.exe'
        )) {
            if (Test-Path $candidate) { $iscc = $candidate; break }
        }
    }
    if (-not $iscc) {
        $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
        if ($command) { $iscc = $command.Source }
    }

    if ($iscc) {
        Write-Host "-- Building installer with $iscc"
        & $iscc (Join-Path $root 'installer\CleanRift.iss')
        if ($LASTEXITCODE -ne 0) { throw 'Inno Setup compilation failed' }
    }
    else {
        Write-Warning 'ISCC.exe not found. Portable ZIP was created; run installer\CleanRift.iss with Inno Setup 6 to build the setup.'
    }
}

Write-Host '-- Checksums'
$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("CleanRift $version")
$lines.Add("Built: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')")
$lines.Add('')
$lines.Add("Portable ZIP: $(Split-Path $zip -Leaf)")
$lines.Add("SHA256: $((Get-FileHash $zip -Algorithm SHA256).Hash)")
$exe = Join-Path $portable 'CleanRift.exe'
$dll = Join-Path $portable 'CleanRift.dll'
$lines.Add("CleanRift.exe SHA256: $((Get-FileHash $exe -Algorithm SHA256).Hash)")
$lines.Add("CleanRift.dll SHA256: $((Get-FileHash $dll -Algorithm SHA256).Hash)")
if (Test-Path $setup) {
    $lines.Add("Setup EXE: $(Split-Path $setup -Leaf)")
    $lines.Add("Setup SHA256: $((Get-FileHash $setup -Algorithm SHA256).Hash)")
}
$checksums = Join-Path $releaseRoot 'SHA256.txt'
$lines | Set-Content $checksums -Encoding UTF8
$lines | ForEach-Object { Write-Host $_ }

Write-Host '== Done ==' -ForegroundColor Green
