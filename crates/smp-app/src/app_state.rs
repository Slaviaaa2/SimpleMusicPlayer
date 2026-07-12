use std::cell::RefCell;
use std::path::Path;
use std::sync::Arc;
use std::time::Duration;

use slint::{ComponentHandle, Timer, TimerMode};
use smp_core::history::PlaybackHistoryService;
use smp_core::{get_next_index, get_previous_index, media_file_types, source_collector, LoopMode, PlaybackItem};
use smp_discord::DiscordPresenceService;
use smp_playback::{FfmpegAudioCache, PlaybackController, PlaybackEvent, YtDlpAudioCache, YtDlpPlaylistResult};
use tokio_util::sync::CancellationToken;

use crate::resolve::{self, ResolveError, ResolvedPlayback};
use crate::ui_bridge;
use crate::AppWindow;

thread_local! {
    static STATE: RefCell<Option<AppState>> = const { RefCell::new(None) };
}

pub fn init(window: slint::Weak<AppWindow>, tokio_handle: tokio::runtime::Handle) {
    let state = AppState::new(window, tokio_handle);
    STATE.with(|s| *s.borrow_mut() = Some(state));
}

/// All Slint callbacks and the background-task continuations funnel through
/// here: since `AppState` is thread-confined (owns `!Send` LibVLC handles),
/// it lives in a thread-local instead of an `Rc<RefCell<_>>` that would need
/// to be smuggled across the `invoke_from_event_loop` boundary.
pub fn with_state(f: impl FnOnce(&mut AppState)) {
    STATE.with(|s| {
        if let Some(state) = s.borrow_mut().as_mut() {
            f(state);
        }
    });
}

/// Which decision the currently visible modal dialog is asking about, so
/// the shared confirmed/cancelled callbacks know what to do.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum PendingDialog {
    None,
    Info,
    SetupOffer,
}

pub struct AppState {
    window: slint::Weak<AppWindow>,
    tokio_handle: tokio::runtime::Handle,
    queue: Vec<PlaybackItem>,
    current_index: i32,
    is_preparing_track: bool,
    is_media_loaded: bool,
    is_playing: bool,
    is_dragging_seek_bar: bool,
    /// True while a native dialog (file/folder picker) is open, a source
    /// collection is running on a background thread, or a playlist fetch is
    /// in flight. Guards Open/Add Album/Add Source (and queue/history
    /// interactions) against re-entrant calls while the async work completes;
    /// the UI event loop keeps running the whole time, so clicks are
    /// dispatched immediately and rejected here instead of piling up.
    is_busy: bool,
    pending_dialog: PendingDialog,
    loop_mode: LoopMode,
    has_user_loop_preference: bool,
    /// 0-200 (100 = 0dB); above 100 libvlc software-amplifies.
    volume_percent: i32,
    is_muted: bool,
    history: PlaybackHistoryService,
    discord: DiscordPresenceService,
    ytdlp: Arc<YtDlpAudioCache>,
    ffmpeg: Arc<FfmpegAudioCache>,
    playback_controller: Option<PlaybackController>,
    track_load_cancellation: Option<CancellationToken>,
    track_load_generation: u64,
    position_timer: Timer,
}

impl AppState {
    fn new(window: slint::Weak<AppWindow>, tokio_handle: tokio::runtime::Handle) -> Self {
        let state = Self {
            window,
            tokio_handle,
            queue: Vec::new(),
            current_index: -1,
            is_preparing_track: false,
            is_media_loaded: false,
            is_playing: false,
            is_dragging_seek_bar: false,
            is_busy: false,
            pending_dialog: PendingDialog::None,
            loop_mode: LoopMode::All,
            has_user_loop_preference: false,
            volume_percent: 100,
            is_muted: false,
            history: PlaybackHistoryService::load(),
            discord: DiscordPresenceService::new(),
            ytdlp: Arc::new(YtDlpAudioCache::new()),
            ffmpeg: Arc::new(FfmpegAudioCache::new()),
            playback_controller: None,
            track_load_cancellation: None,
            track_load_generation: 0,
            position_timer: Timer::default(),
        };
        state.load_history_into_ui();
        state
    }

    pub fn apply_initial_sources(
        &mut self,
        sources: Vec<String>,
        shuffle: bool,
        start_index: Option<i32>,
        loop_mode: LoopMode,
        has_explicit_loop: bool,
    ) {
        self.loop_mode = loop_mode;
        self.has_user_loop_preference = has_explicit_loop;
        self.update_loop_button();

        if sources.is_empty() {
            self.refresh_ui();
            return;
        }

        self.add_sources_async(sources, false, shuffle, move |state, added| {
            if added.is_empty() {
                return;
            }
            let clamped = start_index.unwrap_or(0).clamp(0, added.len() as i32 - 1) as usize;
            state.start_track(clamped);
        });
    }

