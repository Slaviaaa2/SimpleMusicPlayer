using System.IO;
using System.Text.Json;

namespace SimpleMusicPlayer;

public sealed class PlaybackHistoryStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _historyFilePath;

    public PlaybackHistoryStore()
    {
        var historyDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SimpleMusicPlayer");
        _historyFilePath = Path.Combine(historyDirectory, "history.json");
    }

    public PlaybackHistorySnapshot Load()
    {
        try
        {
            if (!File.Exists(_historyFilePath))
            {
                return new PlaybackHistorySnapshot();
            }

            var json = File.ReadAllText(_historyFilePath);
            return JsonSerializer.Deserialize<PlaybackHistorySnapshot>(json, SerializerOptions) ?? new PlaybackHistorySnapshot();
        }
        catch
        {
            return new PlaybackHistorySnapshot();
        }
    }

    public void Save(PlaybackHistorySnapshot snapshot)
    {
        try
        {
            var directory = Path.GetDirectoryName(_historyFilePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(snapshot, SerializerOptions);
            File.WriteAllText(_historyFilePath, json);
        }
        catch
        {
        }
    }
}
