param(
    [Parameter(Mandatory)][string] $Output,
    [ValidateSet('x64', 'ARM64')][string] $Architecture = 'x64',
    [string] $Version = '1.0.0'
)
$ErrorActionPreference = 'Stop'
$dotnetExe = if ($env:HYSHADE_DOTNET) { $env:HYSHADE_DOTNET } else { 'dotnet' }
$sdkVersion = & $dotnetExe --version
if ($LASTEXITCODE -ne 0) { throw "Cannot identify the .NET SDK ($LASTEXITCODE)." }
$buildVersion = "$Version/$sdkVersion"
$outputPath = [IO.Path]::GetFullPath($Output)
$binary = Join-Path $outputPath 'F6Launcher.BackgroundHost.exe'
$versionStamp = Join-Path $outputPath 'build-version.txt'
$sourcePaths = @($PSScriptRoot, (Join-Path $PSScriptRoot '..\Shared'))
$sources = @($sourcePaths | ForEach-Object { Get-ChildItem -LiteralPath $_ -File } |
    Where-Object { $_.Extension -in '.cs', '.csproj', '.ps1' })
$sources += @(Get-ChildItem -Path (Join-Path $PSScriptRoot '..\HoYoShadeHub\Features\GameLauncher\Fps*.cs') -File |
    Where-Object { $_.Name -eq 'FpsUnlocker.cs' -or $_.Name -like 'FpsRemoteLifetime*' })
$sources += Get-Item -LiteralPath (Join-Path $PSScriptRoot '..\HoYoShadeHub.Extensions\Games\GameProcessDiscovery.cs')
if ((Test-Path -LiteralPath $binary) -and
    (Test-Path -LiteralPath $versionStamp) -and
    (Get-Content -LiteralPath $versionStamp -Raw).Trim() -ceq $buildVersion -and
    -not ($sources | Where-Object { $_.LastWriteTimeUtc -gt (Get-Item -LiteralPath $binary).LastWriteTimeUtc })) { return }
$runtime = 'win-' + $Architecture.ToLowerInvariant()
& $dotnetExe publish (Join-Path $PSScriptRoot 'F6Launcher.BackgroundHost.csproj') -c Release -r $runtime `
    -p:Platform=AnyCPU -p:Version=$Version -p:PublishDir="$outputPath\" -m:1 -nodeReuse:false --nologo
if ($LASTEXITCODE -ne 0) { throw "Lightweight background host build failed ($LASTEXITCODE)." }
Set-Content -LiteralPath $versionStamp -Value $buildVersion -Encoding Ascii
