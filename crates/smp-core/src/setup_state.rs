use std::path::{Path, PathBuf};

use chrono::{DateTime, Local};
use serde::{Deserialize, Serialize};

use crate::history::JsonFileStore;

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase")]
pub struct AppSetupState {
    pub completed_install_path: Option<String>,
    pub completed_at: Option<DateTime<Local>>,
    pub dismissed_install_path: Option<String>,
    pub dismissed_at: Option<DateTime<Local>>,
}

pub struct AppSetupStateStore {
    store: JsonFileStore<AppSetupState>,
}

impl AppSetupStateStore {
    pub fn new() -> Self {
        Self::at_path(setup_state_file_path())
    }

    pub fn at_path(file_path: impl Into<PathBuf>) -> Self {
        Self {
            store: JsonFileStore::new(file_path),
        }
    }

    pub fn load(&self) -> AppSetupState {
        self.store.load()
    }

    pub fn save(&self, state: &AppSetupState) {
        self.store.save(state);
    }

    pub fn mark_completed(&self, install_path: &str) {
        self.save(&AppSetupState {
            completed_install_path: Some(normalize_path(install_path)),
            completed_at: Some(Local::now()),
            dismissed_install_path: None,
            dismissed_at: None,
        });
    }

    pub fn mark_dismissed(&self, install_path: &str) {
        let current = self.load();
        self.save(&AppSetupState {
            completed_install_path: current.completed_install_path,
            completed_at: current.completed_at,
            dismissed_install_path: Some(normalize_path(install_path)),
            dismissed_at: Some(Local::now()),
        });
    }
}

impl Default for AppSetupStateStore {
    fn default() -> Self {
        Self::new()
    }
}

fn setup_state_file_path() -> PathBuf {
    dirs::data_local_dir()
        .unwrap_or_else(std::env::temp_dir)
        .join("SimpleMusicPlayer")
        .join("setup-state.json")
}

/// Absolute, trailing-separator-free form of `path`, for comparing install
/// directories. Inputs are always already-clean absolute directories (the
/// running exe's own folder), so this only needs to make relative inputs
/// absolute and strip a trailing separator -- it does not need to collapse
/// `.`/`..` segments the way .NET's Path.GetFullPath does.
pub fn normalize_path(path: &str) -> String {
    let trimmed = path.trim();
    let candidate = Path::new(trimmed);
    let absolute = if candidate.is_absolute() {
        candidate.to_path_buf()
    } else {
        std::env::current_dir()
            .map(|cwd| cwd.join(candidate))
            .unwrap_or_else(|_| candidate.to_path_buf())
    };

    let mut normalized = absolute.to_string_lossy().to_string();
    while normalized.ends_with('\\') || normalized.ends_with('/') {
        normalized.pop();
    }
    normalized
}

#[cfg(test)]
mod tests {
    use super::*;

    // `D:\App` is only recognized as absolute on Windows; on Unix it would
    // be joined onto the current directory.
    #[test]
    #[cfg(windows)]
    fn normalize_path_strips_trailing_separators() {
        assert_eq!(normalize_path("D:\\App\\"), "D:\\App");
        assert_eq!(normalize_path("D:\\App"), "D:\\App");
    }

    #[test]
    fn mark_dismissed_preserves_previous_completed_fields() {
        let path = std::env::temp_dir().join(format!(
            "smp-core-setup-state-{}-{:?}.json",
            std::process::id(),
            std::time::SystemTime::now()
        ));
        let store = AppSetupStateStore::at_path(&path);

        store.mark_completed("D:\\App");
        store.mark_dismissed("D:\\App");

        let state = store.load();
        assert!(state.completed_install_path.is_some());
        assert!(state.dismissed_install_path.is_some());

        std::fs::remove_file(&path).ok();
    }
}
