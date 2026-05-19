using System.IO;

namespace SimpleMusicPlayer;

internal static class ToolPathResolver
{
    public static string? ResolveExecutablePath(string toolName)
    {
        foreach (var candidate in EnumerateLocalToolPaths(toolName).Concat(EnumeratePathToolPaths(toolName)))
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateLocalToolPaths(string toolName)
    {
        foreach (var executableName in EnumerateExecutableNames(toolName))
        {
            yield return Path.Combine(AppContext.BaseDirectory, "tools", toolName, executableName);
            yield return Path.Combine(AppContext.BaseDirectory, "tools", executableName);
        }
    }

    private static IEnumerable<string> EnumeratePathToolPaths(string toolName)
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            yield break;
        }

        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var executableName in EnumerateExecutableNames(toolName))
            {
                yield return Path.Combine(directory, executableName);
            }
        }
    }

    private static IEnumerable<string> EnumerateExecutableNames(string toolName)
    {
        yield return toolName;

        if (!OperatingSystem.IsWindows())
        {
            yield break;
        }

        yield return $"{toolName}.exe";
        yield return $"{toolName}.cmd";
        yield return $"{toolName}.bat";
    }
}
