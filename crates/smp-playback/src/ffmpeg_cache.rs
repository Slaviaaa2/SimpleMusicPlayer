use std::collections::HashMap;
use std::fmt;
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex};

use sha2::{Digest, Sha256};
use tokio_util::sync::CancellationToken;

use smp_core::{build_failure_details, media_file_types};

use crate::external_process::{ExternalProcessRunner, RunError};
use crate::paths::{app_base_directory, is_usable_file, try_delete};
use crate::tool_path_resolver;

#[derive(Debug)]
pub enum FfmpegCacheError {
    Cancelled,
    Io(std::io::Error),
    TranscodeFailed(String),
}

impl fmt::Display for FfmpegCacheError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            FfmpegCacheError::Cancelled => write!(f, "the transcode was cancelled"),
            FfmpegCacheError::Io(err) => write!(f, "{err}"),
            FfmpegCacheError::TranscodeFailed(message) => write!(f, "{message}"),
        }
    }
}

impl std::error::Error for FfmpegCacheError {}

pub struct FfmpegAudioCache {
    ffmpeg_path: Option<PathBuf>,
    cache_directory: PathBuf,
    process_runner: ExternalProcessRunner,
    transcoded_paths: Mutex<HashMap<String, PathBuf>>,
    transcode_locks: Mutex<HashMap<String, Arc<tokio::sync::Mutex<()>>>>,
}

impl FfmpegAudioCache {
    pub fn new() -> Self {
        Self {
            ffmpeg_path: tool_path_resolver::resolve_executable_path("ffmpeg"),
            cache_directory: app_base_directory().join("cache").join("transcoded"),
            process_runner: ExternalProcessRunner::new(),
            transcoded_paths: Mutex::new(HashMap::new()),
            transcode_locks: Mutex::new(HashMap::new()),
        }
    }

    pub fn is_available(&self) -> bool {
        self.ffmpeg_path.is_some()
    }

    pub fn requires_transcode(&self, path: &str) -> bool {
        media_file_types::requires_transcode(path)
    }

    pub async fn get_playback_path(
        &self,
        source_path: &str,
        cancellation: &CancellationToken,
    ) -> Result<String, FfmpegCacheError> {
        if !self.requires_transcode(source_path) || !self.is_available() {
            return Ok(source_path.to_string());
        }

        if let Some(existing) = self.cached_path_if_usable(source_path, |p| p.is_file()) {
            return Ok(existing);
        }

        let cache_key = build_source_cache_key(source_path).map_err(FfmpegCacheError::Io)?;
        let lock = {
            let mut locks = self.transcode_locks.lock().unwrap();
            locks
                .entry(cache_key.clone())
                .or_insert_with(|| Arc::new(tokio::sync::Mutex::new(())))
                .clone()
        };
        let _guard = lock.lock().await;

        if let Some(existing) = self.cached_path_if_usable(source_path, is_usable_file) {
            return Ok(existing);
        }

        std::fs::create_dir_all(&self.cache_directory).map_err(FfmpegCacheError::Io)?;

        let output_path = self.cache_directory.join(format!("{cache_key}.wav"));
        if is_usable_file(&output_path) {
            self.remember(source_path, &output_path);
            return Ok(output_path.to_string_lossy().to_string());
        }

        let temp_output_path = self
            .cache_directory
            .join(format!("{cache_key}.{}.tmp.wav", uuid::Uuid::new_v4().simple()));

        let ffmpeg_path = self.ffmpeg_path.as_ref().expect("checked by is_available");
        let args = build_transcode_arguments(source_path, &temp_output_path);

        let run_result = self
            .process_runner
            .run(&ffmpeg_path.to_string_lossy(), args, None, cancellation)
            .await;

        match run_result {
            Ok(process_result) if process_result.exit_code == 0 && is_usable_file(&temp_output_path) => {
                move_replacing(&temp_output_path, &output_path).map_err(FfmpegCacheError::Io)?;
                self.remember(source_path, &output_path);
                Ok(output_path.to_string_lossy().to_string())
            }
            Ok(process_result) => {
                try_delete(&temp_output_path);
                let details = build_failure_details(&process_result.stderr, &process_result.stdout);
                Err(FfmpegCacheError::TranscodeFailed(format!(
                    "ffmpeg could not decode this file.{details}"
                )))
            }
            Err(RunError::Cancelled) => {
                try_delete(&temp_output_path);
                Err(FfmpegCacheError::Cancelled)
            }
            Err(RunError::Io(err)) => {
                try_delete(&temp_output_path);
                Err(FfmpegCacheError::Io(err))
            }
        }
    }

    fn cached_path_if_usable(&self, source_path: &str, is_usable: impl Fn(&Path) -> bool) -> Option<String> {
        let paths = self.transcoded_paths.lock().unwrap();
        let existing = paths.get(source_path)?;
        is_usable(existing).then(|| existing.to_string_lossy().to_string())
    }

    fn remember(&self, source_path: &str, output_path: &Path) {
        self.transcoded_paths
            .lock()
            .unwrap()
            .insert(source_path.to_string(), output_path.to_path_buf());
    }
}

impl Default for FfmpegAudioCache {
    fn default() -> Self {
        Self::new()
    }
}

fn build_transcode_arguments(source_path: &str, output_path: &Path) -> Vec<String> {
    vec![
        "-y".to_string(),
        "-i".to_string(),
        source_path.to_string(),
        "-vn".to_string(),
        "-acodec".to_string(),
        "pcm_s16le".to_string(),
        "-ar".to_string(),
        "48000".to_string(),
        "-ac".to_string(),
        "2".to_string(),
        output_path.to_string_lossy().to_string(),
    ]
}

fn build_source_cache_key(source_path: &str) -> std::io::Result<String> {
    let metadata = std::fs::metadata(source_path)?;
    let modified_nanos = metadata
        .modified()
        .ok()
        .and_then(|time| time.duration_since(std::time::UNIX_EPOCH).ok())
        .map(|duration| duration.as_nanos())
        .unwrap_or(0);
    let fingerprint = format!("{source_path}|{}|{modified_nanos}", metadata.len());
    Ok(hex::encode(Sha256::digest(fingerprint.as_bytes())))
}

fn move_replacing(source: &Path, destination: &Path) -> std::io::Result<()> {
    try_delete(destination);
    std::fs::rename(source, destination)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn requires_transcode_matches_media_file_types() {
        let cache = FfmpegAudioCache::new();
        assert!(cache.requires_transcode("clip.webm"));
        assert!(!cache.requires_transcode("clip.mp3"));
    }

    #[tokio::test]
    async fn passthrough_paths_that_do_not_need_transcoding() {
        let cache = FfmpegAudioCache::new();
        let cancellation = CancellationToken::new();
        let result = cache
            .get_playback_path("D:\\Music\\track.mp3", &cancellation)
            .await
            .unwrap();
        assert_eq!(result, "D:\\Music\\track.mp3");
    }
}
