using System.IO;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace SimpleMusicPlayer;

public sealed class AppSetupCoordinator
{
    private readonly AppSetupStateStore _stateStore = new();
    private readonly ExternalProcessRunner _processRunner = new();

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

        var arguments = new List<string>
        {
            "-NoProfile",
            "-ExecutionPolicy",
            "Bypass",
            "-File",
            setupScriptPath,
            "-AppDir",
            AppContext.BaseDirectory
        };

        if (redownloadTools)
        {
            arguments.Add("-RedownloadTools");
        }

        var result = await _processRunner.RunAsync(
            "powershell.exe",
            arguments,
            AppContext.BaseDirectory,
            cancellationToken);

        if (result.ExitCode == 0)
        {
            _stateStore.MarkCompleted(AppContext.BaseDirectory);
            return new AppSetupRunResult(true, string.Empty, result.ExitCode, result.StandardOutput, result.StandardError);
        }

        var details = ProcessOutputFormatter.BuildFailureDetails(result.StandardError, result.StandardOutput, maxLines: 4);
        return new AppSetupRunResult(
            false,
            string.IsNullOrWhiteSpace(details) ? "Setup exited with an error." : details,
            result.ExitCode,
            result.StandardOutput,
            result.StandardError);
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

}

public sealed record AppSetupRunResult(
    bool Success,
    string Message,
    int? ExitCode = null,
    string StandardOutput = "",
    string StandardError = "");
