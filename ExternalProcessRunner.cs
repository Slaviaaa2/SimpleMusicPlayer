using System.Diagnostics;

namespace SimpleMusicPlayer;

internal sealed class ExternalProcessRunner
{
    public async Task<ExternalProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        string? workingDirectory,
        CancellationToken cancellationToken)
    {
        var processStartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };

        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            processStartInfo.WorkingDirectory = workingDirectory;
        }

        foreach (var argument in arguments)
        {
            processStartInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(processStartInfo)
            ?? throw new InvalidOperationException($"Failed to start {Path.GetFileName(fileName)}.");

        var standardErrorTask = process.StandardError.ReadToEndAsync();
        var standardOutputTask = process.StandardOutput.ReadToEndAsync();

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            await DrainOutputAsync(standardErrorTask, standardOutputTask);
            throw;
        }

        await Task.WhenAll(standardErrorTask, standardOutputTask);
        return new ExternalProcessResult(process.ExitCode, standardOutputTask.Result, standardErrorTask.Result);
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
}

internal sealed record ExternalProcessResult(int ExitCode, string StandardOutput, string StandardError);
