//! Native replacement for the PowerShell installer/uninstaller scripts
//! (Install-Release.ps1 / Uninstall-Release.ps1 / modules/*.psm1).
//! Windows-only in effect; on other platforms everything is a no-op, matching
//! AppSetupCoordinator.cs's OperatingSystem.IsWindows() gates.

#[cfg(windows)]
mod config;
#[cfg(windows)]
mod path_env;
#[cfg(windows)]
mod shell_integration;
#[cfg(windows)]
mod shortcut;
#[cfg(windows)]
mod tools;

pub struct SetupRunResult {
    pub success: bool,
    pub message: String,
}

#[derive(Default, Clone, Copy)]
pub struct UninstallOptions {
    pub remove_user_data: bool,
    pub remove_cache: bool,
    pub remove_bundled_tools: bool,
    pub remove_app_directory: bool,
}

#[cfg(windows)]
mod windows_impl {
    use std::path::PathBuf;

    use smp_core::{normalize_path, AppSetupStateStore};

    use super::{SetupRunResult, UninstallOptions};
    use crate::{config, path_env, shell_integration, shortcut, tools};

    fn current_exe() -> Option<PathBuf> {
        std::env::current_exe().ok()
    }

    fn app_dir_of(exe: &std::path::Path) -> Option<PathBuf> {
        exe.parent().map(|p| p.to_path_buf())
    }

    /// Mirrors AppSetupCoordinator.ShouldOfferSetup. The C# version also
    /// required Install-SimpleMusicPlayer.ps1 to exist next to the exe (its
    /// proxy for "this is an extracted release, not a dev build"); the
    /// native equivalent is a debug/release distinction: never offer from
    /// debug builds.
    pub fn should_offer_setup() -> bool {
        if cfg!(debug_assertions) {
            return false;
        }

        let Some(exe) = current_exe() else {
            return false;
        };
        if !exe
            .file_name()
            .and_then(|n| n.to_str())
            .map(|n| n.eq_ignore_ascii_case(config::EXE_NAME))
            .unwrap_or(false)
        {
            return false;
        }
        let Some(app_dir) = app_dir_of(&exe) else {
            return false;
        };

        let install_path = normalize_path(&app_dir.to_string_lossy());
        let store = AppSetupStateStore::new();
        let state = store.load();

        if state
            .completed_install_path
            .as_deref()
            .map(|p| p.eq_ignore_ascii_case(&install_path))
            .unwrap_or(false)
        {
            return false;
        }

        if path_env::user_path_contains(&install_path) && shell_integration::looks_configured(&exe) {
            store.mark_completed(&install_path);
            return false;
        }

        !state
            .dismissed_install_path
            .as_deref()
            .map(|p| p.eq_ignore_ascii_case(&install_path))
            .unwrap_or(false)
    }

    pub fn mark_dismissed() {
        if let Some(app_dir) = current_exe().as_deref().and_then(app_dir_of) {
            AppSetupStateStore::new().mark_dismissed(&app_dir.to_string_lossy());
        }
    }

    /// Blocking (network + registry + COM); run on a worker thread.
    pub fn run_setup(redownload_tools: bool, display_version: &str) -> SetupRunResult {
        let Some(exe) = current_exe() else {
            return fail("Could not determine the running executable path.");
        };
        let Some(app_dir) = app_dir_of(&exe) else {
            return fail("Could not determine the application directory.");
        };

        unsafe {
            use windows::Win32::System::Com::{CoInitializeEx, COINIT_APARTMENTTHREADED};
            // S_FALSE (already initialized) is fine; only real failures matter.
            let hr = CoInitializeEx(None, COINIT_APARTMENTTHREADED);
            if hr.is_err() && hr != windows::Win32::Foundation::S_FALSE {
                return fail(&format!("COM initialization failed: {hr}"));
            }
        }

        if let Err(err) = shortcut::create_start_menu_shortcut(&exe, &app_dir) {
            return fail(&format!("Could not create the Start Menu shortcut: {err}"));
        }

        if let Err(err) = path_env::add_to_user_path(&app_dir.to_string_lossy()) {
            return fail(&format!("Could not update the user PATH: {err}"));
        }

        let paths = shell_integration::ShellIntegrationPaths {
            app_dir: app_dir.to_string_lossy().to_string(),
            exe_path: exe.to_string_lossy().to_string(),
        };
        if let Err(err) = shell_integration::install(&paths, display_version) {
            return fail(&format!("Could not register shell integration: {err}"));
        }

        let tool_message = match tools::install_bundled_tools(&app_dir, redownload_tools) {
            Ok(report) => {
                if report.installed.is_empty() {
                    "Bundled tools were already present.".to_string()
                } else {
                    format!("Downloaded tools: {}.", report.installed.join(", "))
                }
            }
            Err(err) => format!("Shell integration finished, but tool download failed: {err}"),
        };

        AppSetupStateStore::new().mark_completed(&app_dir.to_string_lossy());
        SetupRunResult { success: true, message: tool_message }
    }

