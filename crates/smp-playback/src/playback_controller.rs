use std::time::Duration;

use tokio::sync::mpsc::{unbounded_channel, UnboundedReceiver, UnboundedSender};
use url::Url;
use vlc::{EventType, Instance, Media, MediaPlayer, MediaPlayerAudioEx};

/// Forwarded from libvlc's internal event thread; the receiver is expected to
/// marshal these onto the UI thread (mirrors `Dispatcher.UIThread.Post` around
/// PlaybackController.cs's `PlaybackStarted`/`PlaybackEnded`/`PlaybackFailed`).
pub enum PlaybackEvent {
    Started,
    Ended,
    Failed(String),
}

pub struct PlaybackController {
    instance: Instance,
    media_player: MediaPlayer,
    current_media: Option<Media>,
}

impl PlaybackController {
    pub fn new() -> Result<(Self, UnboundedReceiver<PlaybackEvent>), String> {
        let instance = Instance::with_args(Some(vec!["--no-video".to_string(), "--quiet".to_string()]))
            .ok_or_else(|| "Could not initialize LibVLC.".to_string())?;
        let media_player =
            MediaPlayer::new(&instance).ok_or_else(|| "Could not create a LibVLC media player.".to_string())?;

        let (sender, receiver) = unbounded_channel();
        attach_events(&media_player, sender);

        Ok((
            Self {
                instance,
                media_player,
                current_media: None,
            },
            receiver,
        ))
    }

    pub fn duration(&self) -> Duration {
        self.media_player
            .get_media()
            .and_then(|media| media.duration())
            .filter(|ms| *ms > 0)
            .map(|ms| Duration::from_millis(ms as u64))
            .unwrap_or_default()
    }

    pub fn position(&self) -> Duration {
        self.media_player
            .get_time()
            .filter(|ms| *ms > 0)
            .map(|ms| Duration::from_millis(ms as u64))
            .unwrap_or_default()
    }

    pub fn set_position(&mut self, position: Duration) {
        let millis = i64::try_from(position.as_millis()).unwrap_or(i64::MAX).max(0);
        self.media_player.set_time(millis);
    }

    pub fn play(&mut self, source_path: &str) -> bool {
        let Some(media) = create_media(&self.instance, source_path) else {
            return false;
        };

        self.media_player.set_media(&media);
        self.current_media = Some(media);

        let started = self.media_player.play().is_ok();
        if !started {
            self.current_media = None;
        }
        started
    }

    /// 0-200; libvlc software-amplifies anything above 100. Fails silently
    /// before the audio output exists, so callers re-apply it on Started.
    pub fn set_volume(&mut self, volume_percent: i32) {
        let _ = self.media_player.set_volume(volume_percent.max(0));
    }

    pub fn set_mute(&mut self, muted: bool) {
        self.media_player.set_mute(muted);
    }

    pub fn pause(&mut self) {
        self.media_player.set_pause(true);
    }

    pub fn resume(&mut self) {
        self.media_player.set_pause(false);
    }

    pub fn stop(&mut self, clear_media: bool) {
        self.media_player.stop();
        if clear_media {
            self.current_media = None;
        }
    }
}

fn attach_events(media_player: &MediaPlayer, sender: UnboundedSender<PlaybackEvent>) {
    let playing_sender = sender.clone();
    let _ = media_player
        .event_manager()
        .attach(EventType::MediaPlayerPlaying, move |_, _| {
            let _ = playing_sender.send(PlaybackEvent::Started);
        });

    let ended_sender = sender.clone();
    let _ = media_player
        .event_manager()
        .attach(EventType::MediaPlayerEndReached, move |_, _| {
            let _ = ended_sender.send(PlaybackEvent::Ended);
        });

    let _ = media_player
        .event_manager()
        .attach(EventType::MediaPlayerEncounteredError, move |_, _| {
            let _ = sender.send(PlaybackEvent::Failed("LibVLC could not decode this media.".to_string()));
        });
}

fn create_media(instance: &Instance, source_path: &str) -> Option<Media> {
    if let Ok(url) = Url::parse(source_path) {
        if url.scheme() == "http" || url.scheme() == "https" {
            return Media::new_location(instance, source_path);
        }
    }

    Media::new_path(instance, source_path)
}
