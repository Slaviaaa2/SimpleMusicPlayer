const SUPPORTED_EXTENSIONS: &[&str] = &[
    "mp3", "wav", "aac", "m4a", "flac", "wma", "ogg", "opus", "mp4", "m4v", "mov", "wmv", "avi",
    "mkv", "webm",
];

const TRANSCODE_EXTENSIONS: &[&str] = &["mkv", "webm", "opus"];

pub fn supported_extensions() -> impl Iterator<Item = &'static str> {
    SUPPORTED_EXTENSIONS.iter().copied()
}

pub fn supported_patterns() -> impl Iterator<Item = String> {
    SUPPORTED_EXTENSIONS.iter().map(|ext| format!("*.{ext}"))
}

fn extension_of(path: &str) -> Option<String> {
    std::path::Path::new(path)
        .extension()
        .and_then(|ext| ext.to_str())
        .map(|ext| ext.to_ascii_lowercase())
}

pub fn is_supported(path: &str) -> bool {
    match extension_of(path) {
        Some(ext) => SUPPORTED_EXTENSIONS.contains(&ext.as_str()),
        None => false,
    }
}

pub fn requires_transcode(path: &str) -> bool {
    match extension_of(path) {
        Some(ext) => TRANSCODE_EXTENSIONS.contains(&ext.as_str()),
        None => false,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn recognizes_supported_extensions_case_insensitively() {
        assert!(is_supported("track.MP3"));
        assert!(is_supported("video.mkv"));
        assert!(!is_supported("document.pdf"));
        assert!(!is_supported("no-extension"));
    }

    #[test]
    fn flags_only_containers_that_need_transcoding() {
        assert!(requires_transcode("clip.mkv"));
        assert!(requires_transcode("clip.webm"));
        assert!(requires_transcode("clip.opus"));
        assert!(!requires_transcode("clip.mp3"));
        assert!(!requires_transcode("clip.mp4"));
    }
}
