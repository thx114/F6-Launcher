<# Generate metadata only from the verified F6 portable updater archive. #>
param(
    [ValidateSet('x64')][string] $Architecture = 'x64',
    [Parameter(Mandatory)][string] $Version,
    [switch] $Dev,
    [string] $Updater = ''
)
$ErrorActionPreference = 'Stop'
if (-not $Updater) { $Updater = Join-Path $PSScriptRoot "build/release/F6Launcher_Portable_${Version}_${Architecture}.zip" }
& python (Join-Path $PSScriptRoot 'scripts/prepare-f6-release-metadata.py') --updater $Updater
if ($LASTEXITCODE -ne 0) { throw "Verified fork metadata generation failed (exit $LASTEXITCODE)." }
