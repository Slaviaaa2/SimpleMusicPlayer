function Install-SmpShellIntegration
{
    param(
        [Parameter(Mandatory = $true)][string]$AppFullPath,
        [Parameter(Mandatory = $true)][string]$TargetPath,
        [Parameter(Mandatory = $true)][string]$ShortcutName,
        [Parameter(Mandatory = $true)][string]$AppName,
        [Parameter(Mandatory = $true)][string]$ProgId,
        [Parameter(Mandatory = $true)][string]$ApplicationKeyName,
        [Parameter(Mandatory = $true)][string]$CapabilitiesKeyName,
        [Parameter(Mandatory = $true)][string[]]$SupportedExtensions
    )

    $programsPath = [Environment]::GetFolderPath("Programs")
    $shortcutPath = Join-Path $programsPath $ShortcutName

    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $TargetPath
    $shortcut.WorkingDirectory = $AppFullPath
    $shortcut.IconLocation = $TargetPath
    $shortcut.Save()

    $userPath = [Environment]::GetEnvironmentVariable("Path", "User")
    $pathEntries = @($userPath -split ';' | Where-Object { $_ })
    if ($pathEntries -notcontains $AppFullPath)
    {
        $updatedPath = if ([string]::IsNullOrWhiteSpace($userPath)) { $AppFullPath } else { "$userPath;$AppFullPath" }
        [Environment]::SetEnvironmentVariable("Path", $updatedPath, "User")
    }

    $folderCommand = "`"$TargetPath`" --album `"%1`""
    $fileCommand = "`"$TargetPath`" `"%1`""
    $backgroundCommand = "powershell.exe -NoProfile -WindowStyle Hidden -Command `"Start-Process -FilePath '$TargetPath' -WorkingDirectory '%V'`""

    $folderKey = "HKCU:\Software\Classes\Directory\shell\SimpleMusicPlayer"
    $folderCommandKey = Join-Path $folderKey "command"
    New-Item -Path $folderKey -Force | Out-Null
    New-Item -Path $folderCommandKey -Force | Out-Null
    Set-Item -Path $folderKey -Value "Play with $AppName"
    New-ItemProperty -Path $folderKey -Name "Icon" -Value $TargetPath -PropertyType String -Force | Out-Null
    Set-Item -Path $folderCommandKey -Value $folderCommand

    $backgroundKey = "HKCU:\Software\Classes\Directory\Background\shell\SimpleMusicPlayer"
    $backgroundCommandKey = Join-Path $backgroundKey "command"
    New-Item -Path $backgroundKey -Force | Out-Null
    New-Item -Path $backgroundCommandKey -Force | Out-Null
    Set-Item -Path $backgroundKey -Value "Open here with $AppName"
    New-ItemProperty -Path $backgroundKey -Name "Icon" -Value $TargetPath -PropertyType String -Force | Out-Null
    Set-Item -Path $backgroundCommandKey -Value $backgroundCommand

    $progIdKey = "HKCU:\Software\Classes\$ProgId"
    $progIdCommandKey = Join-Path $progIdKey "shell\open\command"
    $progIdIconKey = Join-Path $progIdKey "DefaultIcon"
    New-Item -Path $progIdCommandKey -Force | Out-Null
    New-Item -Path $progIdIconKey -Force | Out-Null
    Set-Item -Path $progIdKey -Value "$AppName media file"
    Set-Item -Path $progIdCommandKey -Value $fileCommand
    Set-Item -Path $progIdIconKey -Value $TargetPath

    $applicationKey = "HKCU:\Software\Classes\Applications\$ApplicationKeyName"
    $applicationCommandKey = Join-Path $applicationKey "shell\open\command"
    $supportedTypesKey = Join-Path $applicationKey "SupportedTypes"
    New-Item -Path $applicationCommandKey -Force | Out-Null
    New-Item -Path $supportedTypesKey -Force | Out-Null
    New-ItemProperty -Path $applicationKey -Name "FriendlyAppName" -Value $AppName -PropertyType String -Force | Out-Null
    Set-Item -Path $applicationCommandKey -Value $fileCommand

    $capabilitiesKey = "HKCU:\Software\$CapabilitiesKeyName\Capabilities"
    $fileAssociationsKey = Join-Path $capabilitiesKey "FileAssociations"
    New-Item -Path $fileAssociationsKey -Force | Out-Null
    New-ItemProperty -Path $capabilitiesKey -Name "ApplicationName" -Value $AppName -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $capabilitiesKey -Name "ApplicationDescription" -Value "Minimal media player for local music and video files." -PropertyType String -Force | Out-Null

    $registeredApplicationsKey = "HKCU:\Software\RegisteredApplications"
    New-Item -Path $registeredApplicationsKey -Force | Out-Null
    New-ItemProperty -Path $registeredApplicationsKey -Name $AppName -Value "Software\$CapabilitiesKeyName\Capabilities" -PropertyType String -Force | Out-Null

    foreach ($extension in $SupportedExtensions)
    {
        New-ItemProperty -Path $supportedTypesKey -Name $extension -Value "" -PropertyType String -Force | Out-Null
        New-ItemProperty -Path $fileAssociationsKey -Name $extension -Value $ProgId -PropertyType String -Force | Out-Null

        $openWithProgidsKey = "HKCU:\Software\Classes\$extension\OpenWithProgids"
        New-Item -Path $openWithProgidsKey -Force | Out-Null
        New-ItemProperty -Path $openWithProgidsKey -Name $ProgId -Value "" -PropertyType String -Force | Out-Null
    }

    return [pscustomobject]@{
        ShortcutPath = $shortcutPath
        AddedToPath = ($pathEntries -notcontains $AppFullPath)
    }
}

Export-ModuleMember -Function Install-SmpShellIntegration
