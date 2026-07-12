use chrono::Local;

use crate::PlaybackItem;

use super::{AlbumHistoryEntry, PlaybackHistorySnapshot, PlaybackHistoryStore, TrackHistoryEntry};

const MAX_HISTORY_ITEMS: usize = 20;

/// Owns the in-memory history lists (the Rust analogue of the ObservableCollections
/// that MainWindow.axaml.cs bound to directly) and keeps them synced to disk.
pub struct PlaybackHistoryService {
    store: PlaybackHistoryStore,
    albums: Vec<AlbumHistoryEntry>,
    tracks: Vec<TrackHistoryEntry>,
}

impl PlaybackHistoryService {
    pub fn load() -> Self {
        Self::with_store(PlaybackHistoryStore::new())
    }

    pub fn with_store(store: PlaybackHistoryStore) -> Self {
        let snapshot = store.load();

        let mut albums = snapshot.albums;
        albums.sort_by_key(|entry| std::cmp::Reverse(entry.last_played_at));
        albums.truncate(MAX_HISTORY_ITEMS);

        let mut tracks = snapshot.tracks;
        tracks.sort_by_key(|entry| std::cmp::Reverse(entry.last_played_at));
        tracks.truncate(MAX_HISTORY_ITEMS);

        Self {
            store,
            albums,
            tracks,
        }
    }

    pub fn albums(&self) -> &[AlbumHistoryEntry] {
        &self.albums
    }

    pub fn tracks(&self) -> &[TrackHistoryEntry] {
        &self.tracks
    }

    pub fn remove_album(&mut self, album_path: &str) -> bool {
        let before = self.albums.len();
        self.albums
            .retain(|entry| !entry.album_path.eq_ignore_ascii_case(album_path));
        let removed = self.albums.len() != before;
        if removed {
            self.save();
        }
        removed
    }

    pub fn remove_track(&mut self, source_path: &str) -> bool {
        let before = self.tracks.len();
        self.tracks
            .retain(|entry| !entry.source_path.eq_ignore_ascii_case(source_path));
        let removed = self.tracks.len() != before;
        if removed {
            self.save();
        }
        removed
    }

    pub fn record_album(&mut self, album_path: &str, track_count: i32, display_name_override: Option<&str>) {
        if album_path.trim().is_empty() || track_count <= 0 {
            return;
        }

        let display_name = display_name_override
            .map(str::trim)
            .filter(|s| !s.is_empty())
            .map(str::to_string)
            .or_else(|| {
                std::path::Path::new(album_path)
                    .file_name()
                    .and_then(|n| n.to_str())
                    .map(str::to_string)
            })
            .filter(|s| !s.is_empty())
            .unwrap_or_else(|| album_path.to_string());

        let entry = AlbumHistoryEntry {
            album_path: album_path.to_string(),
            display_name,
            track_count,
            last_played_at: Local::now(),
        };

        upsert(&mut self.albums, entry, |a, b| {
            a.album_path.eq_ignore_ascii_case(&b.album_path)
        });
        self.save();
    }

    pub fn record_track(&mut self, item: &PlaybackItem) {
        let context_text = if item.is_url_source && !item.is_album_source {
            item.path.clone()
        } else {
            item.context_text.clone()
        };

        let entry = TrackHistoryEntry {
            source_path: item.path.clone(),
            display_name: item.display_name.clone(),
            context_text,
            last_played_at: Local::now(),
        };

        upsert(&mut self.tracks, entry, |a, b| {
            a.source_path.eq_ignore_ascii_case(&b.source_path)
        });
        self.save();
    }

    fn save(&self) {
        self.store.save(&PlaybackHistorySnapshot {
            albums: self.albums.clone(),
            tracks: self.tracks.clone(),
        });
    }
}

fn upsert<T>(collection: &mut Vec<T>, item: T, is_match: impl Fn(&T, &T) -> bool) {
    if let Some(pos) = collection.iter().position(|existing| is_match(existing, &item)) {
        collection.remove(pos);
    }
    collection.insert(0, item);
    collection.truncate(MAX_HISTORY_ITEMS);
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::PlaybackItem;

    fn temp_store() -> PlaybackHistoryStore {
        let path = std::env::temp_dir().join(format!(
            "smp-core-history-{}-{:?}.json",
            std::process::id(),
            std::time::SystemTime::now()
        ));
        PlaybackHistoryStore::at_path(path)
    }

    #[test]
    fn record_album_moves_existing_entry_to_front() {
        let mut service = PlaybackHistoryService::with_store(temp_store());
        service.record_album("D:\\Music\\A", 5, None);
        service.record_album("D:\\Music\\B", 3, None);
        service.record_album("D:\\Music\\A", 5, None);

        assert_eq!(service.albums().len(), 2);
        assert_eq!(service.albums()[0].album_path, "D:\\Music\\A");
    }

    #[test]
    fn record_album_ignores_empty_albums() {
        let mut service = PlaybackHistoryService::with_store(temp_store());
        service.record_album("D:\\Music\\Empty", 0, None);
        assert!(service.albums().is_empty());
    }

    #[test]
    fn remove_album_deletes_matching_path_case_insensitively() {
        let mut service = PlaybackHistoryService::with_store(temp_store());
        service.record_album("D:\\Music\\A", 5, None);

        assert!(service.remove_album("d:\\music\\a"));
        assert!(service.albums().is_empty());
    }

    #[test]
    fn record_track_uses_raw_path_as_context_for_bare_urls() {
        let mut service = PlaybackHistoryService::with_store(temp_store());
        let item = PlaybackItem::from_url("https://example.com/watch?v=1", None);
        service.record_track(&item);

        assert_eq!(service.tracks()[0].context_text, "https://example.com/watch?v=1");
    }

    #[test]
    fn history_is_capped_at_twenty_entries() {
        let mut service = PlaybackHistoryService::with_store(temp_store());
        for i in 0..25 {
            service.record_album(&format!("D:\\Music\\Album{i}"), 1, None);
        }

        assert_eq!(service.albums().len(), MAX_HISTORY_ITEMS);
        assert_eq!(service.albums()[0].album_path, "D:\\Music\\Album24");
    }
}
