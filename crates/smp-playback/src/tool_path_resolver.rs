use std::path::PathBuf;

use crate::paths::app_base_directory;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ToolResolutionSource {
    NotFound,
    Bundled,
    Path,
}

pub struct ToolResolution {
    pub tool_name: String,
    pub executable_path: Option<PathBuf>,
    pub source: ToolResolutionSource,
    pub searched_paths: Vec<PathBuf>,
}

pub fn resolve_executable_path(tool_name: &str) -> Option<PathBuf> {
    resolve_tool(tool_name).executable_path
}

/// Mirrors ToolPathResolver.cs: looks under `tools/<name>/` and `tools/` next
/// to the running executable before falling back to PATH.
pub fn resolve_tool(tool_name: &str) -> ToolResolution {
    let mut searched_paths = Vec::new();

    for (candidate, source) in
        enumerate_local_tool_paths(tool_name).chain(enumerate_path_tool_paths(tool_name))
    {
        searched_paths.push(candidate.clone());
        if candidate.is_file() {
            return ToolResolution {
                tool_name: tool_name.to_string(),
                executable_path: Some(candidate),
                source,
                searched_paths,
            };
        }
    }

    ToolResolution {
        tool_name: tool_name.to_string(),
        executable_path: None,
        source: ToolResolutionSource::NotFound,
        searched_paths,
    }
}

fn enumerate_executable_names(tool_name: &str) -> Vec<String> {
    let mut names = vec![tool_name.to_string()];
    if cfg!(windows) {
        names.push(format!("{tool_name}.exe"));
        names.push(format!("{tool_name}.cmd"));
        names.push(format!("{tool_name}.bat"));
    }
    names
}

fn enumerate_local_tool_paths(
    tool_name: &str,
) -> impl Iterator<Item = (PathBuf, ToolResolutionSource)> {
    let base = app_base_directory();
    let tool_name = tool_name.to_string();
    enumerate_executable_names(&tool_name).into_iter().flat_map(move |name| {
        [
            base.join("tools").join(&tool_name).join(&name),
            base.join("tools").join(&name),
        ]
        .into_iter()
        .map(|path| (path, ToolResolutionSource::Bundled))
    })
}

fn enumerate_path_tool_paths(
    tool_name: &str,
) -> impl Iterator<Item = (PathBuf, ToolResolutionSource)> {
    let names = enumerate_executable_names(tool_name);
    let dirs: Vec<PathBuf> = std::env::var_os("PATH")
        .map(|value| std::env::split_paths(&value).collect())
        .unwrap_or_default();

    dirs.into_iter().flat_map(move |dir| {
        names
            .clone()
            .into_iter()
            .map(move |name| (dir.join(name), ToolResolutionSource::Path))
            .collect::<Vec<_>>()
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn unknown_tool_resolves_to_not_found() {
        let resolution = resolve_tool("definitely-not-a-real-tool-xyz");
        assert_eq!(resolution.source, ToolResolutionSource::NotFound);
        assert!(resolution.executable_path.is_none());
        assert!(!resolution.searched_paths.is_empty());
    }

    #[cfg(windows)]
    #[test]
    fn finds_cmd_on_path() {
        let resolution = resolve_tool("cmd");
        assert_eq!(resolution.source, ToolResolutionSource::Path);
        assert!(resolution.executable_path.is_some());
    }
}
