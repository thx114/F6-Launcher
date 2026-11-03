<# Compatibility entry point for the maintained F6 portable publisher. #>
param(
    [ValidateSet('x64','x86','ARM64')][string] $Architecture = 'x64',
    [Parameter(Mandatory)][string] $Version,
    [string] $Output = 'build/HoYoShadeHub',
    [switch] $Dev
)
$ErrorActionPreference = 'Stop'
if ($Dev) { throw 'Use compile.ps1 for development; release packaging uses package.ps1 or package-full.ps1.' }
& (Join-Path $PSScriptRoot 'package.ps1') -Version $Version -Architecture $Architecture -Output $Output -SkipZip
