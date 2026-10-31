$ErrorActionPreference = 'Stop'

$packageArgs = @{
  packageName    = 'cleanrift'
  fileType       = 'exe'
  url            = 'https://github.com/iseaprojects/cleanrift/releases/download/v1.0.0-beta.3.1/CleanRift-1.0-beta3.1-setup.exe'
  checksum       = '0603A280163EFC406E25E6B6D796A46641690B96F05FFECD72368D0A44C9F4BC'
  checksumType   = 'sha256'
  silentArgs     = '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /ALLUSERS'
  validExitCodes = @(0)
}

Install-ChocolateyPackage @packageArgs
