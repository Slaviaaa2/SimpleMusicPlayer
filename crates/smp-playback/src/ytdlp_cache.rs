use std::collections::HashMap;
use std::fmt;
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex};

use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use tokio_util::sync::CancellationToken;
use url::Url;

use smp_core::build_failure_details;

use crate::external_process::{ExternalProcessRunner, RunError};
use crate::js_runtime_resolver::{self, JavaScriptRuntimeSelection};
use crate::paths::{app_base_directory, is_usable_file, try_delete, try_delete_directory};
use crate::tool_path_resolver;

const DOWNLOAD_FILE_PREFIX: &str = "audio";
const METADATA_FILE_NAME: &str = "entry.json";
const NOT_AVAILABLE_MESSAGE: &str = "yt-dlp was not found in PATH or the bundled tools directory.";

#[derive(Debug)]
pub enum YtDlpError {
    Cancelled,
    Message(String),
}

impl fmt::Display for YtDlpError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            YtDlpError::Cancelled => write!(f, "the yt-dlp operation was cancelled"),
            YtDlpError::Message(message) => write!(f, "{message}"),
        }
    }
}

impl std::error::Error for YtDlpError {}

pub struct CachedAudioResult {
    pub file_path: String,
    pub title: String,
    pub source_url: String,
    pub was_cached: bool,
}

pub struct YtDlpPlaylistEntry {
    pub url: String,
    pub title: Option<String>,
}

pub struct YtDlpPlaylistResult {
    pub url: String,
    pub title: String,
    pub entries: Vec<YtDlpPlaylistEntry>,
}

#[derive(Serialize, Deserialize)]
#[serde(rename_all = "PascalCase")]
struct CachedAudioEntry {
    url: String,
    title: String,
}

#[derive(Default)]
struct YtDlpMetadata {
    id: Option<String>,
    title: Option<String>,
}

struct YtDlpProcessResult {
    exit_code: i32,
    stdout: String,
    stderr: String,
}

struct MetadataOutcome {
    success: bool,
    metadata: YtDlpMetadata,
    stdout: String,
    stderr: String,
}

struct PlaylistMetadataOutcome {
    success: bool,
    playlist: YtDlpPlaylistResult,
    stdout: String,
    stderr: String,
}

struct DownloadAttempt {
    result: YtDlpProcessResult,
    file_path: Option<String>,
}

pub struct YtDlpAudioCache {
    ytdlp_path: Option<PathBuf>,
    ffmpeg_path: Option<PathBuf>,
    js_runtime: Option<JavaScriptRuntimeSelection>,
    download_locks: Mutex<HashMap<String, Arc<tokio::sync::Mutex<()>>>>,
    update_lock: tokio::sync::Mutex<()>,
    process_runner: ExternalProcessRunner,
    cache_directory: PathBuf,
}

impl YtDlpAudioCache {
    pub fn new() -> Self {
        Self {
            ytdlp_path: tool_path_resolver::resolve_executable_path("yt-dlp"),
            ffmpeg_path: tool_path_resolver::resolve_executable_path("ffmpeg"),
            js_runtime: js_runtime_resolver::resolve_for_ytdlp(),
            download_locks: Mutex::new(HashMap::new()),
            update_lock: tokio::sync::Mutex::new(()),
            process_runner: ExternalProcessRunner::new(),
            cache_directory: app_base_directory().join("cache").join("yt-dlp"),
        }
    }

    pub fn is_available(&self) -> bool {
        self.ytdlp_path.is_some()
    }

    pub fn has_supported_javascript_runtime(&self) -> bool {
        self.js_runtime.is_some()
    }

    pub fn is_supported_url(&self, source: &str) -> bool {
        Url::parse(source)
            .map(|url| url.scheme() == "http" || url.scheme() == "https")
            .unwrap_or(false)
    }

    pub fn is_youtube_playlist_page_url(&self, source: &str) -> bool {
        let Ok(url) = Url::parse(source) else {
            return false;
        };
        if !is_likely_youtube_url(source) {
            return false;
        }

        let has_playlist_id = url
            .query_pairs()
            .any(|(key, value)| key.eq_ignore_ascii_case("list") && !value.trim().is_empty());
        if !has_playlist_id {
            return false;
        }

        url.path().eq_ignore_ascii_case("/playlist")
    }

