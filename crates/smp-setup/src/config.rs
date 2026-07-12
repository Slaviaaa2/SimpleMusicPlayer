//! Constants carried over from scripts/install-config.psd1 (which this crate
//! replaces along with the PowerShell installer/uninstaller scripts).

pub const APP_NAME: &str = "Simple Music Player";
pub const EXE_NAME: &str = "SimpleMusicPlayer.exe";
pub const SHORTCUT_NAME: &str = "Simple Music Player.lnk";
pub const PROG_ID: &str = "SimpleMusicPlayer.media";
pub const APPLICATION_KEY_NAME: &str = "SimpleMusicPlayer.exe";
pub const CAPABILITIES_KEY_NAME: &str = "SimpleMusicPlayer";
pub const UNINSTALL_KEY_NAME: &str = "SimpleMusicPlayer";
pub const PUBLISHER: &str = "zeros";

pub const YTDLP_URL: &str = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
pub const DENO_URL: &str =
    "https://github.com/denoland/deno/releases/latest/download/deno-x86_64-pc-windows-msvc.zip";
pub const FFMPEG_URL: &str = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";

/// Extensions registered for Open With / Default apps, dot-prefixed as the
/// registry expects. Sourced from smp-core so the association list can never
/// drift from what the player actually supports.
pub fn supported_extensions() -> Vec<String> {
    smp_core::media_file_types::supported_extensions()
        .map(|ext| format!(".{ext}"))
        .collect()
}
