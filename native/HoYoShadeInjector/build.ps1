param(
    [Parameter(Mandatory)][string] $Output,
    [ValidateSet('x64', 'x86', 'ARM64')][string] $Architecture = 'x64'
)
$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot 'injector.cpp'
$outputPath = [IO.Path]::GetFullPath($Output)
New-Item -ItemType Directory -Force -Path $outputPath | Out-Null
$binary = Join-Path $outputPath 'F6ShadeInjector.exe'
if ((Test-Path -LiteralPath $binary) -and
    (Get-Item -LiteralPath $binary).LastWriteTimeUtc -gt (Get-Item -LiteralPath $source).LastWriteTimeUtc -and
    (Get-Item -LiteralPath $binary).LastWriteTimeUtc -gt (Get-Item -LiteralPath $PSCommandPath).LastWriteTimeUtc) { return }

$vc = $env:VSINSTALLDIR
if (-not $vc) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path -LiteralPath $vswhere) {
        $vc = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    }
}
if (-not $vc -and (Test-Path 'D:\VS2022BuildTools\VC\Auxiliary\Build\vcvarsall.bat')) { $vc = 'D:\VS2022BuildTools' }
$vcvars = Join-Path $vc 'VC\Auxiliary\Build\vcvarsall.bat'
if (-not (Test-Path -LiteralPath $vcvars)) { throw 'MSVC C++ Build Tools are required for F6ShadeInjector.' }
$vcArch = @{ x64 = 'x64'; x86 = 'x86'; ARM64 = 'x64_arm64' }[$Architecture]
$batch = Join-Path $outputPath 'compile-injector.cmd'
$command = "@call `"$vcvars`" $vcArch >nul`r`n@cl /nologo /EHsc /MT /std:c++17 /source-charset:utf-8 `"$source`" /Fo`"$outputPath\injector.obj`" /Fe`"$binary`" /link /subsystem:console`r`n@exit /b %errorlevel%`r`n"
[IO.File]::WriteAllText($batch, $command, [Text.Encoding]::Default)
& $env:ComSpec /d /c $batch
if ($LASTEXITCODE -ne 0) { throw "F6ShadeInjector build failed ($LASTEXITCODE)." }
