using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml.Media.Imaging;
using WinIsland.Interop;

namespace WinIsland.Services;

/// <summary>An app pinned to the taskbar: its shortcut.</summary>
internal sealed record PinnedApp(string Name, string Shortcut);

/// <summary>
/// The apps pinned to the taskbar, for the menu bar: the shortcuts Windows keeps for them, with the
/// icons the shell draws for them.
/// </summary>
internal static partial class PinnedApps
{
    public static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");

    public static List<PinnedApp> Read()
    {
        try
        {
            return Directory.Exists(Folder)
                ? [.. Directory.GetFiles(Folder, "*.lnk")
                    .Select(path => new PinnedApp(NameOf(path), path))
                    .OrderBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase)]
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Reading the taskbar's pinned apps failed", ex);
            return [];
        }
    }

    /// <summary>When the pins last changed, to know when to read them again.</summary>
    public static DateTime ChangedAt() => Directory.Exists(Folder) ? Directory.GetLastWriteTimeUtc(Folder) : default;

    public static void Open(PinnedApp app)
    {
        try
        {
            Process.Start(new ProcessStartInfo(app.Shortcut) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            Log.Error($"Opening {app.Name} failed", ex);
        }
    }

    /// <summary>"File Explorer (5).lnk" → "File Explorer".</summary>
    private static string NameOf(string path) => CopyNumber().Replace(Path.GetFileNameWithoutExtension(path), string.Empty);

    [GeneratedRegex(@"\s*\(\d+\)$")]
    private static partial Regex CopyNumber();

    /// <summary>The shell's icon for a shortcut, <paramref name="size"/> pixels square. Call on the UI thread.</summary>
    public static WriteableBitmap? Icon(string path, int size) => ShellIcons.Bitmap(path, size);
}
