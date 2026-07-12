//! Start Menu shortcut creation via IShellLinkW/IPersistFile COM interop,
//! replacing the WScript.Shell COM automation in ShellIntegration.psm1.

use std::path::{Path, PathBuf};

use windows::core::{Interface, HSTRING};
use windows::Win32::System::Com::{CoCreateInstance, IPersistFile, CLSCTX_INPROC_SERVER};
use windows::Win32::UI::Shell::{FOLDERID_Programs, IShellLinkW, SHGetKnownFolderPath, ShellLink, KF_FLAG_DEFAULT};

use crate::config;

pub fn shortcut_path() -> windows::core::Result<PathBuf> {
    let programs = unsafe { SHGetKnownFolderPath(&FOLDERID_Programs, KF_FLAG_DEFAULT, None)? };
    let programs = unsafe { programs.to_string() }
        .map_err(|_| windows::core::Error::from(windows::Win32::Foundation::E_FAIL))?;
    Ok(PathBuf::from(programs).join(config::SHORTCUT_NAME))
}

/// Assumes COM is already initialized on this thread (the caller does a
/// CoInitializeEx/OleInitialize once for the whole setup run).
pub fn create_start_menu_shortcut(target_exe: &Path, working_directory: &Path) -> windows::core::Result<PathBuf> {
    let link_path = shortcut_path()?;

    unsafe {
        let shell_link: IShellLinkW = CoCreateInstance(&ShellLink, None, CLSCTX_INPROC_SERVER)?;
        shell_link.SetPath(&HSTRING::from(target_exe.as_os_str()))?;
        shell_link.SetWorkingDirectory(&HSTRING::from(working_directory.as_os_str()))?;
        shell_link.SetIconLocation(&HSTRING::from(target_exe.as_os_str()), 0)?;

        let persist_file: IPersistFile = shell_link.cast()?;
        persist_file.Save(&HSTRING::from(link_path.as_os_str()), true)?;
    }

    Ok(link_path)
}

pub fn remove_start_menu_shortcut() -> windows::core::Result<()> {
    let link_path = shortcut_path()?;
    if link_path.exists() {
        let _ = std::fs::remove_file(&link_path);
    }
    Ok(())
}
