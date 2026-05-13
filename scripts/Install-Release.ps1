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

$userPath = [Environment]::GetEnvironmentVariable("Path", "User")
$pathEntries = @($userPath -split ';' | Where-Object { $_ })
if ($pathEntries -notcontains $installFullPath)
{
    $updatedPath = if ([string]::IsNullOrWhiteSpace($userPath)) { $installFullPath } else { "$userPath;$installFullPath" }
    [Environment]::SetEnvironmentVariable("Path", $updatedPath, "User")
}

$folderCommand = "`"$targetPath`" --album `"%1`""
$backgroundCommand = "powershell.exe -NoProfile -WindowStyle Hidden -Command `"Start-Process -FilePath '$targetPath' -WorkingDirectory '%V'`""

$folderKey = "HKCU:\Software\Classes\Directory\shell\SimpleMusicPlayer"
$folderCommandKey = Join-Path $folderKey "command"
New-Item -Path $folderKey -Force | Out-Null
New-Item -Path $folderCommandKey -Force | Out-Null
Set-Item -Path $folderKey -Value "Play with Simple Music Player"
Set-ItemProperty -Path $folderKey -Name "Icon" -Value $targetPath
Set-Item -Path $folderCommandKey -Value $folderCommand

$backgroundKey = "HKCU:\Software\Classes\Directory\Background\shell\SimpleMusicPlayer"
$backgroundCommandKey = Join-Path $backgroundKey "command"
New-Item -Path $backgroundKey -Force | Out-Null
New-Item -Path $backgroundCommandKey -Force | Out-Null
Set-Item -Path $backgroundKey -Value "Open here with Simple Music Player"
Set-ItemProperty -Path $backgroundKey -Name "Icon" -Value $targetPath
Set-Item -Path $backgroundCommandKey -Value $backgroundCommand

Write-Host "Published to $installFullPath"
Write-Host "Shortcut created at $shortcutPath"
Write-Host "Added to user PATH: $installFullPath"
Write-Host "Explorer context menu entries installed."
