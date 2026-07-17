use std::path::Path;

use url::Url;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PlaybackSourceKind {
    File,
    AlbumTrack,
    Url,
    PlaylistTrack,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PlaybackItem {
    pub path: String,
    pub kind: PlaybackSourceKind,
    pub is_album_source: bool,
    pub is_url_source: bool,
    pub source_label: &'static str,
    pub context_text: String,
    pub display_name: String,
}

impl PlaybackItem {
    fn new(
        path: impl Into<String>,
        kind: PlaybackSourceKind,
        display_name: Option<String>,
        context_text: Option<String>,
    ) -> Self {
        let path = path.into();
        let is_album_source = matches!(
            kind,
            PlaybackSourceKind::AlbumTrack | PlaybackSourceKind::PlaylistTrack
        );
        let is_url_source = matches!(
            kind,
            PlaybackSourceKind::Url | PlaybackSourceKind::PlaylistTrack
        );
        let source_label = match kind {
            PlaybackSourceKind::AlbumTrack => "ALBUM",
            PlaybackSourceKind::PlaylistTrack => "PLAYLIST",
            PlaybackSourceKind::Url => "URL",
            PlaybackSourceKind::File => "FILE",
        };
        let context_text = non_blank(context_text).unwrap_or_else(|| build_context_text(&path, kind));
        let display_name =
            non_blank(display_name).unwrap_or_else(|| build_fallback_name(&path, is_url_source));

        Self {
            path,
            kind,
            is_album_source,
            is_url_source,
            source_label,
            context_text,
            display_name,
        }
    }

    pub fn from_file(path: impl Into<String>) -> Self {
        Self::new(path, PlaybackSourceKind::File, None, None)
    }

    pub fn from_album_track(path: impl Into<String>) -> Self {
        Self::new(path, PlaybackSourceKind::AlbumTrack, None, None)
    }

    pub fn from_url(url: impl Into<String>, display_name: Option<String>) -> Self {
        let url = url.into();
        let display_name = display_name.or_else(|| Some(url.clone()));
        Self::new(url, PlaybackSourceKind::Url, display_name, None)
    }

    pub fn from_playlist_track(
        url: impl Into<String>,
        playlist_title: &str,
        title: Option<String>,
    ) -> Self {
        let url = url.into();
        let display_name = title.or_else(|| Some(url.clone()));
        Self::new(
            url,
            PlaybackSourceKind::PlaylistTrack,
            display_name,
            Some(format!("Playlist \u{b7} {playlist_title}")),
        )
    }

    pub fn update_display_name(&mut self, display_name: Option<&str>) {
        if let Some(trimmed) = display_name.map(str::trim) {
            if !trimmed.is_empty() {
                self.display_name = trimmed.to_string();
            }
        }
    }
}

fn non_blank(value: Option<String>) -> Option<String> {
    value.map(|v| v.trim().to_string()).filter(|v| !v.is_empty())
}

fn build_fallback_name(source: &str, is_url_source: bool) -> String {
    if is_url_source {
        if let Ok(url) = Url::parse(source) {
            if let Some(host) = url.host_str() {
                return host.to_string();
            }
        }
    }

    Path::new(source)
        .file_stem()
        .and_then(|s| s.to_str())
        .unwrap_or(source)
        .to_string()
}

fn build_context_text(source: &str, kind: PlaybackSourceKind) -> String {
    if matches!(kind, PlaybackSourceKind::Url | PlaybackSourceKind::PlaylistTrack) {
        if let Ok(url) = Url::parse(source) {
            return url.to_string();
        }
    }

    if kind == PlaybackSourceKind::AlbumTrack {
        if let Some(album_dir_name) = Path::new(source)
            .parent()
            .and_then(|dir| dir.file_name())
            .and_then(|name| name.to_str())
        {
            return format!("Album \u{b7} {album_dir_name}");
        }
    }

    match Path::new(source).parent() {
        Some(dir) if !dir.as_os_str().is_empty() => dir.to_string_lossy().to_string(),
        _ => source.to_string(),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    // Asserts Windows path semantics (drive-letter roots, `\` separators);
    // on Unix the same literal is a single path component.
    #[test]
    #[cfg(windows)]
    fn from_file_defaults_display_name_to_stem_and_context_to_directory() {
        let item = PlaybackItem::from_file("D:\\Music\\Some Album\\01 Track.mp3");
        assert_eq!(item.display_name, "01 Track");
        assert_eq!(item.context_text, "D:\\Music\\Some Album");
        assert_eq!(item.source_label, "FILE");
        assert!(!item.is_album_source);
        assert!(!item.is_url_source);
    }

    #[test]
    #[cfg(windows)]
    fn from_album_track_context_mentions_album_folder() {
        let item = PlaybackItem::from_album_track("D:\\Music\\Some Album\\01 Track.mp3");
        assert_eq!(item.context_text, "Album \u{b7} Some Album");
        assert_eq!(item.source_label, "ALBUM");
        assert!(item.is_album_source);
        assert!(!item.is_url_source);
    }

    #[test]
    fn from_url_defaults_display_name_to_the_raw_url() {
        // Matches PlaybackItem.cs's `FromUrl`, which passes `displayName ?? url`
        // into the constructor -- so the host-only fallback in BuildFallbackName
        // is never actually reached through this factory.
        let item = PlaybackItem::from_url("https://example.com/watch?v=abc", None);
        assert_eq!(item.display_name, "https://example.com/watch?v=abc");
        assert_eq!(item.context_text, "https://example.com/watch?v=abc");
        assert_eq!(item.source_label, "URL");
        assert!(item.is_url_source);
        assert!(!item.is_album_source);
    }

    #[test]
    fn from_playlist_track_uses_playlist_title_in_context() {
        let item = PlaybackItem::from_playlist_track(
            "https://example.com/watch?v=abc",
            "My Playlist",
            Some("Track One".to_string()),
        );
        assert_eq!(item.display_name, "Track One");
        assert_eq!(item.context_text, "Playlist \u{b7} My Playlist");
        assert_eq!(item.source_label, "PLAYLIST");
        assert!(item.is_url_source);
        assert!(item.is_album_source);
    }

    #[test]
    fn update_display_name_ignores_blank_input() {
        let mut item = PlaybackItem::from_file("track.mp3");
        item.update_display_name(Some("  "));
        assert_eq!(item.display_name, "track");
        item.update_display_name(Some(" Real Title "));
        assert_eq!(item.display_name, "Real Title");
    }
}
