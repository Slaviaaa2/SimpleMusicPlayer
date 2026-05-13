using DiscordRPC;

namespace SimpleMusicPlayer;

public sealed class DiscordPresenceService : IDisposable
{
    public const string AppIdEnvironmentVariable = "SIMPLE_MUSIC_PLAYER_DISCORD_APP_ID";

    private readonly DiscordRpcClient? _client;
    private readonly bool _isInitialized;

    public DiscordPresenceService(string? applicationId)
    {
        if (string.IsNullOrWhiteSpace(applicationId))
        {
            return;
        }

        try
        {
            _client = new DiscordRpcClient(applicationId.Trim());
            _isInitialized = _client.Initialize();
        }
        catch
        {
            _client?.Dispose();
            _client = null;
            _isInitialized = false;
        }
    }

    public static string? ResolveApplicationId(CliOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.DiscordAppId))
        {
            return options.DiscordAppId;
        }

        var environmentValue = Environment.GetEnvironmentVariable(AppIdEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(environmentValue))
        {
            return environmentValue;
        }

        var envFileValues = EnvFile.LoadFromBaseDirectory();
        return envFileValues.TryGetValue(AppIdEnvironmentVariable, out var appId) ? appId : null;
    }

    public bool IsEnabled => _isInitialized;

    public void SetNowPlaying(PlaybackItem item, int currentIndex, int totalCount, bool isPlaying)
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
            Details = item.DisplayName,
            State = state
        };

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
