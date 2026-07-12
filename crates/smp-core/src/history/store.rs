use std::path::PathBuf;

use super::{JsonFileStore, PlaybackHistorySnapshot};

pub struct PlaybackHistoryStore {
    store: JsonFileStore<PlaybackHistorySnapshot>,
}

impl PlaybackHistoryStore {
    pub fn new() -> Self {
        Self::at_path(history_file_path())
    }

    pub fn at_path(file_path: impl Into<PathBuf>) -> Self {
        Self {
            store: JsonFileStore::new(file_path),
        }
    }

    pub fn load(&self) -> PlaybackHistorySnapshot {
        self.store.load()
    }

    pub fn save(&self, snapshot: &PlaybackHistorySnapshot) {
        self.store.save(snapshot);
    }
}

impl Default for PlaybackHistoryStore {
    fn default() -> Self {
        Self::new()
    }
}

fn history_file_path() -> PathBuf {
    dirs::data_local_dir()
        .unwrap_or_else(std::env::temp_dir)
        .join("SimpleMusicPlayer")
        .join("history.json")
}
