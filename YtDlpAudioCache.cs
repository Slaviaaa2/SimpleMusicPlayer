using System.Collections.Concurrent;
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
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _downloadLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _updateLock = new(1, 1);
    private readonly ExternalProcessRunner _processRunner = new();
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

    public bool IsYouTubePlaylistPageUrl(string source)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) || !IsLikelyYouTubeUrl(source))
        {
            return false;
        }

        if (!TryGetQueryValue(uri.Query, "list", out var playlistId) ||
            string.IsNullOrWhiteSpace(playlistId))
        {
            return false;
        }

        return uri.AbsolutePath.Equals("/playlist", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<YtDlpPlaylistResult> GetPlaylistAsync(string sourceUrl, CancellationToken cancellationToken)
    {
        if (!IsAvailable)
        {
            throw new InvalidOperationException("yt-dlp was not found in PATH or the bundled tools directory.");
        }

        var normalizedUrl = NormalizeUrl(sourceUrl);
        var playlistResult = await GetPlaylistMetadataAsync(normalizedUrl, cancellationToken);
        if (!playlistResult.Success && IsLikelyYouTubeUrl(normalizedUrl) && HasSupportedJavaScriptRuntime)
        {
            var updateResult = await TryUpdateYtDlpAsync(cancellationToken);
            playlistResult = await GetPlaylistMetadataAsync(normalizedUrl, cancellationToken);
            if (!playlistResult.Success)
            {
                throw new InvalidOperationException(BuildFailureMessage(
                    normalizedUrl,
                    playlistResult.StandardError,
                    playlistResult.StandardOutput,
                    updateResult));
            }
        }
        else if (!playlistResult.Success)
        {
            throw new InvalidOperationException(BuildFailureMessage(
                normalizedUrl,
                playlistResult.StandardError,
                playlistResult.StandardOutput,
                updateResult: null));
        }

        if (playlistResult.Playlist.Entries.Count == 0)
        {
            throw new InvalidOperationException("yt-dlp found this playlist, but it did not contain playable entries.");
        }

        return playlistResult.Playlist;
    }

    public async Task<CachedAudioResult> GetOrDownloadAsync(string sourceUrl, CancellationToken cancellationToken)
    {
        if (!IsAvailable)
        {
            throw new InvalidOperationException("yt-dlp was not found in PATH or the bundled tools directory.");
        }

        var normalizedUrl = NormalizeUrl(sourceUrl);
        var cacheKey = BuildUrlCacheKey(normalizedUrl);
        var entryDirectory = Path.Combine(_cacheDirectory, cacheKey);
        var lockSlim = _downloadLocks.GetOrAdd(cacheKey, static _ => new SemaphoreSlim(1, 1));
        await lockSlim.WaitAsync(cancellationToken);

        try
        {
            Directory.CreateDirectory(entryDirectory);
            CleanupStaleWorkDirectories(entryDirectory);

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

            var title = metadataResult.Metadata.Title ?? normalizedUrl;
            var firstAttempt = await DownloadToCacheAsync(normalizedUrl, entryDirectory, cancellationToken);
            if (firstAttempt.FilePath is not null)
            {
                WriteMetadata(metadataPath, new CachedAudioEntry(normalizedUrl, title));
                return new CachedAudioResult(firstAttempt.FilePath, title, normalizedUrl, false);
            }

            CleanupDownloadedFiles(entryDirectory);

            if (IsLikelyYouTubeUrl(normalizedUrl) && HasSupportedJavaScriptRuntime)
            {
                var updateResult = await TryUpdateYtDlpAsync(cancellationToken);
                var retryAttempt = await DownloadToCacheAsync(normalizedUrl, entryDirectory, cancellationToken);
                if (retryAttempt.FilePath is not null)
                {
                    WriteMetadata(metadataPath, new CachedAudioEntry(normalizedUrl, title));
                    return new CachedAudioResult(retryAttempt.FilePath, title, normalizedUrl, false);
                }

                CleanupDownloadedFiles(entryDirectory);
                throw new InvalidOperationException(BuildFailureMessage(
                    normalizedUrl,
                    retryAttempt.Result.StandardError,
                    retryAttempt.Result.StandardOutput,
                    updateResult));
            }

            throw new InvalidOperationException(BuildFailureMessage(
                normalizedUrl,
                firstAttempt.Result.StandardError,
                firstAttempt.Result.StandardOutput,
                updateResult: null));
        }
        finally
        {
            lockSlim.Release();
        }
    }

    private async Task<YtDlpMetadataResult> GetMetadataAsync(string normalizedUrl, CancellationToken cancellationToken)
    {
        var arguments = new List<string>
        {
            "--no-playlist",
            "--dump-single-json"
        };
        AddCommonNetworkArguments(arguments, normalizedUrl);
        arguments.Add(normalizedUrl);

        var result = await RunYtDlpAsync(arguments, cancellationToken);
        if (result.ExitCode != 0)
        {
            return new YtDlpMetadataResult(false, new YtDlpMetadata(null, null), result.StandardError, result.StandardOutput);
        }

        var metadata = ParseMetadata(result.StandardOutput);
        return new YtDlpMetadataResult(true, metadata, result.StandardError, result.StandardOutput);
    }

    private async Task<YtDlpPlaylistMetadataResult> GetPlaylistMetadataAsync(string normalizedUrl, CancellationToken cancellationToken)
    {
        var arguments = new List<string>
        {
            "--flat-playlist",
            "--dump-single-json"
        };
        AddCommonNetworkArguments(arguments, normalizedUrl);
        arguments.Add(normalizedUrl);

        var result = await RunYtDlpAsync(arguments, cancellationToken);
        if (result.ExitCode != 0)
        {
            return new YtDlpPlaylistMetadataResult(
                false,
                new YtDlpPlaylistResult(normalizedUrl, normalizedUrl, []),
                result.StandardError,
                result.StandardOutput);
        }

        var playlist = ParsePlaylistMetadata(normalizedUrl, result.StandardOutput);
        return new YtDlpPlaylistMetadataResult(true, playlist, result.StandardError, result.StandardOutput);
    }

    private async Task<DownloadAttempt> DownloadToCacheAsync(
        string normalizedUrl,
        string entryDirectory,
        CancellationToken cancellationToken)
    {
        var workDirectory = CreateWorkDirectory(entryDirectory);

        try
        {
            var result = await RunDownloadAsync(normalizedUrl, workDirectory, cancellationToken);
            if (result.ExitCode != 0)
            {
                return new DownloadAttempt(result, null);
            }

            var downloadedFile = FindDownloadedFile(workDirectory);
            if (downloadedFile is null)
            {
                return new DownloadAttempt(result, null);
            }

            return new DownloadAttempt(result, CommitDownloadedFile(downloadedFile, entryDirectory));
        }
        finally
        {
            TryDeleteDirectory(workDirectory);
        }
    }

    private async Task<YtDlpProcessResult> RunDownloadAsync(
        string normalizedUrl,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string>
        {
            "--no-playlist",
            "--no-progress",
            "--format",
            "bestaudio/best",
            "--output",
            Path.Combine(outputDirectory, $"{DownloadFilePrefix}.%(ext)s")
        };

        if (!string.IsNullOrWhiteSpace(_ffmpegPath))
        {
            arguments.Add("--ffmpeg-location");
            arguments.Add(_ffmpegPath);
            arguments.Add("--extract-audio");
            arguments.Add("--audio-format");
            arguments.Add("mp3");
            arguments.Add("--audio-quality");
            arguments.Add("0");
            arguments.Add("--embed-metadata");
        }

        AddCommonNetworkArguments(arguments, normalizedUrl);
        arguments.Add(normalizedUrl);

        return await RunYtDlpAsync(arguments, cancellationToken);
    }

    private async Task<YtDlpProcessResult> RunYtDlpAsync(
        IEnumerable<string> arguments,
        CancellationToken cancellationToken)
    {
        var result = await _processRunner.RunAsync(_ytDlpPath!, arguments, workingDirectory: null, cancellationToken);
        return new YtDlpProcessResult(result.ExitCode, result.StandardError, result.StandardOutput);
    }

    private void AddCommonNetworkArguments(ICollection<string> arguments, string normalizedUrl)
    {
        if (_javaScriptRuntime is not null)
        {
            arguments.Add("--js-runtimes");
            arguments.Add(_javaScriptRuntime.ToYtDlpArgument());
        }

        if (IsLikelyYouTubeUrl(normalizedUrl))
        {
            arguments.Add("--extractor-args");
            arguments.Add("youtube:player_client=default,web_safari");
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
            .FirstOrDefault(path =>
                !path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) &&
                !path.EndsWith(".part", StringComparison.OrdinalIgnoreCase) &&
                IsUsableFile(path));

    private static string CreateWorkDirectory(string entryDirectory)
    {
        var workDirectory = Path.Combine(entryDirectory, $"work-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDirectory);
        return workDirectory;
    }

    private static string CommitDownloadedFile(string downloadedFilePath, string entryDirectory)
    {
        var extension = Path.GetExtension(downloadedFilePath);
        var targetPath = Path.Combine(entryDirectory, $"{DownloadFilePrefix}{extension}");
        TryDelete(targetPath);
        File.Move(downloadedFilePath, targetPath);
        return targetPath;
    }

    private static bool IsUsableFile(string path)
        => File.Exists(path) && new FileInfo(path).Length > 0;

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

    private static YtDlpPlaylistResult ParsePlaylistMetadata(string sourceUrl, string standardOutput)
    {
        try
        {
            using var document = JsonDocument.Parse(standardOutput);
            var root = document.RootElement;
            var title = TryGetStringProperty(root, "title") ?? sourceUrl;
            var entries = new List<YtDlpPlaylistEntry>();

            if (root.TryGetProperty("entries", out var entriesElement) &&
                entriesElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var entryElement in entriesElement.EnumerateArray())
                {
                    var entryTitle = TryGetStringProperty(entryElement, "title");
                    var entryUrl = BuildPlaylistEntryUrl(entryElement);
                    if (!string.IsNullOrWhiteSpace(entryUrl))
                    {
                        entries.Add(new YtDlpPlaylistEntry(entryUrl, entryTitle));
                    }
                }
            }

            return new YtDlpPlaylistResult(sourceUrl, title, entries);
        }
        catch
        {
            return new YtDlpPlaylistResult(sourceUrl, sourceUrl, []);
        }
    }

    private static string? BuildPlaylistEntryUrl(JsonElement entryElement)
    {
        var webpageUrl = TryGetStringProperty(entryElement, "webpage_url");
        if (!string.IsNullOrWhiteSpace(webpageUrl))
        {
            return webpageUrl;
        }

        var originalUrl = TryGetStringProperty(entryElement, "original_url");
        if (!string.IsNullOrWhiteSpace(originalUrl))
        {
            return originalUrl;
        }

        var url = TryGetStringProperty(entryElement, "url");
        if (!string.IsNullOrWhiteSpace(url))
        {
            return Uri.TryCreate(url, UriKind.Absolute, out _)
                ? url
                : $"https://www.youtube.com/watch?v={url}";
        }

        var id = TryGetStringProperty(entryElement, "id");
        return string.IsNullOrWhiteSpace(id)
            ? null
            : $"https://www.youtube.com/watch?v={id}";
    }

    private static string? TryGetStringProperty(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static bool TryGetQueryValue(string query, string key, out string? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(query))
        {
            return false;
        }

        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            var name = Uri.UnescapeDataString(parts[0].Replace("+", " "));
            if (!string.Equals(name, key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            value = parts.Length == 2
                ? Uri.UnescapeDataString(parts[1].Replace("+", " "))
                : string.Empty;
            return true;
        }

        return false;
    }

    private async Task<YtDlpProcessResult?> TryUpdateYtDlpAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_ytDlpPath))
        {
            return null;
        }

        await _updateLock.WaitAsync(cancellationToken);

        try
        {
            var result = await _processRunner.RunAsync(_ytDlpPath, ["-U"], workingDirectory: null, cancellationToken);
            return new YtDlpProcessResult(result.ExitCode, result.StandardError, result.StandardOutput);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new YtDlpProcessResult(-1, ex.Message, string.Empty);
        }
        finally
        {
            _updateLock.Release();
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
        => ProcessOutputFormatter.BuildFailureDetails(standardError, standardOutput);

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

    private static void CleanupStaleWorkDirectories(string entryDirectory)
    {
        try
        {
            foreach (var path in Directory.EnumerateDirectories(entryDirectory, "work-*", SearchOption.TopDirectoryOnly))
            {
                TryDeleteDirectory(path);
            }
        }
        catch
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }

    private sealed record CachedAudioEntry(string Url, string Title);
    private sealed record DownloadAttempt(YtDlpProcessResult Result, string? FilePath);
    private sealed record YtDlpProcessResult(int ExitCode, string StandardError, string StandardOutput);
    private sealed record YtDlpMetadata(string? Id, string? Title);
    private sealed record YtDlpMetadataResult(
        bool Success,
        YtDlpMetadata Metadata,
        string StandardError,
        string StandardOutput);
    private sealed record YtDlpPlaylistMetadataResult(
        bool Success,
        YtDlpPlaylistResult Playlist,
        string StandardError,
        string StandardOutput);
}

internal sealed record CachedAudioResult(string FilePath, string Title, string SourceUrl, bool WasCached);
internal sealed record YtDlpPlaylistEntry(string Url, string? Title);
internal sealed record YtDlpPlaylistResult(string Url, string Title, IReadOnlyList<YtDlpPlaylistEntry> Entries);
