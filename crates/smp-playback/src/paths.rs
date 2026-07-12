use std::path::{Path, PathBuf};

/// The directory the running executable lives in -- the Rust analogue of
/// .NET's `AppContext.BaseDirectory`, used to locate `tools/` and `cache/`.
pub(crate) fn app_base_directory() -> PathBuf {
    std::env::current_exe()
        .ok()
        .and_then(|exe| exe.parent().map(|p| p.to_path_buf()))
        .unwrap_or_else(|| PathBuf::from("."))
}

pub(crate) fn is_usable_file(path: &Path) -> bool {
    std::fs::metadata(path)
        .map(|metadata| metadata.is_file() && metadata.len() > 0)
        .unwrap_or(false)
}

pub(crate) fn try_delete(path: &Path) {
    let _ = std::fs::remove_file(path);
}

pub(crate) fn try_delete_directory(path: &Path) {
    let _ = std::fs::remove_dir_all(path);
}
