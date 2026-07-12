//! Registry-based shell integration (context menus, ProgId, Open With,
//! Default apps, uninstall entry), a 1:1 port of ShellIntegration.psm1's
//! Install-SmpShellIntegration / Uninstall-SmpShellIntegration.

use std::io;
use std::path::Path;

use winreg::enums::HKEY_CURRENT_USER;
use winreg::RegKey;

use crate::config;

pub struct ShellIntegrationPaths {
    pub app_dir: String,
    pub exe_path: String,
}

pub fn install(paths: &ShellIntegrationPaths, display_version: &str) -> io::Result<()> {
    let hkcu = RegKey::predef(HKEY_CURRENT_USER);
    let exe = &paths.exe_path;

    let folder_command = format!("\"{exe}\" --album \"%1\"");
    let file_command = format!("\"{exe}\" \"%1\"");
    // The PowerShell installer wrapped this in a hidden powershell.exe that
    // set the working directory to %V; passing the folder as --album is the
    // native equivalent (same "play this folder" outcome, no PowerShell).
    let background_command = format!("\"{exe}\" --album \"%V\"");

    // Explorer folder context menu
    let (folder_key, _) = hkcu.create_subkey(r"Software\Classes\Directory\shell\SimpleMusicPlayer")?;
    folder_key.set_value("", &format!("Play with {}", config::APP_NAME))?;
    folder_key.set_value("Icon", exe)?;
    let (folder_command_key, _) = folder_key.create_subkey("command")?;
    folder_command_key.set_value("", &folder_command)?;

    // Explorer folder-background context menu
    let (background_key, _) =
        hkcu.create_subkey(r"Software\Classes\Directory\Background\shell\SimpleMusicPlayer")?;
    background_key.set_value("", &format!("Open here with {}", config::APP_NAME))?;
    background_key.set_value("Icon", exe)?;
    let (background_command_key, _) = background_key.create_subkey("command")?;
    background_command_key.set_value("", &background_command)?;

    // ProgId
    let (prog_id_key, _) = hkcu.create_subkey(format!(r"Software\Classes\{}", config::PROG_ID))?;
    prog_id_key.set_value("", &format!("{} media file", config::APP_NAME))?;
    let (prog_id_command_key, _) = prog_id_key.create_subkey(r"shell\open\command")?;
    prog_id_command_key.set_value("", &file_command)?;
    let (prog_id_icon_key, _) = prog_id_key.create_subkey("DefaultIcon")?;
    prog_id_icon_key.set_value("", exe)?;

    // Applications\<exe> (Open With)
    let (application_key, _) =
        hkcu.create_subkey(format!(r"Software\Classes\Applications\{}", config::APPLICATION_KEY_NAME))?;
    application_key.set_value("FriendlyAppName", &config::APP_NAME)?;
    let (application_command_key, _) = application_key.create_subkey(r"shell\open\command")?;
    application_command_key.set_value("", &file_command)?;
    let (supported_types_key, _) = application_key.create_subkey("SupportedTypes")?;

    // Capabilities + Default apps registration
    let (capabilities_key, _) =
        hkcu.create_subkey(format!(r"Software\{}\Capabilities", config::CAPABILITIES_KEY_NAME))?;
    capabilities_key.set_value("ApplicationName", &config::APP_NAME)?;
    capabilities_key.set_value(
        "ApplicationDescription",
        &"Minimal media player for local music and video files.",
    )?;
    let (file_associations_key, _) = capabilities_key.create_subkey("FileAssociations")?;

    let (registered_applications_key, _) = hkcu.create_subkey(r"Software\RegisteredApplications")?;
    registered_applications_key.set_value(
        config::APP_NAME,
        &format!(r"Software\{}\Capabilities", config::CAPABILITIES_KEY_NAME),
    )?;

    for extension in config::supported_extensions() {
        supported_types_key.set_value(&extension, &"")?;
        file_associations_key.set_value(&extension, &config::PROG_ID)?;

        let (open_with_key, _) =
            hkcu.create_subkey(format!(r"Software\Classes\{extension}\OpenWithProgids"))?;
        open_with_key.set_value(config::PROG_ID, &"")?;
    }

    // Uninstall entry (Settings > Apps). UninstallString points back at the
    // binary itself; there is no separate uninstaller script anymore.
    let uninstall_string = format!("\"{exe}\" --uninstall");
    let (uninstall_key, _) = hkcu.create_subkey(format!(
        r"Software\Microsoft\Windows\CurrentVersion\Uninstall\{}",
        config::UNINSTALL_KEY_NAME
    ))?;
    uninstall_key.set_value("DisplayName", &config::APP_NAME)?;
    uninstall_key.set_value("DisplayIcon", exe)?;
    uninstall_key.set_value("InstallLocation", &paths.app_dir)?;
    uninstall_key.set_value("Publisher", &config::PUBLISHER)?;
    uninstall_key.set_value("UninstallString", &uninstall_string)?;
    uninstall_key.set_value("NoModify", &1u32)?;
    uninstall_key.set_value("NoRepair", &1u32)?;
    if !display_version.trim().is_empty() {
        uninstall_key.set_value("DisplayVersion", &display_version)?;
    }

    Ok(())
}