    /// Runs the potentially slow source collection (directory scans stat
    /// every entry, which can take seconds on large folders or network
    /// drives) off the UI thread, then applies the result and calls
    /// `on_collected` back on it. `is_busy` stays set for the whole span so
    /// the buttons stay locked while the event loop keeps pumping.
    fn add_sources_async(
        &mut self,
        raw_sources: Vec<String>,
        append: bool,
        shuffle: bool,
        on_collected: impl FnOnce(&mut AppState, Vec<PlaybackItem>) + Send + 'static,
    ) {
        self.is_busy = true;
        self.with_window(|w| w.set_status("Loading sources...".into()));
        self.refresh_ui();

        let ytdlp = self.ytdlp.clone();
        self.tokio_handle.spawn(async move {
            let result = tokio::task::spawn_blocking(move || {
                source_collector::collect(raw_sources, |s| ytdlp.is_supported_url(s), shuffle)
            })
            .await;

            let _ = slint::invoke_from_event_loop(move || {
                with_state(move |state| {
                    state.is_busy = false;
                    match result {
                        Ok(collected) => {
                            let added = state.apply_collected_sources(collected, append);
                            on_collected(state, added);
                        }
                        Err(join_error) => {
                            eprintln!("Source collection failed: {join_error}");
                            state.with_window(|w| w.set_status("Could not add source.".into()));
                            state.refresh_ui();
                        }
                    }
                });
            });
        });
    }

    fn apply_collected_sources(
        &mut self,
        result: source_collector::PlaybackSourceCollectionResult,
        append: bool,
    ) -> Vec<PlaybackItem> {
        if !append {
            self.cancel_track_preparation();
            self.stop_playback(true);
            self.queue.clear();
        }

        for album in &result.albums {
            self.history.record_album(&album.path, album.track_count as i32, None);
        }

        for item in &result.items {
            self.queue.push(item.clone());
        }

        self.current_index = if self.queue.is_empty() {
            -1
        } else if append {
            self.current_index
        } else {
            0
        };

        self.apply_default_loop_mode_for_queue();
        self.load_history_into_ui();
        self.load_queue_into_ui();
        self.refresh_ui();
        result.items
    }

    pub fn start_track(&mut self, index: usize) {
        if index >= self.queue.len() {
            return;
        }

        self.cancel_track_preparation();
        let cancellation = CancellationToken::new();
        self.track_load_cancellation = Some(cancellation.clone());
        self.track_load_generation += 1;
        let generation = self.track_load_generation;

        self.current_index = index as i32;
        self.is_preparing_track = true;
        self.is_media_loaded = false;
        self.is_playing = false;
        self.is_dragging_seek_bar = false;

        self.position_timer.stop();
        self.reset_transport_ui();
        self.stop_playback_controller(true);

        let item = self.queue[index].clone();
        let track_title = item.display_name.clone();
        let queue_info = ui_bridge::build_queue_text(&item, index, self.queue.len());
        let status = resolve::preparation_status(item.is_url_source, item.is_album_source).to_string();
        self.with_window(move |w| {
            w.set_track_title(track_title.into());
            w.set_queue_info(queue_info.into());
            w.set_status(status.into());
        });
        self.refresh_ui();

        let ytdlp = self.ytdlp.clone();
        let ffmpeg = self.ffmpeg.clone();
        let window = self.window.clone();
        let request = resolve::TrackResolveRequest {
            item_path: item.path.clone(),
            item_kind: item.kind,
            is_url_source: item.is_url_source,
            current_index: index,
            queue_len: self.queue.len(),
        };

        self.tokio_handle.spawn(async move {
            let result = resolve::resolve_playback_path(request, ytdlp, ffmpeg, cancellation, window).await;

            let _ = slint::invoke_from_event_loop(move || {
                with_state(|state| state.handle_track_resolved(generation, index, result));
            });
        });
    }

    fn handle_track_resolved(&mut self, generation: u64, index: usize, result: Result<ResolvedPlayback, ResolveError>) {
        if generation != self.track_load_generation {
            return;
        }

        match result {
            Ok(resolved) => {
                let mut resolved_title = None;
                if let Some(item) = self.queue.get_mut(index) {
                    if let Some(new_name) = &resolved.updated_display_name {
                        item.update_display_name(Some(new_name));
                        resolved_title = Some(item.display_name.clone());
                    }
                }
                // The big title was set from the pre-resolve display name in
                // start_track (the raw URL for URL sources); push the
                // resolved one now instead of waiting for the next track.
                if let Some(title) = resolved_title {
                    self.with_window(move |w| w.set_track_title(title.into()));
                }
                if let Some(queue_info) = resolved.queue_info_override.clone() {
                    self.with_window(move |w| w.set_queue_info(queue_info.into()));
                }
                self.load_queue_into_ui();

                match self.ensure_playback_controller() {
                    Ok(controller) => {
                        let started = controller.play(&resolved.playback_path);
                        if !started {
                            self.handle_playback_failure("Could not start playback with LibVLC.".to_string());
                        }
                    }
                    Err(message) => self.handle_playback_failure(message),
                }
            }
            Err(ResolveError::Cancelled) => {}
            Err(ResolveError::Message(message)) => self.handle_playback_failure(message),
        }

        if !self.is_media_loaded {
            self.is_preparing_track = false;
            self.refresh_ui();
        }
    }

