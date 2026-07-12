use std::time::{Duration, SystemTime, UNIX_EPOCH};

use discord_rich_presence::{activity, DiscordIpc, DiscordIpcClient};
use smp_core::PlaybackItem;

const APPLICATION_ID: &str = "1504140042820522125";

/// Mirrors DiscordPresenceService.cs: best-effort only. If Discord isn't
/// running (or the IPC connection otherwise fails), presence updates are
/// silently no-ops rather than errors.
pub struct DiscordPresenceService {
    client: Option<DiscordIpcClient>,
}

impl DiscordPresenceService {
    pub fn new() -> Self {
        let mut client = DiscordIpcClient::new(APPLICATION_ID);
        let client = client.connect().ok().map(|_| client);
        Self { client }
    }

    pub fn is_enabled(&self) -> bool {
        self.client.is_some()
    }

    pub fn set_now_playing(
        &mut self,
        item: &PlaybackItem,
        current_index: usize,
        total_count: usize,
        is_playing: bool,
        position: Duration,
        duration: Duration,
    ) {
        let Some(client) = self.client.as_mut() else {
            return;
        };

        let queue_label = if item.is_album_source { "Album" } else { "Queue" };
        let state = if is_playing {
            format!("{queue_label} {}/{}", current_index + 1, total_count)
        } else {
            format!("Paused \u{b7} {queue_label} {}/{}", current_index + 1, total_count)
        };

        let mut payload = activity::Activity::new()
            .activity_type(activity::ActivityType::Listening)
            .details(&item.display_name)
            .state(&state);

        if is_playing && duration > Duration::ZERO {
            if let Some(timestamps) = playback_timestamps(position, duration) {
                payload = payload.timestamps(timestamps);
            }
        }

        let _ = client.set_activity(payload);
    }

    pub fn clear(&mut self) {
        if let Some(client) = self.client.as_mut() {
            let _ = client.clear_activity();
        }
    }
}

impl Default for DiscordPresenceService {
    fn default() -> Self {
        Self::new()
    }
}

impl Drop for DiscordPresenceService {
    fn drop(&mut self) {
        if let Some(client) = self.client.as_mut() {
            let _ = client.clear_activity();
            let _ = client.close();
        }
    }
}

fn playback_timestamps(position: Duration, duration: Duration) -> Option<activity::Timestamps> {
    let bounded_position = position.min(duration);
    let now = SystemTime::now().duration_since(UNIX_EPOCH).ok()?;
    let start = now.checked_sub(bounded_position)?;
    let end = now.checked_add(duration - bounded_position)?;

    Some(
        activity::Timestamps::new()
            .start(start.as_secs() as i64)
            .end(end.as_secs() as i64),
    )
}