    pub async fn get_playlist(
        &self,
        source_url: &str,
        cancellation: &CancellationToken,
    ) -> Result<YtDlpPlaylistResult, YtDlpError> {
        if !self.is_available() {
            return Err(YtDlpError::Message(NOT_AVAILABLE_MESSAGE.to_string()));
        }

        let normalized_url = normalize_url(source_url)?;
        let mut playlist_result = self.get_playlist_metadata(&normalized_url, cancellation).await?;

        if !playlist_result.success {
            let update_result = if is_likely_youtube_url(&normalized_url) && self.has_supported_javascript_runtime() {
                let update_result = self.try_update_ytdlp(cancellation).await?;
                playlist_result = self.get_playlist_metadata(&normalized_url, cancellation).await?;
                update_result
            } else {
                None
            };

            if !playlist_result.success {
                return Err(self.build_failure_message(
                    &normalized_url,
                    &playlist_result.stderr,
                    &playlist_result.stdout,
                    update_result.as_ref(),
                ));
            }
        }

        if playlist_result.playlist.entries.is_empty() {
            return Err(YtDlpError::Message(
                "yt-dlp found this playlist, but it did not contain playable entries.".to_string(),
            ));
        }

        Ok(playlist_result.playlist)
    }

    pub async fn get_or_download(
        &self,
        source_url: &str,
        cancellation: &CancellationToken,
    ) -> Result<CachedAudioResult, YtDlpError> {
        if !self.is_available() {
            return Err(YtDlpError::Message(NOT_AVAILABLE_MESSAGE.to_string()));
        }

        let normalized_url = normalize_url(source_url)?;
        let cache_key = build_url_cache_key(&normalized_url);
        let entry_directory = self.cache_directory.join(&cache_key);

        let lock = {
            let mut locks = self.download_locks.lock().unwrap();
            locks
                .entry(cache_key.clone())
                .or_insert_with(|| Arc::new(tokio::sync::Mutex::new(())))
                .clone()
        };
        let _guard = lock.lock().await;

        std::fs::create_dir_all(&entry_directory)
            .map_err(|err| YtDlpError::Message(err.to_string()))?;
        cleanup_stale_work_directories(&entry_directory);

        let metadata_path = entry_directory.join(METADATA_FILE_NAME);
        if let Some(cached_file_path) = find_downloaded_file(&entry_directory) {
            let cached_entry = read_metadata(&metadata_path);
            return Ok(CachedAudioResult {
                file_path: cached_file_path,
                title: cached_entry.map(|entry| entry.title).unwrap_or_else(|| normalized_url.clone()),
                source_url: normalized_url,
                was_cached: true,
            });
        }

        let mut metadata_result = self.get_metadata(&normalized_url, cancellation).await?;
        if !metadata_result.success {
            let update_result = if is_likely_youtube_url(&normalized_url) && self.has_supported_javascript_runtime() {
                let update_result = self.try_update_ytdlp(cancellation).await?;
                metadata_result = self.get_metadata(&normalized_url, cancellation).await?;
                update_result
            } else {
                None
            };

            if !metadata_result.success {
                return Err(self.build_failure_message(
                    &normalized_url,
                    &metadata_result.stderr,
                    &metadata_result.stdout,
                    update_result.as_ref(),
                ));
            }
        }

        if let Some(id) = metadata_result.metadata.id.as_deref().filter(|id| !id.trim().is_empty()) {
            cleanup_downloaded_files(&entry_directory, Some(id));
        }

        let title = metadata_result.metadata.title.unwrap_or_else(|| normalized_url.clone());
        let first_attempt = self
            .download_to_cache(&normalized_url, &entry_directory, cancellation)
            .await?;
        if let Some(file_path) = first_attempt.file_path {
            write_metadata(&metadata_path, &CachedAudioEntry { url: normalized_url.clone(), title: title.clone() });
            return Ok(CachedAudioResult { file_path, title, source_url: normalized_url, was_cached: false });
        }

        cleanup_downloaded_files(&entry_directory, None);

        if is_likely_youtube_url(&normalized_url) && self.has_supported_javascript_runtime() {
            let update_result = self.try_update_ytdlp(cancellation).await?;
            let retry_attempt = self
                .download_to_cache(&normalized_url, &entry_directory, cancellation)
                .await?;
            if let Some(file_path) = retry_attempt.file_path {
                write_metadata(&metadata_path, &CachedAudioEntry { url: normalized_url.clone(), title: title.clone() });
                return Ok(CachedAudioResult { file_path, title, source_url: normalized_url, was_cached: false });
            }

            cleanup_downloaded_files(&entry_directory, None);
            return Err(self.build_failure_message(
                &normalized_url,
                &retry_attempt.result.stderr,
                &retry_attempt.result.stdout,
                update_result.as_ref(),
            ));
        }

        Err(self.build_failure_message(
            &normalized_url,
            &first_attempt.result.stderr,
            &first_attempt.result.stdout,
            None,
        ))
    }