    fn ensure_playback_controller(&mut self) -> Result<&mut PlaybackController, String> {
        if self.playback_controller.is_none() {
            let (mut controller, mut receiver) = PlaybackController::new()?;
            controller.set_volume(self.volume_percent);
            controller.set_mute(self.is_muted);
            self.tokio_handle.spawn(async move {
                while let Some(event) = receiver.recv().await {
                    let _ = slint::invoke_from_event_loop(move || {
                        with_state(|state| state.handle_playback_event(event));
                    });
                }
            });
            self.playback_controller = Some(controller);
        }
        Ok(self.playback_controller.as_mut().unwrap())
    }

    fn handle_playback_event(&mut self, event: PlaybackEvent) {
        match event {
            PlaybackEvent::Started => {
                self.is_preparing_track = false;
                self.is_media_loaded = true;
                self.is_playing = true;
                // Volume/mute set before the audio output existed is dropped
                // by libvlc, so re-apply them now that playback has started.
                let (volume, muted) = (self.volume_percent, self.is_muted);
                if let Some(controller) = self.playback_controller.as_mut() {
                    controller.set_volume(volume);
                    controller.set_mute(muted);
                }
                self.start_position_timer();
                self.on_position_tick();
                self.record_track_history();
                self.update_discord_presence();
                self.refresh_ui();
            }
            PlaybackEvent::Ended => self.continue_after_media_ended(),
            PlaybackEvent::Failed(message) => self.handle_playback_failure(message),
        }
    }

    fn continue_after_media_ended(&mut self) {
        self.position_timer.stop();

        if self.loop_mode == LoopMode::One {
            self.start_track(self.current_index.max(0) as usize);
            return;
        }

        let next_index = get_next_index(self.current_index, self.queue.len() as i32, self.loop_mode);
        if next_index >= 0 {
            self.start_track(next_index as usize);
            return;
        }

        self.is_playing = false;
        self.is_media_loaded = false;
        self.discord.clear();
        self.refresh_ui();
    }

    pub fn toggle_playback(&mut self) {
        if self.is_busy || self.queue.is_empty() || self.is_preparing_track {
            return;
        }

        if self.current_index < 0 {
            self.start_track(0);
            return;
        }

        if !self.is_media_loaded {
            self.start_track(self.current_index as usize);
            return;
        }

        if self.is_playing {
            if let Some(controller) = self.playback_controller.as_mut() {
                controller.pause();
            }
            self.position_timer.stop();
            self.is_playing = false;
        } else {
            if let Some(controller) = self.playback_controller.as_mut() {
                controller.resume();
            }
            self.start_position_timer();
            self.is_playing = true;
        }

        self.update_discord_presence();
        self.refresh_ui();
    }

    pub fn go_to_previous_track(&mut self) {
        if self.is_busy || self.queue.is_empty() {
            return;
        }

        if self.is_media_loaded {
            if let Some(controller) = self.playback_controller.as_mut() {
                if controller.position() > Duration::from_secs(3) {
                    controller.set_position(Duration::ZERO);
                    self.on_position_tick();
                    return;
                }
            }
        }

        if self.current_index < 0 || self.queue.len() == 1 {
            self.start_track(0);
            return;
        }

        let previous_index = get_previous_index(self.current_index, self.queue.len() as i32, self.loop_mode);
        self.start_track(previous_index.max(0) as usize);
    }

    pub fn go_to_next_track(&mut self) {
        if self.is_busy || self.queue.is_empty() {
            return;
        }
        if self.current_index < 0 {
            self.start_track(0);
            return;
        }
        let next_index = get_next_index(self.current_index, self.queue.len() as i32, self.loop_mode);
        if next_index >= 0 {
            self.start_track(next_index as usize);
        }
    }

    pub fn reverse_queue(&mut self) {
        if self.is_busy || self.queue.len() < 2 || self.is_preparing_track {
            return;
        }

        let current_item = self.current_item().cloned();
        self.queue.reverse();
        self.current_index = match &current_item {
            Some(item) => self
                .queue
                .iter()
                .position(|candidate| candidate == item)
                .map(|i| i as i32)
                .unwrap_or(-1),
            None => -1,
        };

        self.load_queue_into_ui();
        self.with_window(|w| w.set_status("Queue order reversed.".into()));
        self.refresh_ui();
    }

