using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace SimpleMusicPlayer;

internal sealed class FfmpegAudioCache : IDisposable
{
    private readonly Dictionary<string, string> _transcodedPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _transcodeLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ExternalProcessRunner _processRunner = new();
    private readonly string? _ffmpegPath;
    private readonly string _cacheDirectory;
    private bool _disposed;

    public FfmpegAudioCache()
    {
        _ffmpegPath = ToolPathResolver.ResolveExecutablePath("ffmpeg");
        _cacheDirectory = Path.Combine(AppContext.BaseDirectory, "cache", "transcoded");
    }

    public bool IsAvailable => !string.IsNullOrWhiteSpace(_ffmpegPath);

    public bool RequiresTranscode(string path)
    {
        return MediaFileTypes.RequiresTranscode(path);
    }

    public async Task<string> GetPlaybackPathAsync(string sourcePath, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!RequiresTranscode(sourcePath) || !IsAvailable)
        {
            return sourcePath;
        }

        if (_transcodedPaths.TryGetValue(sourcePath, out var existingPath) && File.Exists(existingPath))
        {
            return existingPath;
        }

        var cacheKey = BuildSourceCacheKey(sourcePath);
        var lockSlim = _transcodeLocks.GetOrAdd(cacheKey, static _ => new SemaphoreSlim(1, 1));
        await lockSlim.WaitAsync(cancellationToken);
        try
        {
            if (_transcodedPaths.TryGetValue(sourcePath, out existingPath) && IsUsableFile(existingPath))
            {
                return existingPath;
            }

            Directory.CreateDirectory(_cacheDirectory);

            var outputPath = Path.Combine(_cacheDirectory, $"{cacheKey}.wav");
            if (IsUsableFile(outputPath))
            {
                _transcodedPaths[sourcePath] = outputPath;
                return outputPath;
            }

            var tempOutputPath = Path.Combine(_cacheDirectory, $"{cacheKey}.{Guid.NewGuid():N}.tmp.wav");
            try
            {
                var result = await _processRunner.RunAsync(
                    _ffmpegPath!,
                    BuildTranscodeArguments(sourcePath, tempOutputPath),
                    workingDirectory: null,
                    cancellationToken);

                if (result.ExitCode != 0 || !IsUsableFile(tempOutputPath))
                {
                    TryDelete(tempOutputPath);
                    var details = ProcessOutputFormatter.BuildFailureDetails(result.StandardError, result.StandardOutput);
                    throw new InvalidOperationException($"ffmpeg could not decode this file.{details}");
                }

                MoveReplacing(tempOutputPath, outputPath);
                _transcodedPaths[sourcePath] = outputPath;
                return outputPath;
            }
            catch (OperationCanceledException)
            {
                TryDelete(tempOutputPath);
                throw;
            }
            catch
            {
                TryDelete(tempOutputPath);
                throw;
            }
        }
        finally
        {
            lockSlim.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _transcodedPaths.Clear();
        _disposed = true;
    }

    private static IEnumerable<string> BuildTranscodeArguments(string sourcePath, string outputPath)
    {
        yield return "-y";
        yield return "-i";
        yield return sourcePath;
        yield return "-vn";
        yield return "-acodec";
        yield return "pcm_s16le";
        yield return "-ar";
        yield return "48000";
        yield return "-ac";
        yield return "2";
        yield return outputPath;
    }

    private static string BuildSourceCacheKey(string sourcePath)
    {
        var fileInfo = new FileInfo(sourcePath);
        var fingerprint = $"{sourcePath}|{fileInfo.Length}|{fileInfo.LastWriteTimeUtc.Ticks}";
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    private static bool IsUsableFile(string path)
        => File.Exists(path) && new FileInfo(path).Length > 0;

    private static void MoveReplacing(string sourcePath, string destinationPath)
    {
        TryDelete(destinationPath);
        File.Move(sourcePath, destinationPath);
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
}
