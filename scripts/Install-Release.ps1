param(
    [string]$AppDir = $PSScriptRoot,
    [string]$ShortcutName,
    [switch]$SkipToolDownloads,
    [switch]$RedownloadTools
)

$ErrorActionPreference = "Stop"

$configPath = Join-Path $PSScriptRoot "install-config.psd1"
if (-not (Test-Path -LiteralPath $configPath))
{
    throw "install-config.psd1 was not found next to this script."
}

$config = Import-PowerShellDataFile -LiteralPath $configPath
if ([string]::IsNullOrWhiteSpace($ShortcutName))
{
    $ShortcutName = $config.ShortcutName
}

Import-Module (Join-Path $PSScriptRoot "modules\Tools.psm1") -Force
Import-Module (Join-Path $PSScriptRoot "modules\ShellIntegration.psm1") -Force
Import-Module (Join-Path $PSScriptRoot "modules\SetupState.psm1") -Force

$appFullPath = [System.IO.Path]::GetFullPath($AppDir)
$targetPath = Join-Path $appFullPath $config.ExeName
if (-not (Test-Path -LiteralPath $targetPath))
{
    throw "$($config.ExeName) was not found in '$appFullPath'. Extract the release zip first, then run this script from the extracted app folder."
}

$toolsRoot = Join-Path $appFullPath "tools"
$ytDlpDir = Join-Path $toolsRoot "yt-dlp"
$denoDir = Join-Path $toolsRoot "deno"
$ffmpegDir = Join-Path $toolsRoot "ffmpeg"

if (-not $SkipToolDownloads)
{
    New-Item -ItemType Directory -Path $toolsRoot -Force | Out-Null
    Install-SmpYtDlp -DestinationDirectory $ytDlpDir -DownloadUrl $config.ToolUrls.YtDlp -Redownload:$RedownloadTools
    Install-SmpDeno -DestinationDirectory $denoDir -DownloadUrl $config.ToolUrls.Deno -Redownload:$RedownloadTools
    Install-SmpFfmpeg -DestinationDirectory $ffmpegDir -DownloadUrl $config.ToolUrls.Ffmpeg -Redownload:$RedownloadTools
}

$shellResult = Install-SmpShellIntegration `
    -AppFullPath $appFullPath `
    -TargetPath $targetPath `
    -ShortcutName $ShortcutName `
    -AppName $config.AppName `
    -ProgId $config.ProgId `
    -ApplicationKeyName $config.ApplicationKeyName `
    -CapabilitiesKeyName $config.CapabilitiesKeyName `
    -UninstallKeyName $config.UninstallKeyName `
    -UninstallScriptPath (Join-Path $appFullPath "Uninstall-SimpleMusicPlayer.ps1") `
    -SupportedExtensions $config.SupportedExtensions

Save-SmpSetupState -AppFullPath $appFullPath

Write-Host "Integrated app folder: $appFullPath"
Write-Host "Shortcut created at $($shellResult.ShortcutPath)"
Write-Host "Added to user PATH: $appFullPath"
Write-Host "Explorer context menu entries installed."
Write-Host "Registered media file associations for Default apps / Open with."
Write-Host "Registered Windows uninstall entry."
if ($SkipToolDownloads)
{
    Write-Host "Skipped yt-dlp / deno / ffmpeg download."
}
elseif ($RedownloadTools)
{
    Write-Host "Redownloaded bundled tools under $toolsRoot"
}
else
{
    Write-Host "Ensured bundled tools under $toolsRoot"
}
