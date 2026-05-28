using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SimpleMusicPlayer;

public sealed class PlaybackItem : INotifyPropertyChanged
{
    private string _displayName;

    public PlaybackItem(string path, bool isAlbumSource, string? displayName = null, string? contextText = null)
        : this(path, ResolveLegacyKind(path, isAlbumSource), displayName, contextText)
    {
    }

    public PlaybackItem(string path, PlaybackSourceKind kind, string? displayName = null, string? contextText = null)
    {
        Path = path;
        Kind = kind;
        IsAlbumSource = kind is PlaybackSourceKind.AlbumTrack or PlaybackSourceKind.PlaylistTrack;
        IsUrlSource = kind is PlaybackSourceKind.Url or PlaybackSourceKind.PlaylistTrack;
        SourceLabel = kind switch
        {
            PlaybackSourceKind.AlbumTrack => "ALBUM",
            PlaybackSourceKind.PlaylistTrack => "PLAYLIST",
            PlaybackSourceKind.Url => "URL",
            _ => "FILE"
        };
        ContextText = string.IsNullOrWhiteSpace(contextText)
            ? BuildContextText(path, kind)
            : contextText.Trim();
        _displayName = string.IsNullOrWhiteSpace(displayName)
            ? BuildFallbackName(path, IsUrlSource)
            : displayName.Trim();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Path { get; }
    public PlaybackSourceKind Kind { get; }
    public bool IsAlbumSource { get; }
    public bool IsUrlSource { get; }
    public string SourceLabel { get; }
    public string ContextText { get; }

    public string DisplayName
    {
        get => _displayName;
        private set
        {
            if (string.Equals(_displayName, value, StringComparison.Ordinal))
            {
                return;
            }

            _displayName = value;
            OnPropertyChanged();
        }
    }

    public void UpdateDisplayName(string? displayName)
    {
        if (!string.IsNullOrWhiteSpace(displayName))
        {
            DisplayName = displayName.Trim();
        }
    }

    public static PlaybackItem FromFile(string path)
        => new(path, PlaybackSourceKind.File);

    public static PlaybackItem FromAlbumTrack(string path)
        => new(path, PlaybackSourceKind.AlbumTrack);

    public static PlaybackItem FromUrl(string url, string? displayName = null)
        => new(url, PlaybackSourceKind.Url, displayName ?? url);

    public static PlaybackItem FromPlaylistTrack(string url, string playlistTitle, string? title)
        => new(url, PlaybackSourceKind.PlaylistTrack, title ?? url, $"Playlist · {playlistTitle}");

    private static PlaybackSourceKind ResolveLegacyKind(string path, bool isAlbumSource)
    {
        var isUrlSource = Uri.TryCreate(path, UriKind.Absolute, out var uri) &&
                          (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                           uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));

        if (isAlbumSource && isUrlSource)
        {
            return PlaybackSourceKind.PlaylistTrack;
        }

        if (isAlbumSource)
        {
            return PlaybackSourceKind.AlbumTrack;
        }

        return isUrlSource ? PlaybackSourceKind.Url : PlaybackSourceKind.File;
    }

    private static string BuildFallbackName(string source, bool isUrlSource)
    {
        if (isUrlSource && Uri.TryCreate(source, UriKind.Absolute, out var uri))
        {
            return uri.Host;
        }

        return System.IO.Path.GetFileNameWithoutExtension(source);
    }

    private static string BuildContextText(string source, PlaybackSourceKind kind)
    {
        if (kind is PlaybackSourceKind.Url or PlaybackSourceKind.PlaylistTrack &&
            Uri.TryCreate(source, UriKind.Absolute, out var uri))
        {
            return uri.AbsoluteUri;
        }

        if (kind == PlaybackSourceKind.AlbumTrack)
        {
            var albumDirectory = System.IO.Path.GetDirectoryName(source);
            if (!string.IsNullOrWhiteSpace(albumDirectory))
            {
                return $"Album · {System.IO.Path.GetFileName(albumDirectory)}";
            }
        }

        var directory = System.IO.Path.GetDirectoryName(source);
        return string.IsNullOrWhiteSpace(directory) ? source : directory;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
