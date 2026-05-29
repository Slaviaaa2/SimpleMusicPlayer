param(
    [string]$AppDir = $PSScriptRoot,
    [string]$ShortcutName,
    [switch]$RemoveUserData,
    [switch]$RemoveCache,
    [switch]$RemoveBundledTools,
    [switch]$RemoveAppDirectory
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

Import-Module (Join-Path $PSScriptRoot "modules\ShellIntegration.psm1") -Force
Import-Module (Join-Path $PSScriptRoot "modules\SetupState.psm1") -Force

$appFullPath = [System.IO.Path]::GetFullPath($AppDir)
$targetPath = Join-Path $appFullPath $config.ExeName

$shellResult = Uninstall-SmpShellIntegration `
    -AppFullPath $appFullPath `
    -TargetPath $targetPath `
    -ShortcutName $ShortcutName `
    -AppName $config.AppName `
    -ProgId $config.ProgId `
    -ApplicationKeyName $config.ApplicationKeyName `
    -CapabilitiesKeyName $config.CapabilitiesKeyName `
    -UninstallKeyName $config.UninstallKeyName `
    -SupportedExtensions $config.SupportedExtensions

Remove-SmpSetupState -RemoveUserData:$RemoveUserData

$cachePath = Join-Path $appFullPath "cache"
if ($RemoveCache -and (Test-Path -LiteralPath $cachePath))
{
    Remove-Item -LiteralPath $cachePath -Recurse -Force
}

$toolsPath = Join-Path $appFullPath "tools"
if ($RemoveBundledTools -and (Test-Path -LiteralPath $toolsPath))
{
    Remove-Item -LiteralPath $toolsPath -Recurse -Force
}

if ($RemoveAppDirectory)
{
    if (-not (Test-Path -LiteralPath $targetPath))
    {
        throw "$($config.ExeName) was not found in '$appFullPath'. Refusing to remove the app directory."
    }

    $appRoot = [System.IO.Path]::GetPathRoot($appFullPath)
    if ([string]::Equals($appFullPath.TrimEnd('\', '/'), $appRoot.TrimEnd('\', '/'), [System.StringComparison]::OrdinalIgnoreCase))
    {
        throw "Refusing to remove a drive root: $appFullPath"
    }

    $escapedAppFullPath = $appFullPath.Replace("'", "''")
    $escapedDeleteScriptPath = (Join-Path $env:TEMP "SimpleMusicPlayer-remove-app.ps1").Replace("'", "''")
    $deleteScript = @"
Start-Sleep -Seconds 2
Remove-Item -LiteralPath '$escapedAppFullPath' -Recurse -Force
Remove-Item -LiteralPath '$escapedDeleteScriptPath' -Force -ErrorAction SilentlyContinue
"@
    $deleteScriptPath = Join-Path $env:TEMP "SimpleMusicPlayer-remove-app.ps1"
    $deleteScript | Set-Content -LiteralPath $deleteScriptPath -Encoding UTF8
    Start-Process -FilePath "powershell.exe" -ArgumentList @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $deleteScriptPath) -WindowStyle Hidden
}

Write-Host "Removed shortcut at $($shellResult.ShortcutPath)"
Write-Host "Removed app folder from user PATH."
Write-Host "Removed Explorer context menu entries."
Write-Host "Removed Default apps / Open with registration."
Write-Host "Removed Windows uninstall entry."
Write-Host "Removed setup state."
if ($RemoveUserData)
{
    Write-Host "Removed playback history and local app data."
}
if ($RemoveCache)
{
    Write-Host "Removed playback cache."
}
if ($RemoveBundledTools)
{
    Write-Host "Removed bundled yt-dlp / deno / ffmpeg tools."
}
if ($RemoveAppDirectory)
{
    Write-Host "Scheduled app folder removal: $appFullPath"
}