    pub fn seek_by(&mut self, offset_seconds: f64) {
        if !self.is_media_loaded {
            return;
        }
        let Some(controller) = self.playback_controller.as_mut() else {
            return;
        };
        if controller.duration() <= Duration::ZERO {
            return;
        }

        let duration = controller.duration();
        let next = (controller.position().as_secs_f64() + offset_seconds).clamp(0.0, duration.as_secs_f64());
        controller.set_position(Duration::from_secs_f64(next));
        self.on_position_tick();
        self.update_discord_presence();
    }

    pub fn seek_drag_started(&mut self) {
        self.is_dragging_seek_bar = true;
    }

    pub fn set_volume(&mut self, volume: f64) {
        let clamped = volume.round().clamp(0.0, 200.0) as i32;
        self.volume_percent = clamped;
        if let Some(controller) = self.playback_controller.as_mut() {
            controller.set_volume(clamped);
        }
    }

    pub fn set_muted(&mut self, muted: bool) {
        self.is_muted = muted;
        if let Some(controller) = self.playback_controller.as_mut() {
            controller.set_mute(muted);
        }
    }

    pub fn seek_drag_finished(&mut self, value: f64) {
        self.is_dragging_seek_bar = false;
        if !self.is_media_loaded {
            return;
        }
        let Some(controller) = self.playback_controller.as_mut() else {
            return;
        };
        if controller.duration() <= Duration::ZERO {
            return;
        }

        controller.set_position(Duration::from_secs_f64(value.max(0.0)));
        self.on_position_tick();
        self.update_discord_presence();
    }

    pub fn cycle_loop_mode(&mut self) {
        if self.is_busy {
            return;
        }
        self.has_user_loop_preference = true;
        self.loop_mode = self.loop_mode.next();
        self.update_loop_button();
        self.refresh_ui();
    }

    /// Ties the native file/folder dialog to our window as its true OS-level
    /// parent, making it application-modal: the OS disables our window while
    /// the dialog is open, so stray clicks are rejected outright instead of
    /// reaching the (still-running) event loop and bouncing off `is_busy`.
    fn parented_dialog(&self, dialog: rfd::AsyncFileDialog) -> rfd::AsyncFileDialog {
        match self.window.upgrade() {
            Some(window) => dialog.set_parent(&window.window().window_handle()),
            None => dialog,
        }
    }

    /// Mirrors MainWindow.axaml.cs's `Window_Drop`. Windows-only for now (see
    /// `windows_drop_target.rs`); macOS/Linux users fall back to Open/Add
    /// Album until a cross-platform drop path is implemented.
    pub fn handle_dropped_paths(&mut self, paths: Vec<String>) {
        if self.is_busy || paths.is_empty() {
            return;
        }

        let append = !self.queue.is_empty() && paths.iter().any(|p| Path::new(p).is_dir());
        self.add_sources_async(paths, append, false, move |state, added| {
            if !added.is_empty() && (state.current_index < 0 || !append) {
                let start_index = if append { state.queue.len() - added.len() } else { 0 };
                state.start_track(start_index);
            }
        });
    }

    pub fn open_files(&mut self) {
        if self.is_busy {
            return;
        }
        self.is_busy = true;
        self.refresh_ui();

        let extensions: Vec<String> = media_file_types::supported_extensions().map(str::to_string).collect();

        let dialog = self.parented_dialog(
            rfd::AsyncFileDialog::new()
                .set_title("Select audio or video files")
                .add_filter("Media files", &extensions),
        );
        let picked = dialog.pick_files();
        let _ = slint::spawn_local(async move {
            let picked = picked.await;
            with_state(|state| state.handle_open_files_picked(picked));
        });
    }

    fn handle_open_files_picked(&mut self, picked: Option<Vec<rfd::FileHandle>>) {
        let Some(handles) = picked else {
            self.is_busy = false;
            self.refresh_ui();
            return;
        };

        let sources: Vec<String> = handles
            .iter()
            .map(|handle| handle.path().to_string_lossy().to_string())
            .collect();
        self.add_sources_async(sources, false, false, |state, added| {
            if !added.is_empty() {
                state.start_track(0);
            }
        });
    }

    pub fn add_album(&mut self) {
        if self.is_busy {
            return;
        }
        self.is_busy = true;
        self.refresh_ui();

        let dialog = self.parented_dialog(
            rfd::AsyncFileDialog::new().set_title("Select an album folder to append to the queue"),
        );
        let picked = dialog.pick_folder();
        let _ = slint::spawn_local(async move {
            let picked = picked.await;
            with_state(|state| state.handle_album_folder_picked(picked));
        });
    }

