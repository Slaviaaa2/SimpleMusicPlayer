using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SimpleMusicPlayer;

internal sealed class YtDlpAudioCache
{
    private const string DownloadFilePrefix = "audio";
    private const string MetadataFileName = "entry.json";

    private readonly string? _ytDlpPath;
    private readonly string _cacheDirectory;

    public YtDlpAudioCache()
    {
        _ytDlpPath = ResolveToolPath("yt-dlp");
        _cacheDirectory = Path.Combine(AppContext.BaseDirectory, "cache", "yt-dlp");
    }

    public bool IsAvailable => !string.IsNullOrWhiteSpace(_ytDlpPath);

    public bool IsSupportedUrl(string source)
        => Uri.TryCreate(source, UriKind.Absolute, out var uri) &&
           (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
            uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));

    public async Task<CachedAudioResult> GetOrDownloadAsync(string sourceUrl, CancellationToken cancellationToken)
    {
        if (!IsAvailable)
        {
            throw new InvalidOperationException("yt-dlp.exe was not found in PATH.");
        }

        var normalizedUrl = NormalizeUrl(sourceUrl);
        var entryDirectory = Path.Combine(_cacheDirectory, BuildUrlCacheKey(normalizedUrl));
        Directory.CreateDirectory(entryDirectory);

        var metadataPath = Path.Combine(entryDirectory, MetadataFileName);
        var cachedFilePath = FindDownloadedFile(entryDirectory);
        if (cachedFilePath is not null)
        {
            var cachedEntry = ReadMetadata(metadataPath);
            return new CachedAudioResult(
                cachedFilePath,
                cachedEntry?.Title ?? normalizedUrl,
                normalizedUrl,
                true);
        }

        var processStartInfo = new ProcessStartInfo
        {
            FileName = _ytDlpPath!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };

        processStartInfo.ArgumentList.Add("--no-playlist");
        processStartInfo.ArgumentList.Add("--no-progress");
        processStartInfo.ArgumentList.Add("--format");
        processStartInfo.ArgumentList.Add("bestaudio[ext=m4a]/bestaudio[ext=mp4]/bestaudio/best");
        processStartInfo.ArgumentList.Add("--output");
        processStartInfo.ArgumentList.Add(Path.Combine(entryDirectory, $"{DownloadFilePrefix}.%(ext)s"));
        processStartInfo.ArgumentList.Add("--print");
        processStartInfo.ArgumentList.Add("%(title)s");
        processStartInfo.ArgumentList.Add(normalizedUrl);

        using var process = Process.Start(processStartInfo)
            ?? throw new InvalidOperationException("Failed to start yt-dlp.");

        var stdErrTask = process.StandardError.ReadToEndAsync();
        var stdOutTask = process.StandardOutput.ReadToEndAsync();

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            CleanupDownloadedFiles(entryDirectory);
            await DrainOutputAsync(stdErrTask, stdOutTask);
            throw;
        }

        await Task.WhenAll(stdErrTask, stdOutTask);

        cachedFilePath = FindDownloadedFile(entryDirectory);
        if (process.ExitCode != 0 || cachedFilePath is null)
        {
            CleanupDownloadedFiles(entryDirectory);
            var details = BuildFailureDetails(stdErrTask.Result, stdOutTask.Result);
            throw new InvalidOperationException($"yt-dlp could not fetch this URL.{details}");
        }

        var title = ParseTitle(stdOutTask.Result) ?? normalizedUrl;
        WriteMetadata(metadataPath, new CachedAudioEntry(normalizedUrl, title));

        return new CachedAudioResult(cachedFilePath, title, normalizedUrl, false);
    }

    private static string NormalizeUrl(string sourceUrl)
    {
        var uri = new Uri(sourceUrl, UriKind.Absolute);
        return uri.AbsoluteUri;
    }

    private static string BuildUrlCacheKey(string sourceUrl)
    {
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(sourceUrl));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    private static string? FindDownloadedFile(string entryDirectory)
        => Directory.EnumerateFiles(entryDirectory, $"{DownloadFilePrefix}.*", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(path => !path.EndsWith(".json", StringComparison.OrdinalIgnoreCase));

    private static CachedAudioEntry? ReadMetadata(string metadataPath)
    {
        if (!File.Exists(metadataPath))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<CachedAudioEntry>(File.ReadAllText(metadataPath));
        }
        catch
        {
            return null;
        }
    }

    private static void WriteMetadata(string metadataPath, CachedAudioEntry entry)
    {
        var json = JsonSerializer.Serialize(entry, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        File.WriteAllText(metadataPath, json);
    }

    private static string? ParseTitle(string standardOutput)
        => standardOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();

    private static string? ResolveToolPath(string toolName)
    {
        var localToolPaths = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "tools", "yt-dlp", $"{toolName}.exe"),
            Path.Combine(AppContext.BaseDirectory, "tools", $"{toolName}.exe")
        };

        foreach (var candidate in localToolPaths)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            return null;
        }

        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(directory, $"{toolName}.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string BuildFailureDetails(string standardError, string standardOutput)
    {
        var combined = string.IsNullOrWhiteSpace(standardError)
            ? standardOutput
            : standardError;

        if (string.IsNullOrWhiteSpace(combined))
        {
            return string.Empty;
        }

        var lines = combined
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .TakeLast(3);

        return $" {string.Join(" | ", lines)}";
    }

    private static async Task DrainOutputAsync(Task<string> standardErrorTask, Task<string> standardOutputTask)
    {
        try
        {
            await Task.WhenAll(standardErrorTask, standardOutputTask);
        }
        catch
        {
        }
    }

    private static void CleanupDownloadedFiles(string entryDirectory)
    {
        try
        {
            if (!Directory.Exists(entryDirectory))
            {
                return;
            }

            foreach (var path in Directory.EnumerateFiles(entryDirectory, $"{DownloadFilePrefix}*", SearchOption.TopDirectoryOnly))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }

    private sealed record CachedAudioEntry(string Url, string Title);
}

internal sealed record CachedAudioResult(string FilePath, string Title, string SourceUrl, bool WasCached);
