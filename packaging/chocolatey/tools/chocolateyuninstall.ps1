$ErrorActionPreference = 'Stop'

$candidates = @(
  (Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'CleanRift\unins000.exe'),
  (Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'CleanRift\unins000.exe'),
  (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs\CleanRift\unins000.exe')
)

$uninstaller = $candidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if ($uninstaller) {
  Uninstall-ChocolateyPackage -PackageName 'cleanrift' -FileType 'exe' `
    -SilentArgs '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART' -File $uninstaller
} else {
  Write-Warning 'CleanRift uninstaller was not found. It may already be removed.'
}
