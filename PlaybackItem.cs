using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SimpleMusicPlayer;

public sealed class PlaybackItem : INotifyPropertyChanged
{
    private string _displayName;

    public PlaybackItem(string path, bool isAlbumSource, string? displayName = null)
    {
        Path = path;
        IsAlbumSource = isAlbumSource;
        IsUrlSource = Uri.TryCreate(path, UriKind.Absolute, out var uri) &&
                      (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                       uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));
        SourceLabel = IsUrlSource ? "URL" : isAlbumSource ? "ALBUM" : "FILE";
        ContextText = BuildContextText(path, isAlbumSource, IsUrlSource);
        _displayName = string.IsNullOrWhiteSpace(displayName)
            ? BuildFallbackName(path, IsUrlSource)
            : displayName.Trim();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Path { get; }
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

    private static string BuildFallbackName(string source, bool isUrlSource)
    {
        if (isUrlSource && Uri.TryCreate(source, UriKind.Absolute, out var uri))
        {
            return uri.Host;
        }

        return System.IO.Path.GetFileNameWithoutExtension(source);
    }

    private static string BuildContextText(string source, bool isAlbumSource, bool isUrlSource)
    {
        if (isUrlSource && Uri.TryCreate(source, UriKind.Absolute, out var uri))
        {
            return uri.AbsoluteUri;
        }

        if (isAlbumSource)
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
