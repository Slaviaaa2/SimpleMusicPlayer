use chrono::{DateTime, Local};
use serde::{Deserialize, Serialize};

/// Field names are PascalCase to stay compatible with history.json files
/// written by the previous C# release (System.Text.Json defaults to the
/// declared property names, which were PascalCase).
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase")]
pub struct AlbumHistoryEntry {
    pub album_path: String,
    pub display_name: String,
    pub track_count: i32,
    pub last_played_at: DateTime<Local>,
}

impl AlbumHistoryEntry {
    pub fn track_count_text(&self) -> String {
        format!("{} tracks", self.track_count)
    }

    pub fn last_played_text(&self) -> String {
        self.last_played_at.format("%Y/%m/%d %H:%M").to_string()
    }
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase")]
pub struct TrackHistoryEntry {
    pub source_path: String,
    pub display_name: String,
    pub context_text: String,
    pub last_played_at: DateTime<Local>,
}

impl TrackHistoryEntry {
    pub fn last_played_text(&self) -> String {
        self.last_played_at.format("%Y/%m/%d %H:%M").to_string()
    }
}

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase")]
pub struct PlaybackHistorySnapshot {
    #[serde(default)]
    pub albums: Vec<AlbumHistoryEntry>,
    #[serde(default)]
    pub tracks: Vec<TrackHistoryEntry>,
}
