function Save-SmpSetupState
{
    param(
        [Parameter(Mandatory = $true)][string]$AppFullPath
    )

    $setupStatePath = Join-Path ([Environment]::GetFolderPath("LocalApplicationData")) "SimpleMusicPlayer\setup-state.json"
    $stateDirectory = Split-Path -Parent $setupStatePath
    New-Item -ItemType Directory -Path $stateDirectory -Force | Out-Null

    $state = [ordered]@{
        CompletedInstallPath = $AppFullPath
        CompletedAt = (Get-Date).ToString("O")
        DismissedInstallPath = $null
        DismissedAt = $null
    }

    $state | ConvertTo-Json | Set-Content -LiteralPath $setupStatePath -Encoding UTF8
}

function Remove-SmpSetupState
{
    param(
        [switch]$RemoveUserData
    )

    $stateDirectory = Join-Path ([Environment]::GetFolderPath("LocalApplicationData")) "SimpleMusicPlayer"
    if (-not (Test-Path -LiteralPath $stateDirectory))
    {
        return
    }

    if ($RemoveUserData)
    {
        Remove-Item -LiteralPath $stateDirectory -Recurse -Force
        return
    }

    $setupStatePath = Join-Path $stateDirectory "setup-state.json"
    if (Test-Path -LiteralPath $setupStatePath)
    {
        Remove-Item -LiteralPath $setupStatePath -Force
    }
}

Export-ModuleMember -Function Save-SmpSetupState, Remove-SmpSetupState
