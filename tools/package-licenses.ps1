function Copy-DistributionLicenses {
    param([string] $RepoRoot, [string[]] $Destinations)
    foreach ($name in @('LICENSE', 'Starward_LICENSE', 'NOTICE.md')) {
        $source = Join-Path $RepoRoot $name
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Required distribution notice missing: $name" }
        foreach ($destination in $Destinations) {
            $target = Join-Path $destination $name
            Copy-Item -LiteralPath $source -Destination $target -Force
            if ((Get-FileHash -LiteralPath $source).Hash -ne (Get-FileHash -LiteralPath $target).Hash) {
                throw "Distribution license mismatch: $target"
            }
        }
    }
}

function Assert-ForkDistribution {
    param([string] $RepoRoot, [string] $Output, [string] $Version)
    foreach ($relative in @('HoYoShadeHub.exe', 'F6Launcher.exe', "app-$Version\HoYoShadeHub.exe")) {
        $path = Join-Path $Output $relative
        if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or
            [Diagnostics.FileVersionInfo]::GetVersionInfo($path).ProductName -ne 'F6 Launcher') {
            throw "Refusing to distribute a launcher without independent product branding: $path"
        }
    }
    foreach ($name in @('f6-logo.ico', 'f6-logo.png')) {
        $source = Join-Path $RepoRoot "src\HoYoShadeHub\Assets\$name"
        $target = Join-Path $Output "app-$Version\Assets\$name"
        if (-not (Test-Path -LiteralPath $target) -or (Get-FileHash -LiteralPath $source).Hash -ne (Get-FileHash -LiteralPath $target).Hash) {
            throw "Independent artwork missing or mismatched: $target"
        }
    }
    foreach ($name in @('logo.ico', 'logo.png')) {
        if (Test-Path -LiteralPath (Join-Path $Output "app-$Version\Assets\$name")) {
            throw "Upstream branding asset unexpectedly included: $name"
        }
    }
}
