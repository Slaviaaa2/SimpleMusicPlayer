using System.Collections.ObjectModel;

namespace SimpleMusicPlayer;

public sealed class CliOptions
{
    public Collection<string> Sources { get; } = [];
    public bool Shuffle { get; private set; }
    public int? StartIndex { get; private set; }
    public LoopMode LoopMode { get; private set; } = LoopMode.All;
    public string? DiscordAppId { get; private set; }

    public static CliOptions Parse(string[] args)
    {
        var options = new CliOptions();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                options.Sources.Add(arg);
                continue;
            }

            switch (arg)
            {
                case "--shuffle":
                    options.Shuffle = true;
                    break;
                case "--album":
                    if (TryReadValue(args, ref i, out var albumPath))
                    {
                        options.Sources.Add(albumPath);
                    }
                    break;
                case "--index":
                    if (TryReadValue(args, ref i, out var rawIndex) && int.TryParse(rawIndex, out var parsedIndex))
                    {
                        options.StartIndex = parsedIndex;
                    }
                    break;
                case "--loop":
                    if (TryReadValue(args, ref i, out var loopValue))
                    {
                        options.LoopMode = loopValue.ToLowerInvariant() switch
                        {
                            "none" => LoopMode.None,
                            "one" => LoopMode.One,
                            _ => LoopMode.All
                        };
                    }
                    break;
                case "--discord-app-id":
                    if (TryReadValue(args, ref i, out var discordAppId))
                    {
                        options.DiscordAppId = discordAppId;
                    }
                    break;
            }
        }

        return options;
    }

    private static bool TryReadValue(string[] args, ref int index, out string value)
    {
        var nextIndex = index + 1;
        if (nextIndex >= args.Length)
        {
            value = string.Empty;
            return false;
        }

        index = nextIndex;
        value = args[nextIndex];
        return true;
    }
}