    async fn get_metadata(
        &self,
        normalized_url: &str,
        cancellation: &CancellationToken,
    ) -> Result<MetadataOutcome, YtDlpError> {
        let mut args = vec!["--no-playlist".to_string(), "--dump-single-json".to_string()];
        self.add_common_network_arguments(&mut args, normalized_url);
        args.push(normalized_url.to_string());

        let result = self.run_ytdlp(args, cancellation).await?;
        if result.exit_code != 0 {
            return Ok(MetadataOutcome {
                success: false,
                metadata: YtDlpMetadata::default(),
                stdout: result.stdout,
                stderr: result.stderr,
            });
        }

        Ok(MetadataOutcome {
            success: true,
            metadata: parse_metadata(&result.stdout),
            stdout: result.stdout,
            stderr: result.stderr,
        })
    }

    async fn get_playlist_metadata(
        &self,
        normalized_url: &str,
        cancellation: &CancellationToken,
    ) -> Result<PlaylistMetadataOutcome, YtDlpError> {
        let mut args = vec!["--flat-playlist".to_string(), "--dump-single-json".to_string()];
        self.add_common_network_arguments(&mut args, normalized_url);
        args.push(normalized_url.to_string());

        let result = self.run_ytdlp(args, cancellation).await?;
        if result.exit_code != 0 {
            return Ok(PlaylistMetadataOutcome {
                success: false,
                playlist: YtDlpPlaylistResult {
                    url: normalized_url.to_string(),
                    title: normalized_url.to_string(),
                    entries: Vec::new(),
                },
                stdout: result.stdout,
                stderr: result.stderr,
            });
        }

        Ok(PlaylistMetadataOutcome {
            success: true,
            playlist: parse_playlist_metadata(normalized_url, &result.stdout),
            stdout: result.stdout,
            stderr: result.stderr,
        })
    }

    async fn download_to_cache(
        &self,
        normalized_url: &str,
        entry_directory: &Path,
        cancellation: &CancellationToken,
    ) -> Result<DownloadAttempt, YtDlpError> {
        let work_directory = create_work_directory(entry_directory)?;

        let outcome = self.run_download(normalized_url, &work_directory, cancellation).await;
        let attempt = outcome.map(|result| {
            if result.exit_code != 0 {
                return DownloadAttempt { result, file_path: None };
            }

            match find_downloaded_file(&work_directory) {
                Some(downloaded_file) => match commit_downloaded_file(&downloaded_file, entry_directory) {
                    Ok(committed) => DownloadAttempt { result, file_path: Some(committed) },
                    Err(_) => DownloadAttempt { result, file_path: None },
                },
                None => DownloadAttempt { result, file_path: None },
            }
        });

        try_delete_directory(&work_directory);
        attempt
    }

    async fn run_download(
        &self,
        normalized_url: &str,
        output_directory: &Path,
        cancellation: &CancellationToken,
    ) -> Result<YtDlpProcessResult, YtDlpError> {
        let mut args = vec![
            "--no-playlist".to_string(),
            "--no-progress".to_string(),
            "--format".to_string(),
            "bestaudio/best".to_string(),
            "--output".to_string(),
            output_directory
                .join(format!("{DOWNLOAD_FILE_PREFIX}.%(ext)s"))
                .to_string_lossy()
                .to_string(),
        ];

        if let Some(ffmpeg_path) = &self.ffmpeg_path {
            args.push("--ffmpeg-location".to_string());
            args.push(ffmpeg_path.to_string_lossy().to_string());
            args.push("--extract-audio".to_string());
            args.push("--audio-format".to_string());
            args.push("mp3".to_string());
            args.push("--audio-quality".to_string());
            args.push("0".to_string());
            args.push("--embed-metadata".to_string());
        }

        self.add_common_network_arguments(&mut args, normalized_url);
        args.push(normalized_url.to_string());

        self.run_ytdlp(args, cancellation).await
    }