pub fn uninstall() -> io::Result<()> {
    let hkcu = RegKey::predef(HKEY_CURRENT_USER);

    let _ = hkcu.delete_subkey_all(r"Software\Classes\Directory\shell\SimpleMusicPlayer");
    let _ = hkcu.delete_subkey_all(r"Software\Classes\Directory\Background\shell\SimpleMusicPlayer");
    let _ = hkcu.delete_subkey_all(format!(r"Software\Classes\{}", config::PROG_ID));
    let _ = hkcu.delete_subkey_all(format!(
        r"Software\Classes\Applications\{}",
        config::APPLICATION_KEY_NAME
    ));
    let _ = hkcu.delete_subkey_all(format!(r"Software\{}", config::CAPABILITIES_KEY_NAME));
    let _ = hkcu.delete_subkey_all(format!(
        r"Software\Microsoft\Windows\CurrentVersion\Uninstall\{}",
        config::UNINSTALL_KEY_NAME
    ));

    if let Ok(registered) = hkcu.open_subkey_with_flags(
        r"Software\RegisteredApplications",
        winreg::enums::KEY_WRITE,
    ) {
        let _ = registered.delete_value(config::APP_NAME);
    }

    for extension in config::supported_extensions() {
        if let Ok(open_with_key) = hkcu.open_subkey_with_flags(
            format!(r"Software\Classes\{extension}\OpenWithProgids"),
            winreg::enums::KEY_WRITE,
        ) {
            let _ = open_with_key.delete_value(config::PROG_ID);
        }
    }

    Ok(())
}

/// Mirrors AppSetupCoordinator.LooksConfigured: treat the machine as already
/// set up when the file/folder open commands point at this exact exe.
pub fn looks_configured(exe_path: &Path) -> bool {
    let hkcu = RegKey::predef(HKEY_CURRENT_USER);
    let expected_file_command = format!("\"{}\" \"%1\"", exe_path.display());
    let expected_folder_command = format!("\"{}\" --album \"%1\"", exe_path.display());

    let application_matches = hkcu
        .open_subkey(format!(
            r"Software\Classes\Applications\{}\shell\open\command",
            config::APPLICATION_KEY_NAME
        ))
        .and_then(|key| key.get_value::<String, _>(""))
        .map(|value| value.eq_ignore_ascii_case(&expected_file_command))
        .unwrap_or(false);

    let folder_matches = hkcu
        .open_subkey(r"Software\Classes\Directory\shell\SimpleMusicPlayer\command")
        .and_then(|key| key.get_value::<String, _>(""))
        .map(|value| value.eq_ignore_ascii_case(&expected_folder_command))
        .unwrap_or(false);

    application_matches && folder_matches
}
