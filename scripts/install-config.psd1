@{
    AppName = "Simple Music Player"
    ExeName = "SimpleMusicPlayer.exe"
    ShortcutName = "Simple Music Player.lnk"
    ProgId = "SimpleMusicPlayer.media"
    ApplicationKeyName = "SimpleMusicPlayer.exe"
    CapabilitiesKeyName = "SimpleMusicPlayer"
    UninstallKeyName = "SimpleMusicPlayer"
    ToolUrls = @{
        YtDlp = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe"
        Deno = "https://github.com/denoland/deno/releases/latest/download/deno-x86_64-pc-windows-msvc.zip"
        Ffmpeg = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip"
    }
    SupportedExtensions = @(
        ".mp3", ".wav", ".aac", ".m4a", ".flac", ".wma", ".ogg", ".opus",
        ".mp4", ".m4v", ".mov", ".wmv", ".avi", ".mkv", ".webm"
    )
}
