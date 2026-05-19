using System.IO;
using LibVLCSharp.Shared;

namespace SimpleMusicPlayer;

internal sealed class PlaybackController : IDisposable
{
    private readonly LibVLC _libVlc;
    private readonly MediaPlayer _mediaPlayer;
    private Media? _currentMedia;
    private bool _disposed;

    public PlaybackController()
    {
        Core.Initialize();
        _libVlc = new LibVLC("--no-video", "--quiet");
        _mediaPlayer = new MediaPlayer(_libVlc);
        _mediaPlayer.Playing += (_, _) => PlaybackStarted?.Invoke(this, EventArgs.Empty);
        _mediaPlayer.EndReached += (_, _) => PlaybackEnded?.Invoke(this, EventArgs.Empty);
        _mediaPlayer.EncounteredError += (_, _) => PlaybackFailed?.Invoke("LibVLC could not decode this media.");
    }

    public event EventHandler? PlaybackStarted;
    public event EventHandler? PlaybackEnded;
    public event Action<string>? PlaybackFailed;

    public TimeSpan Duration
        => _mediaPlayer.Length > 0 ? TimeSpan.FromMilliseconds(_mediaPlayer.Length) : TimeSpan.Zero;

    public TimeSpan Position
    {
        get => _mediaPlayer.Time > 0 ? TimeSpan.FromMilliseconds(_mediaPlayer.Time) : TimeSpan.Zero;
        set
        {
            var bounded = Math.Max(value.TotalMilliseconds, 0);
            _mediaPlayer.Time = (long)bounded;
        }
    }

    public bool Play(string sourcePath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        ReplaceMedia(sourcePath);
        var media = _currentMedia ?? throw new InvalidOperationException("Playback media could not be initialized.");
        return _mediaPlayer.Play(media);
    }

    public void Pause()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _mediaPlayer.SetPause(true);
    }

    public void Resume()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _mediaPlayer.SetPause(false);
    }

    public void Stop(bool clearMedia)
    {
        if (_disposed)
        {
            return;
        }

        _mediaPlayer.Stop();
        if (clearMedia)
        {
            ClearMedia();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Stop(clearMedia: true);
        _mediaPlayer.Dispose();
        _libVlc.Dispose();
        _disposed = true;
    }

    private void ReplaceMedia(string sourcePath)
    {
        ClearMedia();
        _currentMedia = Uri.TryCreate(sourcePath, UriKind.Absolute, out var absoluteUri)
            ? new Media(_libVlc, absoluteUri)
            : new Media(_libVlc, new Uri(Path.GetFullPath(sourcePath)));
    }

    private void ClearMedia()
    {
        _currentMedia?.Dispose();
        _currentMedia = null;
    }
}
