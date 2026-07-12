use std::rc::Rc;

use slint::VecModel;
use smp_core::history::{AlbumHistoryEntry, TrackHistoryEntry};
use smp_core::PlaybackItem;

use crate::{AlbumHistoryData, QueueItemData, TrackHistoryData};

pub fn queue_item_data(item: &PlaybackItem) -> QueueItemData {
    QueueItemData {
        source_label: item.source_label.into(),
        display_name: item.display_name.clone().into(),
        context_text: item.context_text.clone().into(),
    }
}

pub fn album_history_data(entry: &AlbumHistoryEntry) -> AlbumHistoryData {
    AlbumHistoryData {
        display_name: entry.display_name.clone().into(),
        album_path: entry.album_path.clone().into(),
        track_count_text: entry.track_count_text().into(),
        last_played_text: entry.last_played_text().into(),
    }
}

pub fn track_history_data(entry: &TrackHistoryEntry) -> TrackHistoryData {
    TrackHistoryData {
        display_name: entry.display_name.clone().into(),
        context_text: entry.context_text.clone().into(),
        last_played_text: entry.last_played_text().into(),
    }
}

pub fn queue_model(items: &[PlaybackItem]) -> Rc<VecModel<QueueItemData>> {
    Rc::new(VecModel::from(items.iter().map(queue_item_data).collect::<Vec<_>>()))
}

pub fn album_history_model(entries: &[AlbumHistoryEntry]) -> Rc<VecModel<AlbumHistoryData>> {
    Rc::new(VecModel::from(
        entries.iter().map(album_history_data).collect::<Vec<_>>(),
    ))
}

pub fn track_history_model(entries: &[TrackHistoryEntry]) -> Rc<VecModel<TrackHistoryData>> {
    Rc::new(VecModel::from(
        entries.iter().map(track_history_data).collect::<Vec<_>>(),
    ))
}

pub fn build_queue_text(item: &PlaybackItem, current_index: usize, queue_count: usize) -> String {
    format!(
        "{} {}/{}  {}",
        item.source_label,
        current_index + 1,
        queue_count,
        item.context_text
    )
}

pub fn format_time(total_seconds: f64) -> String {
    let seconds = total_seconds.max(0.0) as u64;
    let hours = seconds / 3600;
    let minutes = (seconds % 3600) / 60;
    let secs = seconds % 60;

    if hours > 0 {
        format!("{hours:02}:{minutes:02}:{secs:02}")
    } else {
        format!("{minutes:02}:{secs:02}")
    }
}
