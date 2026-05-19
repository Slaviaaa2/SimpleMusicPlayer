namespace SimpleMusicPlayer;

internal static class MediaFileTypes
{
    public static readonly string[] SupportedExtensions =
    [
        ".mp3", ".wav", ".aac", ".m4a", ".flac", ".wma", ".ogg", ".opus",
        ".mp4", ".m4v", ".mov", ".wmv", ".avi", ".mkv", ".webm"
    ];

    public const string OpenFileDialogFilter =
        "Media files|*.mp3;*.wav;*.aac;*.m4a;*.flac;*.wma;*.ogg;*.opus;*.mp4;*.m4v;*.mov;*.wmv;*.avi;*.mkv;*.webm|All files|*.*";
}
