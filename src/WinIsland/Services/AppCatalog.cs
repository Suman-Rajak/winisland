using System.Diagnostics;
using System.Text.RegularExpressions;

namespace WinIsland.Services;

/// <summary>An installed app, as the Start menu lists it.</summary>
/// <param name="Id">Its entry in the shell's Applications folder (an app ID, or a path for older apps).</param>
internal sealed record InstalledApp(string Name, string Id);

/// <summary>
/// Finds and opens installed apps by a spoken name ("chrome" finds Google Chrome). The list comes from
/// the shell's Applications folder, the same list as the Start menu's "All apps", so it covers both
/// Store and classic apps. It's read in the background and refreshed every few minutes.
/// </summary>
internal static partial class AppCatalog
{
    private static readonly TimeSpan RefreshAfter = TimeSpan.FromMinutes(5);

    // What people call some apps vs. what the Start menu does.
    private static readonly Dictionary<string, string> s_aliases = new()
    {
        ["vs code"] = "visual studio code",
        ["vscode"] = "visual studio code",
        ["code"] = "visual studio code",
        ["explorer"] = "file explorer",
        ["files"] = "file explorer",
        ["my files"] = "file explorer",
        ["this pc"] = "file explorer",
        ["store"] = "microsoft store",
        ["edge"] = "microsoft edge",
        ["teams"] = "microsoft teams",
        ["cmd"] = "command prompt",
        ["command line"] = "command prompt",
        ["calc"] = "calculator",
        ["control panel"] = "control panel",
    };

    // Opened in the browser when no app by that name is installed.
    private static readonly Dictionary<string, string> s_websites = new()
    {
        ["youtube"] = "https://www.youtube.com",
        ["gmail"] = "https://mail.google.com",
        ["google"] = "https://www.google.com",
        ["github"] = "https://github.com",
        ["netflix"] = "https://www.netflix.com",
        ["instagram"] = "https://www.instagram.com",
        ["facebook"] = "https://www.facebook.com",
        ["twitter"] = "https://x.com",
        ["linkedin"] = "https://www.linkedin.com",
        ["reddit"] = "https://www.reddit.com",
        ["amazon"] = "https://www.amazon.in",
        ["wikipedia"] = "https://www.wikipedia.org",
        ["chatgpt"] = "https://chatgpt.com",
        ["claude"] = "https://claude.ai",
        ["whatsapp"] = "https://web.whatsapp.com",
        ["spotify"] = "https://open.spotify.com",
    };

    // Start menu entries that aren't really apps.
    private static readonly string[] s_notApps = ["uninstall", "readme", "read me", "release notes", "license", "documentation", "help", "website", "manual"];

    private static Task<List<InstalledApp>>? s_apps;
    private static DateTime s_loadedAt;

    /// <summary>Starts reading the list ahead of time, so the first "open …" is quick.</summary>
    public static void Preload() => _ = GetAppsAsync();

    /// <summary>Opens the installed app best matching <paramref name="spokenName"/>, else a well-known website. Returns what was opened, or null.</summary>
    public static async Task<string?> OpenAsync(string spokenName)
    {
        string target = Normalize(spokenName);
        if (target.Length == 0)
        {
            return null;
        }
        target = s_aliases.GetValueOrDefault(target, target);

        // Ties go to the shorter name: "notepad" is Notepad, not Notepad++.
        var best = (await GetAppsAsync())
            .Select(app => (App: app, Score: Score(Normalize(app.Name), target)))
            .Where(m => m.Score > 0)
            .OrderByDescending(m => m.Score)
            .ThenBy(m => m.App.Name.Length)
            .FirstOrDefault();

        if (best.App is { } app)
        {
            Log.Info($"Opening \"{app.Name}\" ({app.Id}) for \"{spokenName}\"");
            Process.Start(new ProcessStartInfo("explorer.exe", $@"shell:AppsFolder\{app.Id}") { UseShellExecute = true });
            return app.Name;
        }

        string squashed = target.Replace(" ", "");
        if (s_websites.TryGetValue(squashed, out var url))
        {
            Log.Info($"Opening {url} for \"{spokenName}\"");
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return new Uri(url).Host.Replace("www.", "");
        }

        return null;
    }

    /// <summary>Higher is a better match; 0 is no match. Prefers exact names, then names containing every spoken word, shortest first.</summary>
    private static int Score(string name, string target)
    {
        if (name.Length == 0 || s_notApps.Any(name.Contains))
        {
            return 0;
        }
        if (name == target)
        {
            return 1000;
        }

        string squashedName = name.Replace(" ", ""), squashedTarget = target.Replace(" ", "");
        if (squashedName == squashedTarget)
        {
            return 900;
        }

        var nameWords = name.Split(' ');
        var targetWords = target.Split(' ');
        if (targetWords.All(nameWords.Contains))
        {
            return 500 - (nameWords.Length - targetWords.Length) * 10 - name.Length;
        }
        if (squashedTarget.Length >= 4 && squashedName.StartsWith(squashedTarget, StringComparison.Ordinal))
        {
            return 300 - name.Length;
        }
        if (squashedTarget.Length >= 5 && squashedName.Contains(squashedTarget, StringComparison.Ordinal))
        {
            return 200 - name.Length;
        }
        return 0;
    }

    /// <summary>"Google Chrome (2)" → "google chrome 2"; "V.S. Code" → "vs code".</summary>
    private static string Normalize(string text)
    {
        text = text.ToLowerInvariant().Replace(".", "").Replace("'", "");
        return Spaces().Replace(NotWordCharacters().Replace(text, " "), " ").Trim();
    }

    private static Task<List<InstalledApp>> GetAppsAsync()
    {
        if (s_apps is null || (s_apps.IsCompleted && DateTime.UtcNow - s_loadedAt > RefreshAfter))
        {
            s_loadedAt = DateTime.UtcNow;
            s_apps = LoadAsync();
        }
        return s_apps;
    }

    /// <summary>The shell's folder objects want a single-threaded apartment, so read them on a thread of our own.</summary>
    private static Task<List<InstalledApp>> LoadAsync()
    {
        var done = new TaskCompletionSource<List<InstalledApp>>();
        var thread = new Thread(() =>
        {
            var apps = new List<InstalledApp>();
            try
            {
                var watch = Stopwatch.StartNew();
                dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
                dynamic items = shell.NameSpace("shell:AppsFolder").Items();
                int count = items.Count;
                for (int i = 0; i < count; i++)
                {
                    dynamic item = items.Item(i);
                    string name = item.Name, id = item.Path;
                    if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(id))
                    {
                        apps.Add(new InstalledApp(name, id));
                    }
                }
                Log.Info($"Found {apps.Count} apps in {watch.ElapsedMilliseconds} ms");
            }
            catch (Exception ex)
            {
                Log.Error("Listing installed apps failed", ex);
            }
            done.SetResult(apps);
        })
        { IsBackground = true, Name = "WinIsland app list" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex NotWordCharacters();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
