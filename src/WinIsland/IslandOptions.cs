namespace WinIsland;

internal enum IslandState
{
    Idle,
    Compact,
    Expanded,
}

/// <summary>
/// Developer switches, mainly for working on layouts without hovering:
///   --pin=idle|compact|expanded   lock the island in one state
///   --demo-media                  show a fake track instead of the real media session
///   --demo-level                  keep the volume indicator up (at 45%)
///   --demo-battery                keep "Charging" up (at 64%)
/// </summary>
internal sealed record IslandOptions(IslandState? PinnedState, bool DemoMedia, bool DemoLevel, bool DemoBattery)
{
    public static IslandOptions FromCommandLine(string[] args)
    {
        IslandState? pinned = null;
        bool demo = false, demoLevel = false, demoBattery = false;

        foreach (var arg in args.Skip(1))
        {
            if (arg.StartsWith("--pin=", StringComparison.OrdinalIgnoreCase) &&
                Enum.TryParse<IslandState>(arg["--pin=".Length..], ignoreCase: true, out var state))
            {
                pinned = state;
            }
            else if (arg.Equals("--demo-media", StringComparison.OrdinalIgnoreCase))
            {
                demo = true;
            }
            else if (arg.Equals("--demo-level", StringComparison.OrdinalIgnoreCase))
            {
                demoLevel = true;
            }
            else if (arg.Equals("--demo-battery", StringComparison.OrdinalIgnoreCase))
            {
                demoBattery = true;
            }
        }

        return new IslandOptions(pinned, demo, demoLevel, demoBattery);
    }
}
