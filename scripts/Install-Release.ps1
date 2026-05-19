param(
    [string]$ProjectPath = (Join-Path $PSScriptRoot "..\SimpleMusicPlayer.csproj"),
    [string]$InstallDir = "D:\Tools\SimpleMusicPlayer",
    [string]$ShortcutName = "Simple Music Player.lnk",
    [switch]$SkipToolDownloads
)

$ErrorActionPreference = "Stop"

$projectFullPath = [System.IO.Path]::GetFullPath($ProjectPath)
$installFullPath = [System.IO.Path]::GetFullPath($InstallDir)
$toolsRoot = Join-Path $installFullPath "tools"
$ytDlpDir = Join-Path $toolsRoot "yt-dlp"
$ffmpegDir = Join-Path $toolsRoot "ffmpeg"

New-Item -ItemType Directory -Path $installFullPath -Force | Out-Null

dotnet publish $projectFullPath -c Release -o $installFullPath

function Download-File
{
    param(
        [Parameter(Mandatory = $true)][string]$Url,
        [Parameter(Mandatory = $true)][string]$DestinationPath
    )

    Write-Host "Downloading $Url"
    Invoke-WebRequest -Uri $Url -OutFile $DestinationPath
}

function Install-YtDlp
{
    param([Parameter(Mandatory = $true)][string]$DestinationDirectory)

    New-Item -ItemType Directory -Path $DestinationDirectory -Force | Out-Null
    $ytDlpPath = Join-Path $DestinationDirectory "yt-dlp.exe"
    Download-File -Url "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe" -DestinationPath $ytDlpPath
    Write-Host "Installed yt-dlp to $ytDlpPath"
}

function Install-Ffmpeg
{
    param([Parameter(Mandatory = $true)][string]$DestinationDirectory)

    $archivePath = Join-Path ([System.IO.Path]::GetTempPath()) ("simplemusicplayer-ffmpeg-" + [guid]::NewGuid().ToString("N") + ".zip")
    $extractRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("simplemusicplayer-ffmpeg-" + [guid]::NewGuid().ToString("N"))

    try
    {
        Download-File -Url "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip" -DestinationPath $archivePath
        New-Item -ItemType Directory -Path $extractRoot -Force | Out-Null
        Expand-Archive -LiteralPath $archivePath -DestinationPath $extractRoot -Force

        $binDirectory = Get-ChildItem -Path $extractRoot -Directory -Recurse |
            Where-Object { $_.Name -eq "bin" } |
            Select-Object -First 1

        if (-not $binDirectory)
        {
            throw "Could not locate ffmpeg bin directory in extracted archive."
        }

        New-Item -ItemType Directory -Path $DestinationDirectory -Force | Out-Null

        foreach ($toolName in @("ffmpeg.exe", "ffprobe.exe", "ffplay.exe"))
        {
            $sourcePath = Join-Path $binDirectory.FullName $toolName
            if (Test-Path -LiteralPath $sourcePath)
            {
                Copy-Item -LiteralPath $sourcePath -Destination (Join-Path $DestinationDirectory $toolName) -Force
            }
        }

        if (-not (Test-Path -LiteralPath (Join-Path $DestinationDirectory "ffmpeg.exe")))
        {
            throw "ffmpeg.exe was not found after extracting the archive."
        }

        Write-Host "Installed ffmpeg tools to $DestinationDirectory"
    }
    finally
    {
        if (Test-Path -LiteralPath $archivePath)
        {
            Remove-Item -LiteralPath $archivePath -Force
        }

        if (Test-Path -LiteralPath $extractRoot)
        {
            Remove-Item -LiteralPath $extractRoot -Recurse -Force
        }
    }
}

if (-not $SkipToolDownloads)
{
    New-Item -ItemType Directory -Path $toolsRoot -Force | Out-Null
    Install-YtDlp -DestinationDirectory $ytDlpDir
    Install-Ffmpeg -DestinationDirectory $ffmpegDir
}

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
    ".mp3", ".wav", ".aac", ".m4a", ".flac", ".wma", ".ogg", ".opus",
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
if ($SkipToolDownloads)
{
    Write-Host "Skipped yt-dlp / ffmpeg download."
}
else
{
    Write-Host "Installed bundled tools under $toolsRoot"
}
