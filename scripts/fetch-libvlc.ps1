# Downloads the VideoLAN.LibVLC.Windows NuGet package and extracts the native
# libvlc runtime into vendor/libvlc/win-x64, which .cargo/config.toml points
# the vlc-sys build script at (VLC_LIB_DIR) and packaging copies next to the
# exe. Run once per clone (or to bump the libvlc version).
param(
    [string]$Version = "3.0.21",
    [string]$Architecture = "x64"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$targetDir = Join-Path $repoRoot "vendor\libvlc\win-$Architecture"
$packageUrl = "https://www.nuget.org/api/v2/package/VideoLAN.LibVLC.Windows/$Version"

$workDir = Join-Path ([System.IO.Path]::GetTempPath()) "smp-libvlc-$([guid]::NewGuid().ToString('N'))"
$archivePath = Join-Path $workDir "libvlc.nupkg.zip"

New-Item -ItemType Directory -Path $workDir -Force | Out-Null
try {
    Write-Host "Downloading VideoLAN.LibVLC.Windows $Version..."
    Invoke-WebRequest -Uri $packageUrl -OutFile $archivePath

    Write-Host "Extracting..."
    Expand-Archive -LiteralPath $archivePath -DestinationPath $workDir -Force

    $nativeDir = Join-Path $workDir "build\$Architecture"
    if (-not (Test-Path -LiteralPath (Join-Path $nativeDir "libvlc.dll"))) {
        throw "libvlc.dll was not found at build\$Architecture inside the package."
    }

    if (Test-Path -LiteralPath $targetDir) {
        Remove-Item -LiteralPath $targetDir -Recurse -Force
    }
    New-Item -ItemType Directory -Path $targetDir -Force | Out-Null
    Copy-Item -Path (Join-Path $nativeDir "*") -Destination $targetDir -Recurse -Force

    Write-Host "libvlc $Version installed to $targetDir"
}
finally {
    Remove-Item -LiteralPath $workDir -Recurse -Force -ErrorAction SilentlyContinue
}