    async fn run_ytdlp(
        &self,
        args: Vec<String>,
        cancellation: &CancellationToken,
    ) -> Result<YtDlpProcessResult, YtDlpError> {
        let ytdlp_path = self
            .ytdlp_path
            .as_ref()
            .ok_or_else(|| YtDlpError::Message(NOT_AVAILABLE_MESSAGE.to_string()))?;

        match self
            .process_runner
            .run(&ytdlp_path.to_string_lossy(), args, None, cancellation)
            .await
        {
            Ok(result) => Ok(YtDlpProcessResult {
                exit_code: result.exit_code,
                stdout: result.stdout,
                stderr: result.stderr,
            }),
            Err(RunError::Cancelled) => Err(YtDlpError::Cancelled),
            Err(RunError::Io(err)) => Err(YtDlpError::Message(err.to_string())),
        }
    }

    fn add_common_network_arguments(&self, args: &mut Vec<String>, normalized_url: &str) {
        if let Some(js_runtime) = &self.js_runtime {
            args.push("--js-runtimes".to_string());
            args.push(js_runtime.to_ytdlp_argument());
        }

        if is_likely_youtube_url(normalized_url) {
            args.push("--extractor-args".to_string());
            args.push("youtube:player_client=default,web_safari".to_string());
        }
    }

    async fn try_update_ytdlp(
        &self,
        cancellation: &CancellationToken,
    ) -> Result<Option<YtDlpProcessResult>, YtDlpError> {
        let Some(ytdlp_path) = self.ytdlp_path.as_ref() else {
            return Ok(None);
        };

        let _guard = self.update_lock.lock().await;
        match self
            .process_runner
            .run(&ytdlp_path.to_string_lossy(), ["-U"], None, cancellation)
            .await
        {
            Ok(result) => Ok(Some(YtDlpProcessResult {
                exit_code: result.exit_code,
                stdout: result.stdout,
                stderr: result.stderr,
            })),
            Err(RunError::Cancelled) => Err(YtDlpError::Cancelled),
            Err(RunError::Io(err)) => Ok(Some(YtDlpProcessResult {
                exit_code: -1,
                stdout: String::new(),
                stderr: err.to_string(),
            })),
        }
    }

    fn build_failure_message(
        &self,
        source_url: &str,
        stderr: &str,
        stdout: &str,
        update_result: Option<&YtDlpProcessResult>,
    ) -> YtDlpError {
        if is_missing_javascript_runtime_failure(stderr, stdout)
            || (is_likely_youtube_url(source_url) && !self.has_supported_javascript_runtime())
        {
            let details = build_failure_details(stderr, stdout);
            return YtDlpError::Message(format!(
                "yt-dlp needs a supported JavaScript runtime for this site. Install {}, or add one to PATH / bundled tools.{details}",
                js_runtime_resolver::supported_runtime_display_text()
            ));
        }

        let generic_details = build_failure_details(stderr, stdout);
        let update_details = build_update_details(update_result);
        YtDlpError::Message(format!("yt-dlp could not fetch this URL.{generic_details}{update_details}"))
    }
}

impl Default for YtDlpAudioCache {
    fn default() -> Self {
        Self::new()
    }
}

fn build_update_details(update_result: Option<&YtDlpProcessResult>) -> String {
    let Some(update_result) = update_result else {
        return String::new();
    };

    if update_result.exit_code == 0 {
        return " yt-dlp was updated and the URL was retried.".to_string();
    }

    let details = build_failure_details(&update_result.stderr, &update_result.stdout);
    if details.trim().is_empty() {
        " yt-dlp update was attempted but failed.".to_string()
    } else {
        format!(" yt-dlp update was attempted but failed: {details}")
    }
}

fn normalize_url(source_url: &str) -> Result<String, YtDlpError> {
    Url::parse(source_url)
        .map(|url| url.to_string())
        .map_err(|err| YtDlpError::Message(format!("'{source_url}' is not a valid URL: {err}")))
}

fn build_url_cache_key(source_url: &str) -> String {
    hex::encode(Sha256::digest(source_url.as_bytes()))
}

fn find_downloaded_file(entry_directory: &Path) -> Option<String> {
    let entries = std::fs::read_dir(entry_directory).ok()?;
    let prefix = format!("{DOWNLOAD_FILE_PREFIX}.");

    for entry in entries.flatten() {
        let path = entry.path();
        if !path.is_file() {
            continue;
        }

        let Some(file_name) = path.file_name().and_then(|n| n.to_str()) else {
            continue;
        };
        if !file_name.starts_with(&prefix) {
            continue;
        }

        let lower = file_name.to_ascii_lowercase();
        if lower.ends_with(".json") || lower.ends_with(".part") {
            continue;
        }

        if is_usable_file(&path) {
            return Some(path.to_string_lossy().to_string());
        }
    }

    None
}

