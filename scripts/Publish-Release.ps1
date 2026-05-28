param(
    [string]$RuntimeIdentifier = "win-x64",
    [string]$Configuration = "Release",
    [string]$OutputDir = (Join-Path $PSScriptRoot "..\publish\SimpleMusicPlayer-$RuntimeIdentifier")
)

$ErrorActionPreference = "Stop"

$outputFullPath = [System.IO.Path]::GetFullPath($OutputDir)
$projectPath = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\SimpleMusicPlayer.csproj"))
$installerSourcePath = Join-Path $PSScriptRoot "Install-Release.ps1"
$installerTargetPath = Join-Path $outputFullPath "Install-SimpleMusicPlayer.ps1"
$installerConfigSourcePath = Join-Path $PSScriptRoot "install-config.psd1"
$installerConfigTargetPath = Join-Path $outputFullPath "install-config.psd1"
$installerModulesSourcePath = Join-Path $PSScriptRoot "modules"
$installerModulesTargetPath = Join-Path $outputFullPath "modules"

if (Test-Path -LiteralPath $outputFullPath)
{
    Remove-Item -LiteralPath $outputFullPath -Recurse -Force
}

dotnet publish $projectPath -c $Configuration -r $RuntimeIdentifier --self-contained false -o $outputFullPath
if ($LASTEXITCODE -ne 0)
{
    throw "dotnet publish failed for runtime '$RuntimeIdentifier'."
}

if ($RuntimeIdentifier -like "win-*")
{
    Copy-Item -LiteralPath $installerSourcePath -Destination $installerTargetPath -Force
    Copy-Item -LiteralPath $installerConfigSourcePath -Destination $installerConfigTargetPath -Force
    Copy-Item -LiteralPath $installerModulesSourcePath -Destination $installerModulesTargetPath -Recurse -Force
}

Write-Host "Prepared release files in $outputFullPath"
if ($RuntimeIdentifier -like "win-*")
{
    Write-Host "Ship the contents of this folder, then run Install-SimpleMusicPlayer.ps1 from inside the published folder on the target machine if you want Explorer integration."
}
