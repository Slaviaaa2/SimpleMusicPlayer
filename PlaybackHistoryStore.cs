using System.IO;

namespace SimpleMusicPlayer;

public sealed class PlaybackHistoryStore
{
    private readonly JsonFileStore<PlaybackHistorySnapshot> _store;

    public PlaybackHistoryStore()
    {
        var historyDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SimpleMusicPlayer");
        _store = new JsonFileStore<PlaybackHistorySnapshot>(Path.Combine(historyDirectory, "history.json"));
    }

    public PlaybackHistorySnapshot Load() => _store.Load();

    public void Save(PlaybackHistorySnapshot snapshot) => _store.Save(snapshot);
}
