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
    private readonly string? _ffmpegPath;
    private readonly JavaScriptRuntimeSelection? _javaScriptRuntime;
    private readonly string _cacheDirectory;

    public YtDlpAudioCache()
    {
        _ytDlpPath = ToolPathResolver.ResolveExecutablePath("yt-dlp");
        _ffmpegPath = ToolPathResolver.ResolveExecutablePath("ffmpeg");
        _javaScriptRuntime = JavaScriptRuntimeResolver.ResolveForYtDlp();
        _cacheDirectory = Path.Combine(AppContext.BaseDirectory, "cache", "yt-dlp");
    }

    public bool IsAvailable => !string.IsNullOrWhiteSpace(_ytDlpPath);
    public bool HasSupportedJavaScriptRuntime => _javaScriptRuntime is not null;

    public bool IsSupportedUrl(string source)
        => Uri.TryCreate(source, UriKind.Absolute, out var uri) &&
           (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
            uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));

    public async Task<CachedAudioResult> GetOrDownloadAsync(string sourceUrl, CancellationToken cancellationToken)
    {
        if (!IsAvailable)
        {
            throw new InvalidOperationException("yt-dlp was not found in PATH or the bundled tools directory.");
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

        var metadataResult = await GetMetadataAsync(normalizedUrl, cancellationToken);
        if (!metadataResult.Success && IsLikelyYouTubeUrl(normalizedUrl) && HasSupportedJavaScriptRuntime)
        {
            var updateResult = await TryUpdateYtDlpAsync(cancellationToken);
            metadataResult = await GetMetadataAsync(normalizedUrl, cancellationToken);
            if (!metadataResult.Success)
            {
                throw new InvalidOperationException(BuildFailureMessage(
                    normalizedUrl,
                    metadataResult.StandardError,
                    metadataResult.StandardOutput,
                    updateResult));
            }
        }
        else if (!metadataResult.Success)
        {
            throw new InvalidOperationException(BuildFailureMessage(
                normalizedUrl,
                metadataResult.StandardError,
                metadataResult.StandardOutput,
                updateResult: null));
        }

        if (!string.IsNullOrWhiteSpace(metadataResult.Metadata.Id))
        {
            CleanupDownloadedFiles(entryDirectory, metadataResult.Metadata.Id);
        }

        var firstAttempt = await RunDownloadAsync(normalizedUrl, entryDirectory, cancellationToken);
        cachedFilePath = FindDownloadedFile(entryDirectory);
        if (firstAttempt.ExitCode != 0 || cachedFilePath is null)
        {
            CleanupDownloadedFiles(entryDirectory);

            if (IsLikelyYouTubeUrl(normalizedUrl) && HasSupportedJavaScriptRuntime)
            {
                var updateResult = await TryUpdateYtDlpAsync(cancellationToken);
                var retryAttempt = await RunDownloadAsync(normalizedUrl, entryDirectory, cancellationToken);
                cachedFilePath = FindDownloadedFile(entryDirectory);
                if (retryAttempt.ExitCode == 0 && cachedFilePath is not null)
                {
                    var retryTitle = metadataResult.Metadata.Title ?? normalizedUrl;
                    WriteMetadata(metadataPath, new CachedAudioEntry(normalizedUrl, retryTitle));

                    return new CachedAudioResult(cachedFilePath, retryTitle, normalizedUrl, false);
                }

                CleanupDownloadedFiles(entryDirectory);
                throw new InvalidOperationException(BuildFailureMessage(
                    normalizedUrl,
                    retryAttempt.StandardError,
                    retryAttempt.StandardOutput,
                    updateResult));
            }

            throw new InvalidOperationException(BuildFailureMessage(
                normalizedUrl,
                firstAttempt.StandardError,
                firstAttempt.StandardOutput,
                updateResult: null));
        }

        var title = metadataResult.Metadata.Title ?? normalizedUrl;
        WriteMetadata(metadataPath, new CachedAudioEntry(normalizedUrl, title));

        return new CachedAudioResult(cachedFilePath, title, normalizedUrl, false);
    }

    private async Task<YtDlpMetadataResult> GetMetadataAsync(string normalizedUrl, CancellationToken cancellationToken)
    {
        var processStartInfo = new ProcessStartInfo
        {
            FileName = _ytDlpPath!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };

        processStartInfo.ArgumentList.Add("--no-playlist");
        processStartInfo.ArgumentList.Add("--dump-single-json");
        AddCommonNetworkArguments(processStartInfo, normalizedUrl);
        processStartInfo.ArgumentList.Add(normalizedUrl);

        var result = await RunYtDlpAsync(processStartInfo, cancellationToken);
        if (result.ExitCode != 0)
        {
            return new YtDlpMetadataResult(false, new YtDlpMetadata(null, null), result.StandardError, result.StandardOutput);
        }

        var metadata = ParseMetadata(result.StandardOutput);
        return new YtDlpMetadataResult(true, metadata, result.StandardError, result.StandardOutput);
    }

    private async Task<YtDlpProcessResult> RunDownloadAsync(
        string normalizedUrl,
        string entryDirectory,
        CancellationToken cancellationToken)
    {
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
        processStartInfo.ArgumentList.Add("bestaudio/best");
        processStartInfo.ArgumentList.Add("--output");
        processStartInfo.ArgumentList.Add(Path.Combine(entryDirectory, $"{DownloadFilePrefix}.%(ext)s"));

        if (!string.IsNullOrWhiteSpace(_ffmpegPath))
        {
            processStartInfo.ArgumentList.Add("--ffmpeg-location");
            processStartInfo.ArgumentList.Add(_ffmpegPath);
            processStartInfo.ArgumentList.Add("--extract-audio");
            processStartInfo.ArgumentList.Add("--audio-format");
            processStartInfo.ArgumentList.Add("mp3");
            processStartInfo.ArgumentList.Add("--audio-quality");
            processStartInfo.ArgumentList.Add("0");
            processStartInfo.ArgumentList.Add("--embed-metadata");
        }

        AddCommonNetworkArguments(processStartInfo, normalizedUrl);
        processStartInfo.ArgumentList.Add(normalizedUrl);

        return await RunYtDlpAsync(processStartInfo, cancellationToken, entryDirectory);
    }

    private static async Task<YtDlpProcessResult> RunYtDlpAsync(
        ProcessStartInfo processStartInfo,
        CancellationToken cancellationToken,
        string? cleanupDirectory = null)
    {
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
            if (cleanupDirectory is not null)
            {
                CleanupDownloadedFiles(cleanupDirectory);
            }

            await DrainOutputAsync(stdErrTask, stdOutTask);
            throw;
        }

        await Task.WhenAll(stdErrTask, stdOutTask);

        return new YtDlpProcessResult(process.ExitCode, stdErrTask.Result, stdOutTask.Result);
    }

    private void AddCommonNetworkArguments(ProcessStartInfo processStartInfo, string normalizedUrl)
    {
        if (_javaScriptRuntime is not null)
        {
            processStartInfo.ArgumentList.Add("--js-runtimes");
            processStartInfo.ArgumentList.Add(_javaScriptRuntime.ToYtDlpArgument());
        }

        if (IsLikelyYouTubeUrl(normalizedUrl))
        {
            processStartInfo.ArgumentList.Add("--extractor-args");
            processStartInfo.ArgumentList.Add("youtube:player_client=default,web_safari");
        }
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

    private static YtDlpMetadata ParseMetadata(string standardOutput)
    {
        try
        {
            using var document = JsonDocument.Parse(standardOutput);
            var root = document.RootElement;
            var id = TryGetStringProperty(root, "id");
            var title = TryGetStringProperty(root, "title");
            return new YtDlpMetadata(id, title);
        }
        catch
        {
            return new YtDlpMetadata(null, null);
        }
    }

    private static string? TryGetStringProperty(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private async Task<YtDlpProcessResult?> TryUpdateYtDlpAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_ytDlpPath))
        {
            return null;
        }

        var processStartInfo = new ProcessStartInfo
        {
            FileName = _ytDlpPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };

        processStartInfo.ArgumentList.Add("-U");

        try
        {
            using var process = Process.Start(processStartInfo);
            if (process is null)
            {
                return null;
            }

            var stdErrTask = process.StandardError.ReadToEndAsync();
            var stdOutTask = process.StandardOutput.ReadToEndAsync();

            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                await DrainOutputAsync(stdErrTask, stdOutTask);
                throw;
            }

            await Task.WhenAll(stdErrTask, stdOutTask);
            return new YtDlpProcessResult(process.ExitCode, stdErrTask.Result, stdOutTask.Result);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new YtDlpProcessResult(-1, ex.Message, string.Empty);
        }
    }

    private string BuildFailureMessage(
        string sourceUrl,
        string standardError,
        string standardOutput,
        YtDlpProcessResult? updateResult)
    {
        if (IsMissingJavaScriptRuntimeFailure(standardError, standardOutput) ||
            (IsLikelyYouTubeUrl(sourceUrl) && !HasSupportedJavaScriptRuntime))
        {
            var details = BuildFailureDetails(standardError, standardOutput);
            return
                $"yt-dlp needs a supported JavaScript runtime for this site. Install {JavaScriptRuntimeResolver.GetSupportedRuntimeDisplayText()}, or add one to PATH / bundled tools.{details}";
        }

        var genericDetails = BuildFailureDetails(standardError, standardOutput);
        var updateDetails = BuildUpdateDetails(updateResult);
        return $"yt-dlp could not fetch this URL.{genericDetails}{updateDetails}";
    }

    private static string BuildUpdateDetails(YtDlpProcessResult? updateResult)
    {
        if (updateResult is null)
        {
            return string.Empty;
        }

        if (updateResult.ExitCode == 0)
        {
            return " yt-dlp was updated and the URL was retried.";
        }

        var details = BuildFailureDetails(updateResult.StandardError, updateResult.StandardOutput);
        return string.IsNullOrWhiteSpace(details)
            ? " yt-dlp update was attempted but failed."
            : $" yt-dlp update was attempted but failed: {details}";
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

    private static bool IsLikelyYouTubeUrl(string sourceUrl)
    {
        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var host = uri.Host;
        return host.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) ||
               host.Contains("youtu.be", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMissingJavaScriptRuntimeFailure(string standardError, string standardOutput)
    {
        var combined = string.Concat(standardError, "\n", standardOutput);
        return combined.Contains("No supported JavaScript runtime could be found", StringComparison.OrdinalIgnoreCase) ||
               combined.Contains("YouTube extraction without a JS runtime has been deprecated", StringComparison.OrdinalIgnoreCase);
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
        => CleanupDownloadedFiles(entryDirectory, id: null);

    private static void CleanupDownloadedFiles(string entryDirectory, string? id)
    {
        try
        {
            if (!Directory.Exists(entryDirectory))
            {
                return;
            }

            var searchPattern = string.IsNullOrWhiteSpace(id)
                ? $"{DownloadFilePrefix}*"
                : $"*{id}*.*";

            foreach (var path in Directory.EnumerateFiles(entryDirectory, searchPattern, SearchOption.TopDirectoryOnly))
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
    private sealed record YtDlpProcessResult(int ExitCode, string StandardError, string StandardOutput);
    private sealed record YtDlpMetadata(string? Id, string? Title);
    private sealed record YtDlpMetadataResult(
        bool Success,
        YtDlpMetadata Metadata,
        string StandardError,
        string StandardOutput);
}

internal sealed record CachedAudioResult(string FilePath, string Title, string SourceUrl, bool WasCached);
