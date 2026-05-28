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

Export-ModuleMember -Function Save-SmpSetupState
