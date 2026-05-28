function Invoke-SmpDownload
{
    param(
        [Parameter(Mandatory = $true)][string]$Url,
        [Parameter(Mandatory = $true)][string]$DestinationPath
    )

    Write-Host "Downloading $Url"
    Invoke-WebRequest -Uri $Url -OutFile $DestinationPath
}

function Install-SmpYtDlp
{
    param(
        [Parameter(Mandatory = $true)][string]$DestinationDirectory,
        [Parameter(Mandatory = $true)][string]$DownloadUrl,
        [switch]$Redownload
    )

    New-Item -ItemType Directory -Path $DestinationDirectory -Force | Out-Null
    $ytDlpPath = Join-Path $DestinationDirectory "yt-dlp.exe"

    if ((-not $Redownload) -and (Test-Path -LiteralPath $ytDlpPath))
    {
        Write-Host "yt-dlp already exists at $ytDlpPath. Skipping download."
        return
    }

    Invoke-SmpDownload -Url $DownloadUrl -DestinationPath $ytDlpPath
    Write-Host "Installed yt-dlp to $ytDlpPath"
}

function Install-SmpDeno
{
    param(
        [Parameter(Mandatory = $true)][string]$DestinationDirectory,
        [Parameter(Mandatory = $true)][string]$DownloadUrl,
        [switch]$Redownload
    )

    $denoPath = Join-Path $DestinationDirectory "deno.exe"
    if ((-not $Redownload) -and (Test-Path -LiteralPath $denoPath))
    {
        Write-Host "deno already exists at $denoPath. Skipping download."
        return
    }

    $archivePath = Join-Path ([System.IO.Path]::GetTempPath()) ("simplemusicplayer-deno-" + [guid]::NewGuid().ToString("N") + ".zip")
    $extractRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("simplemusicplayer-deno-" + [guid]::NewGuid().ToString("N"))

    try
    {
        Invoke-SmpDownload -Url $DownloadUrl -DestinationPath $archivePath
        New-Item -ItemType Directory -Path $extractRoot -Force | Out-Null
        Expand-Archive -LiteralPath $archivePath -DestinationPath $extractRoot -Force
        New-Item -ItemType Directory -Path $DestinationDirectory -Force | Out-Null

        $sourcePath = Join-Path $extractRoot "deno.exe"
        if (-not (Test-Path -LiteralPath $sourcePath))
        {
            throw "deno.exe was not found after extracting the archive."
        }

        Copy-Item -LiteralPath $sourcePath -Destination $denoPath -Force
        Write-Host "Installed deno to $denoPath"
    }
    finally
    {
        if (Test-Path -LiteralPath $archivePath) { Remove-Item -LiteralPath $archivePath -Force }
        if (Test-Path -LiteralPath $extractRoot) { Remove-Item -LiteralPath $extractRoot -Recurse -Force }
    }
}

function Install-SmpFfmpeg
{
    param(
        [Parameter(Mandatory = $true)][string]$DestinationDirectory,
        [Parameter(Mandatory = $true)][string]$DownloadUrl,
        [switch]$Redownload
    )

    $ffmpegPath = Join-Path $DestinationDirectory "ffmpeg.exe"
    if ((-not $Redownload) -and (Test-Path -LiteralPath $ffmpegPath))
    {
        Write-Host "ffmpeg already exists at $ffmpegPath. Skipping download."
        return
    }

    $archivePath = Join-Path ([System.IO.Path]::GetTempPath()) ("simplemusicplayer-ffmpeg-" + [guid]::NewGuid().ToString("N") + ".zip")
    $extractRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("simplemusicplayer-ffmpeg-" + [guid]::NewGuid().ToString("N"))

    try
    {
        Invoke-SmpDownload -Url $DownloadUrl -DestinationPath $archivePath
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

        if (-not (Test-Path -LiteralPath $ffmpegPath))
        {
            throw "ffmpeg.exe was not found after extracting the archive."
        }

        Write-Host "Installed ffmpeg tools to $DestinationDirectory"
    }
    finally
    {
        if (Test-Path -LiteralPath $archivePath) { Remove-Item -LiteralPath $archivePath -Force }
        if (Test-Path -LiteralPath $extractRoot) { Remove-Item -LiteralPath $extractRoot -Recurse -Force }
    }
}

Export-ModuleMember -Function Invoke-SmpDownload, Install-SmpYtDlp, Install-SmpDeno, Install-SmpFfmpeg
