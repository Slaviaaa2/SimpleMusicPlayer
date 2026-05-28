using System.Collections.ObjectModel;
using System.IO;

namespace SimpleMusicPlayer;

public sealed class PlaybackHistoryService
{
    private const int MaxHistoryItems = 20;

    private readonly PlaybackHistoryStore _store = new();

    public void Load(
        ObservableCollection<AlbumHistoryEntry> albums,
        ObservableCollection<TrackHistoryEntry> tracks)
    {
        var history = _store.Load();

        ReplaceItems(albums, history.Albums
            .OrderByDescending(static entry => entry.LastPlayedAt)
            .Take(MaxHistoryItems));
        ReplaceItems(tracks, history.Tracks
            .OrderByDescending(static entry => entry.LastPlayedAt)
            .Take(MaxHistoryItems));
    }

    public bool RemoveAlbum(
        ObservableCollection<AlbumHistoryEntry> albums,
        ObservableCollection<TrackHistoryEntry> tracks,
        AlbumHistoryEntry entry)
    {
        if (!albums.Remove(entry))
        {
            return false;
        }

        Save(albums, tracks);
        return true;
    }

    public bool RemoveTrack(
        ObservableCollection<AlbumHistoryEntry> albums,
        ObservableCollection<TrackHistoryEntry> tracks,
        TrackHistoryEntry entry)
    {
        if (!tracks.Remove(entry))
        {
            return false;
        }

        Save(albums, tracks);
        return true;
    }

    public void RecordAlbum(
        ObservableCollection<AlbumHistoryEntry> albums,
        ObservableCollection<TrackHistoryEntry> tracks,
        string albumPath,
        int trackCount,
        string? displayNameOverride = null)
    {
        if (string.IsNullOrWhiteSpace(albumPath) || trackCount <= 0)
        {
            return;
        }

        var displayName = string.IsNullOrWhiteSpace(displayNameOverride)
            ? Path.GetFileName(albumPath)
            : displayNameOverride;

        if (string.IsNullOrWhiteSpace(displayName))
        {
            displayName = albumPath;
        }

        var entry = new AlbumHistoryEntry(albumPath, displayName, trackCount, DateTimeOffset.Now);
        UpsertItem(
            albums,
            entry,
            static (left, right) => string.Equals(left.AlbumPath, right.AlbumPath, StringComparison.OrdinalIgnoreCase));
        Save(albums, tracks);
    }

    public void RecordTrack(
        ObservableCollection<AlbumHistoryEntry> albums,
        ObservableCollection<TrackHistoryEntry> tracks,
        PlaybackItem item)
    {
        var entry = new TrackHistoryEntry(
            item.Path,
            item.DisplayName,
            BuildTrackHistoryContext(item),
            DateTimeOffset.Now);
        UpsertItem(
            tracks,
            entry,
            static (left, right) => string.Equals(left.SourcePath, right.SourcePath, StringComparison.OrdinalIgnoreCase));
        Save(albums, tracks);
    }

    private void Save(
        ObservableCollection<AlbumHistoryEntry> albums,
        ObservableCollection<TrackHistoryEntry> tracks)
    {
        _store.Save(new PlaybackHistorySnapshot
        {
            Albums = [.. albums],
            Tracks = [.. tracks]
        });
    }

    private static string BuildTrackHistoryContext(PlaybackItem item)
        => item.IsUrlSource && !item.IsAlbumSource ? item.Path : item.ContextText;

    private static void ReplaceItems<T>(ObservableCollection<T> collection, IEnumerable<T> items)
    {
        collection.Clear();
        foreach (var item in items)
        {
            collection.Add(item);
        }
    }

    private static void UpsertItem<T>(ObservableCollection<T> collection, T item, Func<T, T, bool> isMatch)
    {
        var existingIndex = collection
            .Select((entry, index) => new { entry, index })
            .FirstOrDefault(candidate => isMatch(candidate.entry, item))
            ?.index ?? -1;

        if (existingIndex >= 0)
        {
            collection.RemoveAt(existingIndex);
        }

        collection.Insert(0, item);
        while (collection.Count > MaxHistoryItems)
        {
            collection.RemoveAt(collection.Count - 1);
        }
    }
}
