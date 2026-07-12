use std::fmt;
use std::sync::Arc;

use smp_core::PlaybackSourceKind;
use smp_playback::{FfmpegAudioCache, FfmpegCacheError, YtDlpAudioCache, YtDlpError};
use tokio_util::sync::CancellationToken;

use crate::AppWindow;

#[derive(Debug)]
pub enum ResolveError {
    Cancelled,
    Message(String),
}

impl fmt::Display for ResolveError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            ResolveError::Cancelled => write!(f, "cancelled"),
            ResolveError::Message(message) => write!(f, "{message}"),
        }
    }
}

pub struct ResolvedPlayback {
    pub playback_path: String,
    pub updated_display_name: Option<String>,
    pub queue_info_override: Option<String>,
}

pub struct TrackResolveRequest {
    pub item_path: String,
    pub item_kind: PlaybackSourceKind,
    pub is_url_source: bool,
    pub current_index: usize,
    pub queue_len: usize,
}

/// Mirrors MainWindow.axaml.cs's `ResolvePlaybackPathAsync`. Runs entirely on
/// the background tokio runtime -- everything it touches must be `Send`, so
/// intermediate status text is pushed straight to the window (via `set_status`)
/// rather than through the (thread-confined) AppState.
pub async fn resolve_playback_path(
    request: TrackResolveRequest,
    ytdlp: Arc<YtDlpAudioCache>,
    ffmpeg: Arc<FfmpegAudioCache>,
    cancellation: CancellationToken,
    window: slint::Weak<AppWindow>,
) -> Result<ResolvedPlayback, ResolveError> {
    let TrackResolveRequest {
        item_path,
        item_kind,
        is_url_source,
        current_index,
        queue_len,
    } = request;

    if is_url_source && ytdlp.is_supported_url(&item_path) {
        set_status(&window, "Fetching audio from URL...");

        let cached = ytdlp
            .get_or_download(&item_path, &cancellation)
            .await
            .map_err(map_ytdlp_error)?;

        if ffmpeg.requires_transcode(&cached.file_path) && !ffmpeg.is_available() {
            return Err(ResolveError::Message(
                "ffmpeg was not found in PATH or the bundled tools directory, so this downloaded audio could not be decoded.".to_string(),
            ));
        }

        let queue_info = if cached.was_cached {
            format!(
                "URL {}/{}  cache ready  {}",
                current_index + 1,
                queue_len,
                cached.source_url
            )
        } else {
            format!(
                "URL {}/{}  downloaded  {}",
                current_index + 1,
                queue_len,
                cached.source_url
            )
        };

        let interim_status = if ffmpeg.requires_transcode(&cached.file_path) {
            if cached.was_cached {
                "Preparing cached audio for playback..."
            } else {
                "Converting downloaded audio for playback..."
            }
        } else if cached.was_cached {
            "Loaded from cache."
        } else {
            "Download complete."
        };
        set_status(&window, interim_status);

        let playback_path = ffmpeg
            .get_playback_path(&cached.file_path, &cancellation)
            .await
            .map_err(map_ffmpeg_error)?;

        return Ok(ResolvedPlayback {
            playback_path,
            updated_display_name: Some(cached.title),
            queue_info_override: Some(queue_info),
        });
    }

    if ffmpeg.requires_transcode(&item_path) {
        if !ffmpeg.is_available() {
            return Err(ResolveError::Message(
                "ffmpeg was not found in PATH or the bundled tools directory, so this file could not be decoded.".to_string(),
            ));
        }
        set_status(&window, "Converting this source for reliable playback...");
    } else {
        set_status(&window, cue_status(item_kind));
    }

    let playback_path = ffmpeg
        .get_playback_path(&item_path, &cancellation)
        .await
        .map_err(map_ffmpeg_error)?;

    Ok(ResolvedPlayback {
        playback_path,
        updated_display_name: None,
        queue_info_override: None,
    })
}

pub fn preparation_status(is_url_source: bool, is_album_source: bool) -> &'static str {
    if is_url_source {
        "Preparing URL source..."
    } else if is_album_source {
        "Preparing album track..."
    } else {
        "Preparing track..."
    }
}

fn cue_status(kind: PlaybackSourceKind) -> &'static str {
    match kind {
        PlaybackSourceKind::AlbumTrack => "Cueing album track...",
        PlaybackSourceKind::PlaylistTrack => "Cueing playlist track...",
        _ => "Cueing track...",
    }
}

fn map_ytdlp_error(error: YtDlpError) -> ResolveError {
    match error {
        YtDlpError::Cancelled => ResolveError::Cancelled,
        YtDlpError::Message(message) => ResolveError::Message(message),
    }
}

fn map_ffmpeg_error(error: FfmpegCacheError) -> ResolveError {
    match error {
        FfmpegCacheError::Cancelled => ResolveError::Cancelled,
        other => ResolveError::Message(other.to_string()),
    }
}

pub fn set_status(window: &slint::Weak<AppWindow>, message: &str) {
    let window = window.clone();
    let message = message.to_string();
    let _ = slint::invoke_from_event_loop(move || {
        if let Some(window) = window.upgrade() {
            window.set_status(message.into());
        }
    });
}
