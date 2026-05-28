namespace SimpleMusicPlayer;

internal static class ProcessOutputFormatter
{
    public static string BuildFailureDetails(string standardError, string standardOutput, int maxLines = 3)
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
            .TakeLast(maxLines);

        return $" {string.Join(" | ", lines)}";
    }
}
