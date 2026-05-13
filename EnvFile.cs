using System.Collections.ObjectModel;
using System.IO;

namespace SimpleMusicPlayer;

public static class EnvFile
{
    public static IReadOnlyDictionary<string, string> LoadFromBaseDirectory()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var envPath = Path.Combine(baseDirectory, ".env");
        if (!File.Exists(envPath))
        {
            return new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in File.ReadLines(envPath))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var key = line[..separatorIndex].Trim();
            var value = line[(separatorIndex + 1)..].Trim();
            if (key.Length == 0)
            {
                continue;
            }

            values[key] = TrimWrappingQuotes(value);
        }

        return new ReadOnlyDictionary<string, string>(values);
    }

    private static string TrimWrappingQuotes(string value)
    {
        if (value.Length >= 2 &&
            ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
        {
            return value[1..^1];
        }

        return value;
    }
}
