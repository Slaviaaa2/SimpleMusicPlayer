using System.IO;
using System.Text.Json;

namespace SimpleMusicPlayer;

public sealed class AppSetupStateStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _stateFilePath;

    public AppSetupStateStore()
    {
        var stateDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SimpleMusicPlayer");
        _stateFilePath = Path.Combine(stateDirectory, "setup-state.json");
    }

    public AppSetupState Load()
    {
        try
        {
            if (!File.Exists(_stateFilePath))
            {
                return new AppSetupState();
            }

            var json = File.ReadAllText(_stateFilePath);
            return JsonSerializer.Deserialize<AppSetupState>(json, SerializerOptions) ?? new AppSetupState();
        }
        catch
        {
            return new AppSetupState();
        }
    }

    public void Save(AppSetupState state)
    {
        try
        {
            var directory = Path.GetDirectoryName(_stateFilePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(state, SerializerOptions);
            File.WriteAllText(_stateFilePath, json);
        }
        catch
        {
        }
    }

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
