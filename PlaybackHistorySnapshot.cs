namespace SimpleMusicPlayer;

public sealed class PlaybackHistorySnapshot
{
    public List<AlbumHistoryEntry> Albums { get; init; } = [];
    public List<TrackHistoryEntry> Tracks { get; init; } = [];
}
