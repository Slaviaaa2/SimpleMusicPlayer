//! User PATH management via HKCU\Environment, replacing
//! [Environment]::SetEnvironmentVariable(..., "User") in ShellIntegration.psm1.
//! .NET broadcasts WM_SETTINGCHANGE internally when writing user env vars;
//! writing the registry directly does not, so the broadcast is reproduced
//! explicitly or Explorer and new shells would never pick up the change.

use std::io;

use winreg::enums::{HKEY_CURRENT_USER, KEY_READ, KEY_WRITE};
use winreg::RegKey;

use smp_core::normalize_path;

const ENVIRONMENT_KEY: &str = "Environment";
const PATH_VALUE: &str = "Path";

pub fn user_path_contains(directory: &str) -> bool {
    let normalized = normalize_path(directory);
    read_user_path()
        .map(|path| {
            path.split(';')
                .map(str::trim)
                .filter(|entry| !entry.is_empty())
                .any(|entry| normalize_path(entry).eq_ignore_ascii_case(&normalized))
        })
        .unwrap_or(false)
}

pub fn add_to_user_path(directory: &str) -> io::Result<()> {
    if user_path_contains(directory) {
        return Ok(());
    }

    let current = read_user_path().unwrap_or_default();
    let updated = if current.trim().is_empty() {
        directory.to_string()
    } else {
        format!("{current};{directory}")
    };

    write_user_path(&updated)?;
    broadcast_environment_change();
    Ok(())
}

pub fn remove_from_user_path(directory: &str) -> io::Result<()> {
    let Ok(current) = read_user_path() else {
        return Ok(());
    };

    let normalized = normalize_path(directory);
    let entries: Vec<&str> = current
        .split(';')
        .map(str::trim)
        .filter(|entry| !entry.is_empty() && !normalize_path(entry).eq_ignore_ascii_case(&normalized))
        .collect();

    write_user_path(&entries.join(";"))?;
    broadcast_environment_change();
    Ok(())
}

fn read_user_path() -> io::Result<String> {
    RegKey::predef(HKEY_CURRENT_USER)
        .open_subkey_with_flags(ENVIRONMENT_KEY, KEY_READ)?
        .get_value(PATH_VALUE)
}

fn write_user_path(value: &str) -> io::Result<()> {
    RegKey::predef(HKEY_CURRENT_USER)
        .open_subkey_with_flags(ENVIRONMENT_KEY, KEY_WRITE)?
        .set_value(PATH_VALUE, &value)
}

fn broadcast_environment_change() {
    use windows::core::w;
    use windows::Win32::Foundation::{LPARAM, WPARAM};
    use windows::Win32::UI::WindowsAndMessaging::{
        SendMessageTimeoutW, HWND_BROADCAST, SMTO_ABORTIFHUNG, WM_SETTINGCHANGE,
    };

    unsafe {
        SendMessageTimeoutW(
            HWND_BROADCAST,
            WM_SETTINGCHANGE,
            WPARAM(0),
            LPARAM(w!("Environment").as_ptr() as isize),
            SMTO_ABORTIFHUNG,
            5000,
            None,
        );
    }
}
