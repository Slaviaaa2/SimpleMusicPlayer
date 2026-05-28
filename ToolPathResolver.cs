using System.IO;

namespace SimpleMusicPlayer;

internal static class ToolPathResolver
{
    public static string? ResolveExecutablePath(string toolName)
        => ResolveTool(toolName).ExecutablePath;

    public static ToolResolution ResolveTool(string toolName)
    {
        var searchedPaths = new List<string>();

        foreach (var candidate in EnumerateLocalToolPaths(toolName, ToolResolutionSource.Bundled)
                     .Concat(EnumeratePathToolPaths(toolName)))
        {
            searchedPaths.Add(candidate.Path);
            if (File.Exists(candidate.Path))
            {
                return new ToolResolution(toolName, candidate.Path, candidate.Source, searchedPaths);
            }
        }

        return new ToolResolution(toolName, null, ToolResolutionSource.NotFound, searchedPaths);
    }

    private static IEnumerable<ToolCandidate> EnumerateLocalToolPaths(string toolName, ToolResolutionSource source)
    {
        foreach (var executableName in EnumerateExecutableNames(toolName))
        {
            yield return new ToolCandidate(Path.Combine(AppContext.BaseDirectory, "tools", toolName, executableName), source);
            yield return new ToolCandidate(Path.Combine(AppContext.BaseDirectory, "tools", executableName), source);
        }
    }

    private static IEnumerable<ToolCandidate> EnumeratePathToolPaths(string toolName)
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
                yield return new ToolCandidate(Path.Combine(directory, executableName), ToolResolutionSource.Path);
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

    private sealed record ToolCandidate(string Path, ToolResolutionSource Source);
}

internal sealed record ToolResolution(
    string ToolName,
    string? ExecutablePath,
    ToolResolutionSource Source,
    IReadOnlyList<string> SearchedPaths);

internal enum ToolResolutionSource
{
    NotFound,
    Bundled,
    Path
}
