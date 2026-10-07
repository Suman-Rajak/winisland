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
///   --demo-bluetooth              keep a connected pair of earbuds up (at 80%)
///   --demo-privacy                pretend Discord just started using the microphone
///   --demo-clipboard              add a few made-up clipboard entries
///   --demo-shelf                  put a few of the app's own files on the shelf (not saved)
///   --tab=N                       with --pin=expanded, open tab N (0 = first)
/// </summary>
internal sealed record IslandOptions(IslandState? PinnedState, bool DemoMedia, bool DemoLevel, bool DemoBattery, bool DemoBluetooth,
    bool DemoPrivacy, bool DemoClipboard, bool DemoShelf, int Tab)
{
    public static IslandOptions FromCommandLine(string[] args)
    {
        IslandState? pinned = null;
        bool demo = false, demoLevel = false, demoBattery = false, demoBluetooth = false, demoPrivacy = false, demoClipboard = false,
            demoShelf = false;
        int tab = 0;

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
            else if (arg.Equals("--demo-bluetooth", StringComparison.OrdinalIgnoreCase))
            {
                demoBluetooth = true;
            }
            else if (arg.Equals("--demo-privacy", StringComparison.OrdinalIgnoreCase))
            {
                demoPrivacy = true;
            }
            else if (arg.Equals("--demo-clipboard", StringComparison.OrdinalIgnoreCase))
            {
                demoClipboard = true;
            }
            else if (arg.Equals("--demo-shelf", StringComparison.OrdinalIgnoreCase))
            {
                demoShelf = true;
            }
            else if (arg.StartsWith("--tab=", StringComparison.OrdinalIgnoreCase) && int.TryParse(arg["--tab=".Length..], out int n))
            {
                tab = n;
            }
        }

        return new IslandOptions(pinned, demo, demoLevel, demoBattery, demoBluetooth, demoPrivacy, demoClipboard, demoShelf, tab);
    }
}