fn create_work_directory(entry_directory: &Path) -> Result<PathBuf, YtDlpError> {
    let work_directory = entry_directory.join(format!("work-{}", uuid::Uuid::new_v4().simple()));
    std::fs::create_dir_all(&work_directory).map_err(|err| YtDlpError::Message(err.to_string()))?;
    Ok(work_directory)
}

fn commit_downloaded_file(downloaded_file_path: &str, entry_directory: &Path) -> std::io::Result<String> {
    let extension = Path::new(downloaded_file_path)
        .extension()
        .and_then(|ext| ext.to_str())
        .map(|ext| format!(".{ext}"))
        .unwrap_or_default();
    let target_path = entry_directory.join(format!("{DOWNLOAD_FILE_PREFIX}{extension}"));
    try_delete(&target_path);
    std::fs::rename(downloaded_file_path, &target_path)?;
    Ok(target_path.to_string_lossy().to_string())
}

fn read_metadata(metadata_path: &Path) -> Option<CachedAudioEntry> {
    let content = std::fs::read_to_string(metadata_path).ok()?;
    serde_json::from_str(&content).ok()
}

fn write_metadata(metadata_path: &Path, entry: &CachedAudioEntry) {
    if let Ok(json) = serde_json::to_string_pretty(entry) {
        let _ = std::fs::write(metadata_path, json);
    }
}

fn parse_metadata(stdout: &str) -> YtDlpMetadata {
    let Ok(value) = serde_json::from_str::<serde_json::Value>(stdout) else {
        return YtDlpMetadata::default();
    };

    YtDlpMetadata {
        id: value.get("id").and_then(|v| v.as_str()).map(str::to_string),
        title: value.get("title").and_then(|v| v.as_str()).map(str::to_string),
    }
}

fn parse_playlist_metadata(source_url: &str, stdout: &str) -> YtDlpPlaylistResult {
    let Ok(value) = serde_json::from_str::<serde_json::Value>(stdout) else {
        return YtDlpPlaylistResult {
            url: source_url.to_string(),
            title: source_url.to_string(),
            entries: Vec::new(),
        };
    };

    let title = value
        .get("title")
        .and_then(|v| v.as_str())
        .map(str::to_string)
        .unwrap_or_else(|| source_url.to_string());

    let mut entries = Vec::new();
    if let Some(entries_value) = value.get("entries").and_then(|v| v.as_array()) {
        for entry_value in entries_value {
            let entry_title = entry_value.get("title").and_then(|v| v.as_str()).map(str::to_string);
            if let Some(entry_url) = build_playlist_entry_url(entry_value) {
                entries.push(YtDlpPlaylistEntry { url: entry_url, title: entry_title });
            }
        }
    }

    YtDlpPlaylistResult { url: source_url.to_string(), title, entries }
}

fn build_playlist_entry_url(entry_value: &serde_json::Value) -> Option<String> {
    if let Some(webpage_url) = non_blank_str(entry_value, "webpage_url") {
        return Some(webpage_url);
    }

    if let Some(original_url) = non_blank_str(entry_value, "original_url") {
        return Some(original_url);
    }

    if let Some(url) = non_blank_str(entry_value, "url") {
        return Some(if Url::parse(&url).is_ok() {
            url
        } else {
            format!("https://www.youtube.com/watch?v={url}")
        });
    }

    let id = non_blank_str(entry_value, "id")?;
    Some(format!("https://www.youtube.com/watch?v={id}"))
}

fn non_blank_str(value: &serde_json::Value, key: &str) -> Option<String> {
    value
        .get(key)
        .and_then(|v| v.as_str())
        .map(str::trim)
        .filter(|s| !s.is_empty())
        .map(str::to_string)
}

fn is_likely_youtube_url(source_url: &str) -> bool {
    let Ok(url) = Url::parse(source_url) else {
        return false;
    };
    let host = url.host_str().unwrap_or("").to_lowercase();
    host.contains("youtube.com") || host.contains("youtu.be")
}