    /// Blocking; called from the `--uninstall` CLI path before the UI starts.
    pub fn run_uninstall(options: UninstallOptions) -> SetupRunResult {
        let Some(exe) = current_exe() else {
            return fail("Could not determine the running executable path.");
        };
        let Some(app_dir) = app_dir_of(&exe) else {
            return fail("Could not determine the application directory.");
        };

        let _ = shortcut::remove_start_menu_shortcut();
        let _ = path_env::remove_from_user_path(&app_dir.to_string_lossy());
        let _ = shell_integration::uninstall();

        // Remove setup state (and optionally all local app data), matching
        // SetupState.psm1's Remove-SmpSetupState -RemoveUserData behavior.
        if let Some(data_dir) = dirs::data_local_dir().map(|d| d.join("SimpleMusicPlayer")) {
            if options.remove_user_data {
                let _ = std::fs::remove_dir_all(&data_dir);
            } else {
                let _ = std::fs::remove_file(data_dir.join("setup-state.json"));
            }
        }

        if options.remove_cache {
            let _ = std::fs::remove_dir_all(app_dir.join("cache"));
        }

        if options.remove_bundled_tools {
            let _ = tools::remove_bundled_tools(&app_dir);
        }

        if options.remove_app_directory {
            if !exe.is_file() {
                return fail("Refusing to remove the app directory: the executable was not found in it.");
            }
            schedule_app_directory_removal(&app_dir);
        }

        SetupRunResult {
            success: true,
            message: "Uninstall complete.".to_string(),
        }
    }

    /// The running exe can't delete its own directory, so removal is handed
    /// to a detached cmd.exe that waits for this process to exit first
    /// (same trick as Uninstall-Release.ps1's scheduled delete script).
    fn schedule_app_directory_removal(app_dir: &std::path::Path) {
        let dir = app_dir.to_string_lossy().to_string();
        let _ = std::process::Command::new("cmd")
            .args([
                "/C",
                &format!("ping -n 3 127.0.0.1 > nul & rmdir /S /Q \"{dir}\""),
            ])
            .creation_flags_detached()
            .spawn();
    }

    trait DetachedCommand {
        fn creation_flags_detached(&mut self) -> &mut Self;
    }

    impl DetachedCommand for std::process::Command {
        fn creation_flags_detached(&mut self) -> &mut Self {
            use std::os::windows::process::CommandExt;
            const CREATE_NO_WINDOW: u32 = 0x0800_0000;
            const DETACHED_PROCESS: u32 = 0x0000_0008;
            self.creation_flags(CREATE_NO_WINDOW | DETACHED_PROCESS)
        }
    }

    fn fail(message: &str) -> SetupRunResult {
        SetupRunResult { success: false, message: message.to_string() }
    }
}

#[cfg(windows)]
pub use windows_impl::{mark_dismissed, run_setup, run_uninstall, should_offer_setup};

#[cfg(not(windows))]
pub fn should_offer_setup() -> bool {
    false
}

#[cfg(not(windows))]
pub fn mark_dismissed() {}

#[cfg(not(windows))]
pub fn run_setup(_redownload_tools: bool, _display_version: &str) -> SetupRunResult {
    SetupRunResult {
        success: false,
        message: "First-time shell integration is only available on Windows.".to_string(),
    }
}

#[cfg(not(windows))]
pub fn run_uninstall(_options: UninstallOptions) -> SetupRunResult {
    SetupRunResult {
        success: false,
        message: "Uninstall is only available on Windows.".to_string(),
    }
}
