<# Build release files locally. Uploading is a separate explicit action. #>
param(
    [ValidateSet('x64')][string] $Architecture = 'x64',
    [Parameter(Mandatory)][string] $Version,
    [string] $Output = '',
    [int] $DiffCount = 0,
    [array] $DiffTags = @(),
    [string] $ShadeSource = ''
)
$ErrorActionPreference = 'Stop'
if ($DiffCount -ne 0 -or $DiffTags.Count -ne 0) {
    throw 'F6 portable releases use complete verified archives; the upstream diff backend is retired.'
}
& (Join-Path $PSScriptRoot 'package-full.ps1') -Version $Version -Architecture $Architecture -Output $Output -ShadeSource $ShadeSource -DisabledAddons ''
$full = Join-Path $PSScriptRoot "build/release/F6Launcher_Portable_${Version}_${Architecture}_Full.zip"
& python (Join-Path $PSScriptRoot 'scripts/package-launcher-updater.py') --full $full
if ($LASTEXITCODE -ne 0) { throw 'Updater archive generation failed.' }
& (Join-Path $PSScriptRoot 'metadata.ps1') -Version $Version -Architecture $Architecture
& python (Join-Path $PSScriptRoot 'scripts/verify-fork-distribution.py') $full (Join-Path $PSScriptRoot "build/release/F6Launcher_Portable_${Version}_${Architecture}.zip")
if ($LASTEXITCODE -ne 0) { throw 'Distribution compliance verification failed.' }
