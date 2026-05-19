namespace SimpleMusicPlayer;

public sealed class AlbumHistoryEntry
{
    public AlbumHistoryEntry(string albumPath, string displayName, int trackCount, DateTimeOffset lastPlayedAt)
    {
        AlbumPath = albumPath;
        DisplayName = displayName;
        TrackCount = trackCount;
        LastPlayedAt = lastPlayedAt;
    }

    public string AlbumPath { get; init; }
    public string DisplayName { get; init; }
    public int TrackCount { get; init; }
    public DateTimeOffset LastPlayedAt { get; init; }
    public string TrackCountText => $"{TrackCount} tracks";
    public string LastPlayedText => LastPlayedAt.LocalDateTime.ToString("yyyy/MM/dd HH:mm");
}
