namespace SimpleMusicPlayer;

internal static class JavaScriptRuntimeResolver
{
    private static readonly JavaScriptRuntimeCandidate[] Candidates =
    [
        new("deno", "deno"),
        new("node", "node"),
        new("bun", "bun"),
        new("quickjs", "qjs")
    ];

    public static JavaScriptRuntimeSelection? ResolveForYtDlp()
    {
        foreach (var candidate in Candidates)
        {
            var executablePath = ToolPathResolver.ResolveExecutablePath(candidate.ExecutableName);
            if (!string.IsNullOrWhiteSpace(executablePath))
            {
                return new JavaScriptRuntimeSelection(candidate.YtDlpRuntimeName, executablePath);
            }
        }

        return null;
    }

    public static string GetSupportedRuntimeDisplayText()
        => "Deno, Node.js, Bun, or QuickJS";

    private sealed record JavaScriptRuntimeCandidate(string YtDlpRuntimeName, string ExecutableName);
}

internal sealed record JavaScriptRuntimeSelection(string RuntimeName, string ExecutablePath)
{
    public string ToYtDlpArgument()
        => $"{RuntimeName}:{ExecutablePath}";
}