    fn handle_album_folder_picked(&mut self, picked: Option<rfd::FileHandle>) {
        let Some(folder) = picked else {
            self.is_busy = false;
            self.refresh_ui();
            return;
        };

        let had_current_track = self.current_index >= 0;
        let sources = vec![folder.path().to_string_lossy().to_string()];
        self.add_sources_async(sources, true, false, move |state, added| {
            if !added.is_empty() && !had_current_track {
                state.start_track(0);
            }
        });
    }

    pub fn add_source_from_input_from_ui(&mut self) {
        let raw_input = self
            .window
            .upgrade()
            .map(|w| w.get_source_input().to_string())
            .unwrap_or_default();
        self.add_source_from_input(raw_input);
    }

    fn add_source_from_input(&mut self, raw_input: String) {
        if self.is_busy {
            return;
        }

        let source = raw_input.trim().trim_matches('"').to_string();

        if source.is_empty() {
            self.with_window(|w| w.set_status("Enter a URL, file path, or folder path.".into()));
            return;
        }

        let is_url = self.ytdlp.is_supported_url(&source);
        let is_dir = Path::new(&source).is_dir();
        let is_file = Path::new(&source).is_file();
        let is_supported_file = is_file && media_file_types::is_supported(&source);

        if !is_url && !is_dir && !is_supported_file {
            let message = if is_file {
                "That file exists, but its extension is not supported for playback."
            } else {
                "Enter a valid URL, existing file path, or existing folder path."
            };
            self.with_window(|w| w.set_status(message.into()));
            return;
        }

        self.with_window(|w| w.set_source_input("".into()));

        if self.ytdlp.is_youtube_playlist_page_url(&source) {
            self.add_youtube_playlist_source(source, !self.queue.is_empty());
            return;
        }

        let append = !self.queue.is_empty();
        self.add_sources_async(vec![source], append, false, |state, added| {
            let should_start = !added.is_empty()
                && (state.current_index < 0 || (!state.is_playing && state.queue.len() == added.len()));
            if should_start {
                state.start_track(0);
            }
        });
    }

    fn add_youtube_playlist_source(&mut self, playlist_url: String, append: bool) {
        if self.is_busy {
            return;
        }
        self.is_busy = true;
        self.with_window(|w| w.set_status("Fetching playlist...".into()));
        self.refresh_ui();

        let ytdlp = self.ytdlp.clone();
        let cancellation = CancellationToken::new();

        self.tokio_handle.spawn(async move {
            let result = ytdlp.get_playlist(&playlist_url, &cancellation).await;
            let _ = slint::invoke_from_event_loop(move || {
                with_state(|state| state.handle_playlist_resolved(append, result));
            });
        });
    }

    fn handle_playlist_resolved(
        &mut self,
        append: bool,
        result: Result<YtDlpPlaylistResult, smp_playback::YtDlpError>,
    ) {
        self.is_busy = false;

        let playlist = match result {
            Ok(playlist) => playlist,
            Err(err) => {
                self.with_window(|w| w.set_status("Could not add source.".into()));
                eprintln!("Playlist error: {err}");
                self.refresh_ui();
                return;
            }
        };

        let added_items: Vec<PlaybackItem> = playlist
            .entries
            .iter()
            .map(|entry| PlaybackItem::from_playlist_track(entry.url.clone(), &playlist.title, entry.title.clone()))
            .collect();

        if !append {
            self.cancel_track_preparation();
            self.stop_playback(true);
            self.queue.clear();
            self.current_index = -1;
        }

        for item in &added_items {
            self.queue.push(item.clone());
        }

        self.current_index = if self.queue.is_empty() {
            -1
        } else if append {
            self.current_index
        } else {
            0
        };

        self.apply_default_loop_mode_for_queue();
        self.history.record_album(&playlist.url, added_items.len() as i32, Some(&playlist.title));
        self.load_history_into_ui();
        self.load_queue_into_ui();

        let status = if added_items.is_empty() { "Playlist is empty." } else { "Playlist added." };
        self.with_window(|w| w.set_status(status.into()));
        self.refresh_ui();

        if !append && !added_items.is_empty() {
            self.start_track(0);
        }
    }

    pub fn remove_album_history(&mut self, index: usize) {
        if self.is_busy {
            return;
        }
        let Some(entry) = self.history.albums().get(index).cloned() else {
            return;
        };
        if self.history.remove_album(&entry.album_path) {
            self.load_history_into_ui();
            self.with_window(|w| w.set_status("Removed album from history.".into()));
        }
    }

