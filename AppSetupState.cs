namespace SimpleMusicPlayer;

public sealed class AppSetupState
{
    public string? CompletedInstallPath { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public string? DismissedInstallPath { get; init; }
    public DateTimeOffset? DismissedAt { get; init; }
}
