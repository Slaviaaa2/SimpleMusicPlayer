using DiscordRPC;

namespace SimpleMusicPlayer;

public sealed class DiscordPresenceService : IDisposable
{
    private const string ApplicationId = "1504140042820522125";

    private readonly DiscordRpcClient? _client;
    private readonly bool _isInitialized;

    public DiscordPresenceService()
    {
        try
        {
            _client = new DiscordRpcClient(ApplicationId);
            _isInitialized = _client.Initialize();
        }
        catch
        {
            _client?.Dispose();
            _client = null;
            _isInitialized = false;
        }
    }

    public bool IsEnabled => _isInitialized;

    public void SetNowPlaying(
        PlaybackItem item,
        int currentIndex,
        int totalCount,
        bool isPlaying,
        TimeSpan position,
        TimeSpan duration)
    {
        if (!_isInitialized || _client is null)
        {
            return;
        }

        var queueLabel = item.IsAlbumSource ? "Album" : "Queue";
        var state = isPlaying
            ? $"{queueLabel} {currentIndex + 1}/{totalCount}"
            : $"Paused · {queueLabel} {currentIndex + 1}/{totalCount}";

        var presence = new RichPresence
        {
            Type = ActivityType.Listening,
            Details = item.DisplayName,
            State = state
        };

        if (isPlaying && duration > TimeSpan.Zero)
        {
            var boundedPosition = TimeSpan.FromSeconds(Math.Clamp(position.TotalSeconds, 0, duration.TotalSeconds));
            var now = DateTime.UtcNow;
            presence.Timestamps = new Timestamps(now - boundedPosition, now + (duration - boundedPosition));
        }

        _client.SetPresence(presence);
    }

    public void Clear()
    {
        if (!_isInitialized || _client is null)
        {
            return;
        }

        _client.ClearPresence();
    }

    public void Dispose()
    {
        if (_client is null)
        {
            return;
        }

        try
        {
            _client.ClearPresence();
        }
        catch
        {
        }

        _client.Dispose();
    }
}