    pub fn remove_track_history(&mut self, index: usize) {
        if self.is_busy {
            return;
        }
        let Some(entry) = self.history.tracks().get(index).cloned() else {
            return;
        };
        if self.history.remove_track(&entry.source_path) {
            self.load_history_into_ui();
            self.with_window(|w| w.set_status("Removed track from history.".into()));
        }
    }

    pub fn queue_item_double_clicked(&mut self, index: usize) {
        if self.is_busy {
            return;
        }
        if index < self.queue.len() {
            self.start_track(index);
        }
    }

    pub fn replay_album_history(&mut self, index: usize) {
        if self.is_busy {
            return;
        }
        let Some(entry) = self.history.albums().get(index).cloned() else {
            return;
        };

        if self.ytdlp.is_youtube_playlist_page_url(&entry.album_path) {
            self.add_youtube_playlist_source(entry.album_path, false);
            return;
        }

        if !Path::new(&entry.album_path).is_dir() {
            self.with_window(|w| w.set_status("Album folder was not found.".into()));
            return;
        }

        self.add_sources_async(vec![entry.album_path], false, false, |state, added| {
            if !added.is_empty() {
                state.start_track(0);
            }
        });
    }

    pub fn replay_track_history(&mut self, index: usize) {
        if self.is_busy {
            return;
        }
        let Some(entry) = self.history.tracks().get(index).cloned() else {
            return;
        };

        let is_url = self.ytdlp.is_supported_url(&entry.source_path);
        if !is_url && !Path::new(&entry.source_path).is_file() {
            self.with_window(|w| w.set_status("Track file was not found.".into()));
            return;
        }

        self.add_sources_async(vec![entry.source_path], false, false, |state, added| {
            if !added.is_empty() {
                state.start_track(0);
            }
        });
    }

    fn on_position_tick(&mut self) {
        let (duration, position) = match self.playback_controller.as_ref() {
            Some(controller) if controller.duration() > Duration::ZERO => {
                (controller.duration(), controller.position())
            }
            _ => {
                self.with_window(|w| {
                    w.set_elapsed_text("00:00".into());
                    w.set_remaining_text("00:00".into());
                    w.set_seek_value(0.0);
                    w.set_seek_maximum(1.0);
                });
                return;
            }
        };

        let duration_secs = duration.as_secs_f64();
        let position_secs = position.as_secs_f64();

        if !self.is_dragging_seek_bar {
            let seek_maximum = duration_secs.max(1.0);
            let seek_value = position_secs.clamp(0.0, seek_maximum);
            self.with_window(move |w| {
                w.set_seek_maximum(seek_maximum as f32);
                w.set_seek_value(seek_value as f32);
            });
        }

        let elapsed = ui_bridge::format_time(position_secs);
        let remaining = format!("-{}", ui_bridge::format_time((duration_secs - position_secs).max(0.0)));
        self.with_window(move |w| {
            w.set_elapsed_text(elapsed.into());
            w.set_remaining_text(remaining.into());
        });
    }

    fn start_position_timer(&self) {
        self.position_timer
            .start(TimerMode::Repeated, Duration::from_millis(250), || {
                with_state(|state| state.on_position_tick());
            });
    }

    fn update_discord_presence(&mut self) {
        if self.current_index < 0 || (self.current_index as usize) >= self.queue.len() {
            self.discord.clear();
            return;
        }

        let position = self.playback_controller.as_ref().map_or(Duration::ZERO, |c| c.position());
        let duration = self.playback_controller.as_ref().map_or(Duration::ZERO, |c| c.duration());
        let index = self.current_index as usize;
        let total = self.queue.len();
        let is_playing = self.is_playing;
        let item = self.queue[index].clone();

        self.discord.set_now_playing(&item, index, total, is_playing, position, duration);
    }

    fn record_track_history(&mut self) {
        if let Some(item) = self.current_item().cloned() {
            self.history.record_track(&item);
            self.load_history_into_ui();
        }
    }

    fn handle_playback_failure(&mut self, message: String) {
        self.position_timer.stop();
        self.is_preparing_track = false;
        self.is_media_loaded = false;
        self.is_playing = false;
        self.discord.clear();

        let detailed_message = self.augment_failure_message(message);
        eprintln!("Playback error: {detailed_message}");
        self.with_window(|w| w.set_status("Playback failed.".into()));
        self.refresh_ui();
        self.show_info_dialog("Playback error", &format!("Could not play media.\n{detailed_message}"));
    }

    fn augment_failure_message(&self, message: String) -> String {
        if let Some(item) = self.current_item() {
            if self.ytdlp.is_supported_url(&item.path) && !self.ytdlp.is_available() {
                return "yt-dlp was not found in PATH or the bundled tools directory, so this URL could not be fetched."
                    .to_string();
            }
            if media_file_types::requires_transcode(&item.path) && !self.ffmpeg.is_available() {
                return "ffmpeg was not found in PATH or the bundled tools directory, so this file could not be decoded."
                    .to_string();
            }
        }
        message
    }

