namespace SimpleMusicPlayer;

internal static class LoopModeExtensions
{
    public static LoopMode Next(this LoopMode mode) => mode switch
    {
        LoopMode.None => LoopMode.All,
        LoopMode.All => LoopMode.One,
        _ => LoopMode.None
    };

    public static string DisplayText(this LoopMode mode) => mode switch
    {
        LoopMode.None => "Loop Off",
        LoopMode.One => "Loop One",
        _ => "Loop All"
    };
}
