//! Phase 2 CLI harness: plays a local file or URL end-to-end through the same
//! FfmpegAudioCache -> YtDlpAudioCache -> PlaybackController pipeline the real
//! app will use, without any GUI. Run with: `cargo run --example play_file -- <path-or-url>`

use std::time::Duration;

use smp_playback::{FfmpegAudioCache, PlaybackController, PlaybackEvent, YtDlpAudioCache};
use tokio_util::sync::CancellationToken;

#[tokio::main]
async fn main() {
    let source = std::env::args().nth(1).expect("usage: play_file <path-or-url>");

    let ytdlp = YtDlpAudioCache::new();
    let ffmpeg = FfmpegAudioCache::new();
    let cancellation = CancellationToken::new();

    let playback_path = if ytdlp.is_supported_url(&source) {
        println!("Fetching audio from URL via yt-dlp (available: {})...", ytdlp.is_available());
        let cached = ytdlp
            .get_or_download(&source, &cancellation)
            .await
            .expect("yt-dlp fetch failed");
        println!("Fetched '{}' (cached: {})", cached.title, cached.was_cached);
        ffmpeg
            .get_playback_path(&cached.file_path, &cancellation)
            .await
            .expect("ffmpeg transcode failed")
    } else {
        println!(
            "Local file. Needs transcode: {} (ffmpeg available: {})",
            ffmpeg.requires_transcode(&source),
            ffmpeg.is_available()
        );
        ffmpeg
            .get_playback_path(&source, &cancellation)
            .await
            .expect("ffmpeg transcode failed")
    };

    println!("Playback path resolved to: {playback_path}");

    let (mut controller, mut events) = PlaybackController::new().expect("failed to initialize LibVLC");
    let started = controller.play(&playback_path);
    println!("play() returned: {started}");
    assert!(started, "LibVLC failed to start playback");

    let deadline = tokio::time::Instant::now() + Duration::from_secs(10);
    loop {
        tokio::select! {
            event = events.recv() => {
                match event {
                    Some(PlaybackEvent::Started) => println!(
                        "Started. duration={:?} position={:?}",
                        controller.duration(),
                        controller.position()
                    ),
                    Some(PlaybackEvent::Ended) => {
                        println!("Ended.");
                        break;
                    }
                    Some(PlaybackEvent::Failed(message)) => {
                        println!("Failed: {message}");
                        break;
                    }
                    None => break,
                }
            }
            _ = tokio::time::sleep_until(deadline) => {
                println!(
                    "Timed out waiting for playback to finish. position={:?} duration={:?}",
                    controller.position(),
                    controller.duration()
                );
                break;
            }
        }
    }

    controller.stop(true);
}