    fn apply_default_loop_mode_for_queue(&mut self) {
        if self.has_user_loop_preference {
            return;
        }

        let default_mode = if self.queue.len() == 1 && !self.queue[0].is_album_source {
            LoopMode::None
        } else {
            LoopMode::All
        };

        if self.loop_mode != default_mode {
            self.loop_mode = default_mode;
            self.update_loop_button();
        }
    }

    fn update_loop_button(&self) {
        let text = self.loop_mode.display_text();
        let button_text = text.replace("Loop ", "Loop: ");
        let badge_text = text.to_string();
        self.with_window(move |w| {
            w.set_loop_button_content(button_text.into());
            w.set_loop_mode_badge(badge_text.into());
        });
    }

    fn cancel_track_preparation(&mut self) {
        if let Some(token) = self.track_load_cancellation.take() {
            token.cancel();
        }
    }

    fn stop_playback(&mut self, clear_source: bool) {
        self.position_timer.stop();
        self.is_dragging_seek_bar = false;
        self.is_preparing_track = false;
        self.is_media_loaded = false;
        self.is_playing = false;
        self.stop_playback_controller(clear_source);
        self.reset_transport_ui();
        self.discord.clear();
    }

    fn stop_playback_controller(&mut self, clear_media: bool) {
        if let Some(controller) = self.playback_controller.as_mut() {
            controller.stop(clear_media);
        }
    }

    fn reset_transport_ui(&self) {
        self.with_window(|w| {
            w.set_seek_value(0.0);
            w.set_seek_maximum(1.0);
            w.set_elapsed_text("00:00".into());
            w.set_remaining_text("00:00".into());
        });
    }

    fn current_item(&self) -> Option<&PlaybackItem> {
        if self.current_index >= 0 {
            self.queue.get(self.current_index as usize)
        } else {
            None
        }
    }

    fn load_queue_into_ui(&self) {
        self.with_window(|w| w.set_queue(ui_bridge::queue_model(&self.queue).into()));
    }

    fn load_history_into_ui(&self) {
        self.with_window(|w| {
            w.set_album_history(ui_bridge::album_history_model(self.history.albums()).into());
            w.set_track_history(ui_bridge::track_history_model(self.history.tracks()).into());
        });
    }

    pub fn refresh_ui(&self) {
        let queue_len = self.queue.len();

        let play_pause_content = if self.is_preparing_track {
            "Loading"
        } else if self.is_playing {
            "Pause"
        } else {
            "Play"
        };
        let is_play_pause_enabled = queue_len > 0 && !self.is_busy;
        let is_reverse_queue_enabled = queue_len > 1 && !self.is_preparing_track && !self.is_busy;
        let is_seek_enabled = self.is_media_loaded && !self.is_preparing_track;
        let is_preparation_visible = self.is_preparing_track;
        let is_open_enabled = !self.is_busy;
        let is_add_album_enabled = !self.is_busy;
        let is_add_source_enabled = !self.is_busy;
        let is_loop_enabled = !self.is_busy;
        let is_source_input_enabled = !self.is_busy;

        let queue_summary = if queue_len == 0 { "0 queued".to_string() } else { format!("{queue_len} queued") };
        let queue_count_text = if queue_len == 0 { "Queue empty".to_string() } else { queue_summary.clone() };
        let current_index_text = if self.current_index >= 0 && (self.current_index as usize) < queue_len {
            format!("Track {}/{}", self.current_index + 1, queue_len)
        } else {
            "Track --".to_string()
        };
        let source_badge = self
            .current_item()
            .map(|item| item.source_label.to_string())
            .unwrap_or_else(|| "Standby".to_string());

        let mut track_title = None;
        let mut queue_info = None;
        let mut status = None;

        if queue_len == 0 {
            track_title = Some("Drop files or use Open.".to_string());
            queue_info = Some("Load a folder, pick files, or paste a URL to start playback.".to_string());
            status = Some("Ready for local files, albums, and URL playback.".to_string());
        } else if self.current_index >= 0 && (self.current_index as usize) < queue_len {
            let item = &self.queue[self.current_index as usize];
            queue_info = Some(ui_bridge::build_queue_text(item, self.current_index as usize, queue_len));

            if !self.is_preparing_track {
                status = Some(if self.is_playing {
                    "Playing.".to_string()
                } else if self.is_media_loaded {
                    "Paused.".to_string()
                } else {
                    "Queue ready.".to_string()
                });
            }
        }

        self.with_window(move |w| {
            w.set_play_pause_content(play_pause_content.into());
            w.set_is_play_pause_enabled(is_play_pause_enabled);
            w.set_is_previous_enabled(is_play_pause_enabled);
            w.set_is_next_enabled(is_play_pause_enabled);
            w.set_is_reverse_queue_enabled(is_reverse_queue_enabled);
            w.set_is_seek_enabled(is_seek_enabled);
            w.set_is_preparation_visible(is_preparation_visible);
            w.set_is_open_enabled(is_open_enabled);
            w.set_is_add_album_enabled(is_add_album_enabled);
            w.set_is_add_source_enabled(is_add_source_enabled);
            w.set_is_loop_enabled(is_loop_enabled);
            w.set_is_source_input_enabled(is_source_input_enabled);
            w.set_queue_summary(queue_summary.into());
            w.set_queue_count_text(queue_count_text.into());
            w.set_current_index_text(current_index_text.into());
            w.set_source_badge(source_badge.into());

            if let Some(title) = track_title {
                w.set_track_title(title.into());
            }
            if let Some(info) = queue_info {
                w.set_queue_info(info.into());
            }
            if let Some(status) = status {
                w.set_status(status.into());
            }
        });
    }

