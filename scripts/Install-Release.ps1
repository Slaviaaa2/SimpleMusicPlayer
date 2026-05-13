param(
    [string]$ProjectPath = (Join-Path $PSScriptRoot "..\SimpleMusicPlayer.csproj"),
    [string]$InstallDir = "D:\Tools\SimpleMusicPlayer",
    [string]$ShortcutName = "Simple Music Player.lnk"
)

$ErrorActionPreference = "Stop"

$projectFullPath = [System.IO.Path]::GetFullPath($ProjectPath)
$installFullPath = [System.IO.Path]::GetFullPath($InstallDir)

New-Item -ItemType Directory -Path $installFullPath -Force | Out-Null

dotnet publish $projectFullPath -c Release -o $installFullPath

$programsPath = [Environment]::GetFolderPath("Programs")
$shortcutPath = Join-Path $programsPath $ShortcutName
$targetPath = Join-Path $installFullPath "SimpleMusicPlayer.exe"

$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $targetPath
$shortcut.WorkingDirectory = $installFullPath
$shortcut.IconLocation = $targetPath
$shortcut.Save()

Write-Host "Published to $installFullPath"
Write-Host "Shortcut created at $shortcutPath"
