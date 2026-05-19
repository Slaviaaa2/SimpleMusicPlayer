using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace SimpleMusicPlayer;

internal sealed class FfmpegAudioCache : IDisposable
{
    private readonly Dictionary<string, string> _transcodedPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly string? _ffmpegPath;
    private readonly string _cacheDirectory;
    private bool _disposed;

    public FfmpegAudioCache()
    {
        _ffmpegPath = ResolveToolPath("ffmpeg");
        _cacheDirectory = Path.Combine(AppContext.BaseDirectory, "cache", "transcoded");
    }

    public bool IsAvailable => !string.IsNullOrWhiteSpace(_ffmpegPath);

    public bool RequiresTranscode(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".mkv", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".webm", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".opus", StringComparison.OrdinalIgnoreCase);
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

        Directory.CreateDirectory(_cacheDirectory);

        var outputPath = Path.Combine(_cacheDirectory, $"{BuildSourceCacheKey(sourcePath)}.wav");
        if (File.Exists(outputPath))
        {
            _transcodedPaths[sourcePath] = outputPath;
            return outputPath;
        }

        var processStartInfo = new ProcessStartInfo
        {
            FileName = _ffmpegPath!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };

        processStartInfo.ArgumentList.Add("-y");
        processStartInfo.ArgumentList.Add("-i");
        processStartInfo.ArgumentList.Add(sourcePath);
        processStartInfo.ArgumentList.Add("-vn");
        processStartInfo.ArgumentList.Add("-acodec");
        processStartInfo.ArgumentList.Add("pcm_s16le");
        processStartInfo.ArgumentList.Add("-ar");
        processStartInfo.ArgumentList.Add("48000");
        processStartInfo.ArgumentList.Add("-ac");
        processStartInfo.ArgumentList.Add("2");
        processStartInfo.ArgumentList.Add(outputPath);

        using var process = Process.Start(processStartInfo)
            ?? throw new InvalidOperationException("Failed to start ffmpeg.");

        var stdErrTask = process.StandardError.ReadToEndAsync();
        var stdOutTask = process.StandardOutput.ReadToEndAsync();

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            TryDelete(outputPath);
            await DrainOutputAsync(stdErrTask, stdOutTask);
            throw;
        }

        await Task.WhenAll(stdErrTask, stdOutTask);

        if (process.ExitCode != 0 || !File.Exists(outputPath))
        {
            TryDelete(outputPath);
            var details = BuildFailureDetails(stdErrTask.Result, stdOutTask.Result);
            throw new InvalidOperationException($"ffmpeg could not decode this file.{details}");
        }

        _transcodedPaths[sourcePath] = outputPath;
        return outputPath;
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

    private static string BuildSourceCacheKey(string sourcePath)
    {
        var fileInfo = new FileInfo(sourcePath);
        var fingerprint = $"{sourcePath}|{fileInfo.Length}|{fileInfo.LastWriteTimeUtc.Ticks}";
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    private static string? ResolveToolPath(string toolName)
    {
        var localToolPaths = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg", $"{toolName}.exe"),
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

        var builder = new StringBuilder();
        builder.Append(' ');
        builder.Append(string.Join(" | ", lines));
        return builder.ToString();
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
