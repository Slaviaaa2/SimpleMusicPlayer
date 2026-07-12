use std::fs;
use std::path::Path;

use rand::seq::SliceRandom;

use crate::{media_file_types, PlaybackItem};

pub struct AlbumSourceRecord {
    pub path: String,
    pub track_count: usize,
}

pub struct PlaybackSourceCollectionResult {
    pub items: Vec<PlaybackItem>,
    pub albums: Vec<AlbumSourceRecord>,
}

/// Mirrors PlaybackSourceCollector.cs: `is_supported_url` is injected so this crate
/// stays free of network/process concerns (the real check lives in smp-playback).
pub fn collect<I, S, F>(raw_sources: I, is_supported_url: F, shuffle: bool) -> PlaybackSourceCollectionResult
where
    I: IntoIterator<Item = S>,
    S: AsRef<str>,
    F: Fn(&str) -> bool,
{
    let mut items = Vec::new();
    let mut albums = Vec::new();

    for source in raw_sources
        .into_iter()
        .map(|s| s.as_ref().to_string())
        .filter(|s| !s.trim().is_empty())
    {
        if Path::new(&source).is_dir() {
            add_album_source(&source, &mut items, &mut albums);
            continue;
        }

        if is_supported_url(&source) {
            items.push(PlaybackItem::from_url(source, None));
            continue;
        }

        if Path::new(&source).is_file() && media_file_types::is_supported(&source) {
            items.push(PlaybackItem::from_file(source));
        }
    }

    if shuffle && items.len() > 1 {
        items.shuffle(&mut rand::rng());
    }

    PlaybackSourceCollectionResult { items, albums }
}

fn add_album_source(
    source: &str,
    items: &mut Vec<PlaybackItem>,
    albums: &mut Vec<AlbumSourceRecord>,
) {
    let mut album_files: Vec<String> = fs::read_dir(source)
        .into_iter()
        .flatten()
        .filter_map(|entry| entry.ok())
        .filter(|entry| entry.path().is_file())
        .map(|entry| entry.path().to_string_lossy().to_string())
        .filter(|path| media_file_types::is_supported(path))
        .collect();
    album_files.sort_by_key(|path| path.to_lowercase());

    for file in &album_files {
        items.push(PlaybackItem::from_album_track(file.clone()));
    }

    if !album_files.is_empty() {
        albums.push(AlbumSourceRecord {
            path: source.to_string(),
            track_count: album_files.len(),
        });
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::fs::File;

    #[test]
    fn collects_local_file_and_url_sources() {
        let dir = std::env::temp_dir().join(format!("smp-core-test-{}", std::process::id()));
        fs::create_dir_all(&dir).unwrap();
        let track_path = dir.join("track.mp3");
        File::create(&track_path).unwrap();

        let sources = vec![
            track_path.to_string_lossy().to_string(),
            "https://example.com/watch?v=1".to_string(),
        ];

        let result = collect(sources, |s| s.starts_with("https://"), false);

        assert_eq!(result.items.len(), 2);
        assert!(!result.items[0].is_url_source);
        assert!(result.items[1].is_url_source);
        assert!(result.albums.is_empty());

        fs::remove_dir_all(&dir).ok();
    }

    #[test]
    fn collects_album_folder_and_records_it() {
        let dir = std::env::temp_dir().join(format!("smp-core-test-album-{}", std::process::id()));
        fs::create_dir_all(&dir).unwrap();
        File::create(dir.join("b.mp3")).unwrap();
        File::create(dir.join("a.mp3")).unwrap();
        File::create(dir.join("notes.txt")).unwrap();

        let result = collect(
            vec![dir.to_string_lossy().to_string()],
            |_| false,
            false,
        );

        assert_eq!(result.items.len(), 2);
        assert!(result.items[0].display_name < result.items[1].display_name);
        assert_eq!(result.albums.len(), 1);
        assert_eq!(result.albums[0].track_count, 2);

        fs::remove_dir_all(&dir).ok();
    }
}
