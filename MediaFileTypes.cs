namespace SimpleMusicPlayer;

internal static class MediaFileTypes
{
    private static readonly HashSet<string> SupportedExtensionSet = new(
        [
            ".mp3", ".wav", ".aac", ".m4a", ".flac", ".wma", ".ogg", ".opus",
            ".mp4", ".m4v", ".mov", ".wmv", ".avi", ".mkv", ".webm"
        ],
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> TranscodeExtensionSet = new(
        [".mkv", ".webm", ".opus"],
        StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyCollection<string> SupportedExtensions => SupportedExtensionSet;

    public static IEnumerable<string> SupportedPatterns
        => SupportedExtensionSet.Select(static extension => $"*{extension}");

    public const string OpenFileDialogFilter =
        "Media files|*.mp3;*.wav;*.aac;*.m4a;*.flac;*.wma;*.ogg;*.opus;*.mp4;*.m4v;*.mov;*.wmv;*.avi;*.mkv;*.webm|All files|*.*";

    public static bool IsSupported(string path)
        => SupportedExtensionSet.Contains(Path.GetExtension(path));

    public static bool RequiresTranscode(string path)
        => TranscodeExtensionSet.Contains(Path.GetExtension(path));
}
