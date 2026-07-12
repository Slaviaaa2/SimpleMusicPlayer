# Builds the release binary and assembles a distributable Windows folder
# (exe + libvlc runtime) under publish/. Replaces the old Publish-Release.ps1;
# installer/uninstaller logic now lives inside the binary itself (--uninstall,
# first-run setup dialog), so packaging is just a copy.
param(
    [switch]$SkipBuild,
    [string]$OutputDirectory
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$libvlcDir = Join-Path $repoRoot "vendor\libvlc\win-x64"
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot "publish\SimpleMusicPlayer-win-x64"
}

if (-not (Test-Path -LiteralPath (Join-Path $libvlcDir "libvlc.dll"))) {
    throw "libvlc runtime not found at $libvlcDir. Run scripts/fetch-libvlc.ps1 first."
}

if (-not $SkipBuild) {
    Push-Location $repoRoot
    try {
        cargo build --release
        if ($LASTEXITCODE -ne 0) { throw "cargo build failed." }
    }
    finally {
        Pop-Location
    }
}

$exePath = Join-Path $repoRoot "target\release\SimpleMusicPlayer.exe"
if (-not (Test-Path -LiteralPath $exePath)) {
    throw "SimpleMusicPlayer.exe was not found at $exePath. Build first (or drop -SkipBuild)."
}

if (Test-Path -LiteralPath $OutputDirectory) {
    Remove-Item -LiteralPath $OutputDirectory -Recurse -Force
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

Copy-Item -LiteralPath $exePath -Destination $OutputDirectory
foreach ($item in @("libvlc.dll", "libvlccore.dll", "plugins", "hrtfs", "lua")) {
    $source = Join-Path $libvlcDir $item
    if (Test-Path -LiteralPath $source) {
        Copy-Item -LiteralPath $source -Destination $OutputDirectory -Recurse
    }
}

Write-Host "Packaged release at $OutputDirectory"
