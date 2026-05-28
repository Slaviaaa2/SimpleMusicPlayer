using System.IO;

namespace SimpleMusicPlayer;

internal sealed class PlaybackSourceCollector
{
    public PlaybackSourceCollectionResult Collect(
        IEnumerable<string> rawSources,
        Func<string, bool> isSupportedUrl,
        bool shuffle,
        Random random)
    {
        var items = new List<PlaybackItem>();
        var albums = new List<AlbumSourceRecord>();

        foreach (var source in rawSources.Where(static path => !string.IsNullOrWhiteSpace(path)))
        {
            if (Directory.Exists(source))
            {
                AddAlbumSource(source, items, albums);
                continue;
            }

            if (isSupportedUrl(source))
            {
                items.Add(PlaybackItem.FromUrl(source));
                continue;
            }

            if (File.Exists(source) && MediaFileTypes.IsSupported(source))
            {
                items.Add(PlaybackItem.FromFile(source));
            }
        }

        if (shuffle && items.Count > 1)
        {
            ShuffleItems(items, random);
        }

        return new PlaybackSourceCollectionResult(items, albums);
    }

    private static void AddAlbumSource(
        string source,
        ICollection<PlaybackItem> items,
        ICollection<AlbumSourceRecord> albums)
    {
        var albumFiles = Directory.EnumerateFiles(source)
            .Where(MediaFileTypes.IsSupported)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var file in albumFiles)
        {
            items.Add(PlaybackItem.FromAlbumTrack(file));
        }

        if (albumFiles.Count > 0)
        {
            albums.Add(new AlbumSourceRecord(source, albumFiles.Count));
        }
    }

    private static void ShuffleItems(IList<PlaybackItem> items, Random random)
    {
        for (var i = items.Count - 1; i > 0; i--)
        {
            var swapIndex = random.Next(i + 1);
            (items[i], items[swapIndex]) = (items[swapIndex], items[i]);
        }
    }
}

internal sealed record PlaybackSourceCollectionResult(
    IReadOnlyList<PlaybackItem> Items,
    IReadOnlyList<AlbumSourceRecord> Albums);

internal sealed record AlbumSourceRecord(string Path, int TrackCount);
