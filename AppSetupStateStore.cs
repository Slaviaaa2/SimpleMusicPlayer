using System.IO;

namespace SimpleMusicPlayer;

public sealed class AppSetupStateStore
{
    private readonly JsonFileStore<AppSetupState> _store;

    public AppSetupStateStore()
    {
        var stateDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SimpleMusicPlayer");
        _store = new JsonFileStore<AppSetupState>(Path.Combine(stateDirectory, "setup-state.json"));
    }

    public AppSetupState Load() => _store.Load();

    public void Save(AppSetupState state) => _store.Save(state);

    public void MarkCompleted(string installPath)
    {
        Save(new AppSetupState
        {
            CompletedInstallPath = NormalizePath(installPath),
            CompletedAt = DateTimeOffset.Now
        });
    }

    public void MarkDismissed(string installPath)
    {
        var currentState = Load();
        Save(new AppSetupState
        {
            CompletedInstallPath = currentState.CompletedInstallPath,
            CompletedAt = currentState.CompletedAt,
            DismissedInstallPath = NormalizePath(installPath),
            DismissedAt = DateTimeOffset.Now
        });
    }

    public static string NormalizePath(string path)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch
        {
            return path.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }
}
