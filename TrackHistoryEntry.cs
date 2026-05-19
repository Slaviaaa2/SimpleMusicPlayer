namespace SimpleMusicPlayer;

public sealed class TrackHistoryEntry
{
    public TrackHistoryEntry(string sourcePath, string displayName, string contextText, DateTimeOffset lastPlayedAt)
    {
        SourcePath = sourcePath;
        DisplayName = displayName;
        ContextText = contextText;
        LastPlayedAt = lastPlayedAt;
    }

    public string SourcePath { get; init; }
    public string DisplayName { get; init; }
    public string ContextText { get; init; }
    public DateTimeOffset LastPlayedAt { get; init; }
    public string LastPlayedText => LastPlayedAt.LocalDateTime.ToString("yyyy/MM/dd HH:mm");
}
