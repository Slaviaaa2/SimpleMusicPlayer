using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace SimpleMusicPlayer;

public sealed class AppSetupCoordinator
{
    private readonly AppSetupStateStore _stateStore = new();

    public bool ShouldOfferSetup()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var appExePath = GetCurrentExecutablePath();
        if (appExePath is null)
        {
            return false;
        }

        var setupScriptPath = GetSetupScriptPath();
        if (!File.Exists(setupScriptPath))
        {
            return false;
        }

        var installPath = AppSetupStateStore.NormalizePath(AppContext.BaseDirectory);
        var state = _stateStore.Load();

        if (string.Equals(state.CompletedInstallPath, installPath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (LooksConfigured(appExePath, installPath))
        {
            _stateStore.MarkCompleted(installPath);
            return false;
        }

        return !string.Equals(state.DismissedInstallPath, installPath, StringComparison.OrdinalIgnoreCase);
    }

    public void MarkDismissed() => _stateStore.MarkDismissed(AppContext.BaseDirectory);

    public async Task<AppSetupRunResult> RunSetupAsync(bool redownloadTools, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new AppSetupRunResult(false, "First-time shell integration is only available on Windows.");
        }

        var setupScriptPath = GetSetupScriptPath();
        if (!File.Exists(setupScriptPath))
        {
            return new AppSetupRunResult(false, "Install-SimpleMusicPlayer.ps1 was not found next to the app.");
        }

        var processStartInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };

        processStartInfo.ArgumentList.Add("-NoProfile");
        processStartInfo.ArgumentList.Add("-ExecutionPolicy");
        processStartInfo.ArgumentList.Add("Bypass");
        processStartInfo.ArgumentList.Add("-File");
        processStartInfo.ArgumentList.Add(setupScriptPath);
        processStartInfo.ArgumentList.Add("-AppDir");
        processStartInfo.ArgumentList.Add(AppContext.BaseDirectory);

        if (redownloadTools)
        {
            processStartInfo.ArgumentList.Add("-RedownloadTools");
        }

        using var process = Process.Start(processStartInfo);
        if (process is null)
        {
            return new AppSetupRunResult(false, "Could not start PowerShell for setup.");
        }

        var standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var standardOutputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(standardErrorTask, standardOutputTask);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        if (process.ExitCode == 0)
        {
            _stateStore.MarkCompleted(AppContext.BaseDirectory);
            return new AppSetupRunResult(true, string.Empty);
        }

        var details = BuildFailureDetails(standardErrorTask.Result, standardOutputTask.Result);
        return new AppSetupRunResult(false, string.IsNullOrWhiteSpace(details)
            ? "Setup exited with an error."
            : details);
    }

    private static string GetSetupScriptPath()
        => Path.Combine(AppContext.BaseDirectory, "Install-SimpleMusicPlayer.ps1");

    private static string? GetCurrentExecutablePath()
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
        {
            return null;
        }

        return string.Equals(Path.GetFileName(processPath), "SimpleMusicPlayer.exe", StringComparison.OrdinalIgnoreCase)
            ? processPath
            : null;
    }

    [SupportedOSPlatform("windows")]
    private static bool LooksConfigured(string appExePath, string installPath)
        => PathContainsInstallDirectory(installPath) &&
           IsApplicationCommandConfigured(appExePath) &&
           IsFolderCommandConfigured(appExePath);

    private static bool PathContainsInstallDirectory(string installPath)
    {
        var userPath = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User);
        if (string.IsNullOrWhiteSpace(userPath))
        {
            return false;
        }

        var normalizedInstallPath = AppSetupStateStore.NormalizePath(installPath);
        return userPath.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(AppSetupStateStore.NormalizePath)
            .Any(path => string.Equals(path, normalizedInstallPath, StringComparison.OrdinalIgnoreCase));
    }

    [SupportedOSPlatform("windows")]
    private static bool IsApplicationCommandConfigured(string appExePath)
    {
        const string keyPath = @"Software\Classes\Applications\SimpleMusicPlayer.exe\shell\open\command";
        var expectedCommand = $"\"{appExePath}\" \"%1\"";
        return Registry.CurrentUser.OpenSubKey(keyPath)?.GetValue(null)?.ToString() is string currentValue &&
               string.Equals(currentValue, expectedCommand, StringComparison.OrdinalIgnoreCase);
    }

    [SupportedOSPlatform("windows")]
    private static bool IsFolderCommandConfigured(string appExePath)
    {
        const string keyPath = @"Software\Classes\Directory\shell\SimpleMusicPlayer\command";
        var expectedCommand = $"\"{appExePath}\" --album \"%1\"";
        return Registry.CurrentUser.OpenSubKey(keyPath)?.GetValue(null)?.ToString() is string currentValue &&
               string.Equals(currentValue, expectedCommand, StringComparison.OrdinalIgnoreCase);
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
            .TakeLast(4);

        return string.Join(" | ", lines);
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

public sealed record AppSetupRunResult(bool Success, string Message);