    fn with_window(&self, f: impl FnOnce(&AppWindow)) {
        if let Some(window) = self.window.upgrade() {
            f(&window);
        }
    }

    fn show_info_dialog(&mut self, title: &str, message: &str) {
        self.pending_dialog = PendingDialog::Info;
        let (title, message) = (title.to_string(), message.to_string());
        self.with_window(move |w| {
            w.set_dialog_title(title.into());
            w.set_dialog_message(message.into());
            w.set_dialog_confirm_text("OK".into());
            w.set_dialog_cancel_text("".into());
            w.set_dialog_visible(true);
        });
    }

    fn hide_dialog(&mut self) {
        self.pending_dialog = PendingDialog::None;
        self.with_window(|w| w.set_dialog_visible(false));
    }

    /// Mirrors MainWindow.axaml.cs's OfferAppSetupIfNeededAsync (minus the
    /// PowerShell script; smp-setup does the work natively).
    pub fn offer_app_setup_if_needed(&mut self) {
        if !smp_setup::should_offer_setup() || self.pending_dialog != PendingDialog::None {
            return;
        }

        self.pending_dialog = PendingDialog::SetupOffer;
        self.with_window(|w| {
            w.set_dialog_title("Set up Simple Music Player".into());
            w.set_dialog_message(
                "Simple Music Player can finish setting itself up for this folder.\n\n\
                 It will add a Start Menu shortcut, Explorer menu entries, file association hints, \
                 and download optional playback tools if they are missing.\n\nRun setup now?"
                    .into(),
            );
            w.set_dialog_confirm_text("Run setup".into());
            w.set_dialog_cancel_text("Later".into());
            w.set_dialog_visible(true);
        });
    }

    pub fn dialog_confirmed(&mut self) {
        let pending = self.pending_dialog;
        self.hide_dialog();

        if pending == PendingDialog::SetupOffer {
            self.run_app_setup();
        }
    }

    pub fn dialog_cancelled(&mut self) {
        let pending = self.pending_dialog;
        self.hide_dialog();

        if pending == PendingDialog::SetupOffer {
            smp_setup::mark_dismissed();
        }
    }

    fn run_app_setup(&mut self) {
        if self.is_busy {
            return;
        }
        self.is_busy = true;
        self.with_window(|w| w.set_status("Running first-time setup...".into()));
        self.refresh_ui();

        self.tokio_handle.spawn(async {
            let result =
                tokio::task::spawn_blocking(|| smp_setup::run_setup(false, env!("CARGO_PKG_VERSION")))
                    .await
                    .unwrap_or_else(|join_error| smp_setup::SetupRunResult {
                        success: false,
                        message: format!("Setup crashed: {join_error}"),
                    });

            let _ = slint::invoke_from_event_loop(move || {
                with_state(|state| state.handle_setup_finished(result));
            });
        });
    }

    fn handle_setup_finished(&mut self, result: smp_setup::SetupRunResult) {
        self.is_busy = false;
        // Tools may have just been downloaded into tools/, so re-resolve
        // (mirrors ReloadExternalToolCaches in MainWindow.axaml.cs).
        self.ytdlp = Arc::new(YtDlpAudioCache::new());
        self.ffmpeg = Arc::new(FfmpegAudioCache::new());
        self.refresh_ui();

        if result.success {
            self.with_window(|w| w.set_status("Setup complete.".into()));
            self.show_info_dialog(
                "Setup complete",
                &format!("Setup finished. Shortcuts and optional tools are ready for this app folder.\n{}", result.message),
            );
        } else {
            self.with_window(|w| w.set_status("Setup incomplete.".into()));
            self.show_info_dialog("Setup error", &format!("Setup could not finish.\n{}", result.message));
        }
    }
}
