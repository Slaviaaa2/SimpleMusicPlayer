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
$fileCommand = "`"$targetPath`" `"%1`""
$backgroundCommand = "powershell.exe -NoProfile -WindowStyle Hidden -Command `"Start-Process -FilePath '$targetPath' -WorkingDirectory '%V'`""

$folderKey = "HKCU:\Software\Classes\Directory\shell\SimpleMusicPlayer"
$folderCommandKey = Join-Path $folderKey "command"
New-Item -Path $folderKey -Force | Out-Null
New-Item -Path $folderCommandKey -Force | Out-Null
Set-Item -Path $folderKey -Value "Play with Simple Music Player"
New-ItemProperty -Path $folderKey -Name "Icon" -Value $targetPath -PropertyType String -Force | Out-Null
Set-Item -Path $folderCommandKey -Value $folderCommand

$backgroundKey = "HKCU:\Software\Classes\Directory\Background\shell\SimpleMusicPlayer"
$backgroundCommandKey = Join-Path $backgroundKey "command"
New-Item -Path $backgroundKey -Force | Out-Null
New-Item -Path $backgroundCommandKey -Force | Out-Null
Set-Item -Path $backgroundKey -Value "Open here with Simple Music Player"
New-ItemProperty -Path $backgroundKey -Name "Icon" -Value $targetPath -PropertyType String -Force | Out-Null
Set-Item -Path $backgroundCommandKey -Value $backgroundCommand

$progId = "SimpleMusicPlayer.media"
$progIdKey = "HKCU:\Software\Classes\$progId"
$progIdCommandKey = Join-Path $progIdKey "shell\open\command"
$progIdIconKey = Join-Path $progIdKey "DefaultIcon"
New-Item -Path $progIdCommandKey -Force | Out-Null
New-Item -Path $progIdIconKey -Force | Out-Null
Set-Item -Path $progIdKey -Value "Simple Music Player media file"
Set-Item -Path $progIdCommandKey -Value $fileCommand
Set-Item -Path $progIdIconKey -Value $targetPath

$applicationKey = "HKCU:\Software\Classes\Applications\SimpleMusicPlayer.exe"
$applicationCommandKey = Join-Path $applicationKey "shell\open\command"
$supportedTypesKey = Join-Path $applicationKey "SupportedTypes"
New-Item -Path $applicationCommandKey -Force | Out-Null
New-Item -Path $supportedTypesKey -Force | Out-Null
New-ItemProperty -Path $applicationKey -Name "FriendlyAppName" -Value "Simple Music Player" -PropertyType String -Force | Out-Null
Set-Item -Path $applicationCommandKey -Value $fileCommand

$capabilitiesKey = "HKCU:\Software\SimpleMusicPlayer\Capabilities"
$fileAssociationsKey = Join-Path $capabilitiesKey "FileAssociations"
New-Item -Path $fileAssociationsKey -Force | Out-Null
New-ItemProperty -Path $capabilitiesKey -Name "ApplicationName" -Value "Simple Music Player" -PropertyType String -Force | Out-Null
New-ItemProperty -Path $capabilitiesKey -Name "ApplicationDescription" -Value "Minimal media player for local music and video files." -PropertyType String -Force | Out-Null

$registeredApplicationsKey = "HKCU:\Software\RegisteredApplications"
New-Item -Path $registeredApplicationsKey -Force | Out-Null
New-ItemProperty -Path $registeredApplicationsKey -Name "Simple Music Player" -Value "Software\SimpleMusicPlayer\Capabilities" -PropertyType String -Force | Out-Null

$supportedExtensions = @(
    ".mp3", ".wav", ".aac", ".m4a", ".flac", ".wma", ".ogg",
    ".mp4", ".m4v", ".mov", ".wmv", ".avi", ".mkv", ".webm"
)

foreach ($extension in $supportedExtensions)
{
    New-ItemProperty -Path $supportedTypesKey -Name $extension -Value "" -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $fileAssociationsKey -Name $extension -Value $progId -PropertyType String -Force | Out-Null

    $openWithProgidsKey = "HKCU:\Software\Classes\$extension\OpenWithProgids"
    New-Item -Path $openWithProgidsKey -Force | Out-Null
    New-ItemProperty -Path $openWithProgidsKey -Name $progId -Value "" -PropertyType String -Force | Out-Null
}

Write-Host "Published to $installFullPath"
Write-Host "Shortcut created at $shortcutPath"
Write-Host "Added to user PATH: $installFullPath"
Write-Host "Explorer context menu entries installed."
Write-Host "Registered media file associations for Default apps / Open with."