fn is_missing_javascript_runtime_failure(stderr: &str, stdout: &str) -> bool {
    let combined = format!("{stderr}\n{stdout}").to_lowercase();
    combined.contains("no supported javascript runtime could be found")
        || combined.contains("youtube extraction without a js runtime has been deprecated")
}

fn cleanup_downloaded_files(entry_directory: &Path, id: Option<&str>) {
    let Ok(entries) = std::fs::read_dir(entry_directory) else {
        return;
    };

    for entry in entries.flatten() {
        let path = entry.path();
        if !path.is_file() {
            continue;
        }

        let Some(file_name) = path.file_name().and_then(|n| n.to_str()) else {
            continue;
        };

        let matches = match id {
            Some(id) if !id.trim().is_empty() => file_name.contains(id),
            _ => file_name.starts_with(DOWNLOAD_FILE_PREFIX),
        };

        if matches {
            try_delete(&path);
        }
    }
}

fn cleanup_stale_work_directories(entry_directory: &Path) {
    let Ok(entries) = std::fs::read_dir(entry_directory) else {
        return;
    };

    for entry in entries.flatten() {
        let path = entry.path();
        if path.is_dir() {
            if let Some(name) = path.file_name().and_then(|n| n.to_str()) {
                if name.starts_with("work-") {
                    try_delete_directory(&path);
                }
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn is_supported_url_accepts_only_http_and_https() {
        let cache = YtDlpAudioCache::new();
        assert!(cache.is_supported_url("https://example.com"));
        assert!(cache.is_supported_url("http://example.com"));
        assert!(!cache.is_supported_url("D:\\Music\\track.mp3"));
        assert!(!cache.is_supported_url("ftp://example.com"));
    }

    #[test]
    fn is_youtube_playlist_page_url_requires_list_param_and_playlist_path() {
        let cache = YtDlpAudioCache::new();
        assert!(cache.is_youtube_playlist_page_url("https://www.youtube.com/playlist?list=PLxyz"));
        assert!(!cache.is_youtube_playlist_page_url("https://www.youtube.com/watch?v=abc&list=PLxyz"));
        assert!(!cache.is_youtube_playlist_page_url("https://example.com/playlist?list=PLxyz"));
    }

    #[test]
    fn parse_metadata_extracts_id_and_title() {
        let metadata = parse_metadata(r#"{"id":"abc123","title":"My Song"}"#);
        assert_eq!(metadata.id.as_deref(), Some("abc123"));
        assert_eq!(metadata.title.as_deref(), Some("My Song"));
    }

    #[test]
    fn parse_metadata_falls_back_to_default_on_invalid_json() {
        let metadata = parse_metadata("not json");
        assert!(metadata.id.is_none());
        assert!(metadata.title.is_none());
    }

    #[test]
    fn parse_playlist_metadata_prefers_webpage_url_then_original_url_then_id() {
        let json = r#"{
            "title": "My Playlist",
            "entries": [
                {"title": "Track A", "webpage_url": "https://youtu.be/aaa"},
                {"title": "Track B", "original_url": "https://youtu.be/bbb"},
                {"title": "Track C", "id": "ccc"}
            ]
        }"#;

        let playlist = parse_playlist_metadata("https://www.youtube.com/playlist?list=x", json);
        assert_eq!(playlist.title, "My Playlist");
        assert_eq!(playlist.entries.len(), 3);
        assert_eq!(playlist.entries[0].url, "https://youtu.be/aaa");
        assert_eq!(playlist.entries[1].url, "https://youtu.be/bbb");
        assert_eq!(playlist.entries[2].url, "https://www.youtube.com/watch?v=ccc");
    }

    #[test]
    fn is_likely_youtube_url_matches_both_domains() {
        assert!(is_likely_youtube_url("https://www.youtube.com/watch?v=1"));
        assert!(is_likely_youtube_url("https://youtu.be/1"));
        assert!(!is_likely_youtube_url("https://example.com"));
    }

    #[test]
    fn build_update_details_reports_success_and_failure() {
        let success = YtDlpProcessResult { exit_code: 0, stdout: String::new(), stderr: String::new() };
        assert_eq!(build_update_details(Some(&success)), " yt-dlp was updated and the URL was retried.");

        let failure = YtDlpProcessResult { exit_code: 1, stdout: String::new(), stderr: "boom".to_string() };
        assert_eq!(build_update_details(Some(&failure)), " yt-dlp update was attempted but failed:  boom");

        assert_eq!(build_update_details(None), "");
    }
}
