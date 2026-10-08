using Windows.ApplicationModel;
using Windows.Storage;

namespace WinIsland;

/// <summary>Where WinIsland keeps its settings, shelf and log, and whether it's the installed (packaged) build.</summary>
internal static class AppPaths
{
    /// <summary>True when running as the installed MSIX package (which notifications need).</summary>
    public static bool IsPackaged { get; } = DetectPackage();

    /// <summary>
    /// %LOCALAPPDATA%\WinIsland for the plain build. The installed build uses the package's own LocalState
    /// folder: Windows quietly redirects a packaged app's writes elsewhere in AppData, so that's the one
    /// place both it and you can rely on. build-package.ps1 copies your settings and shelf across once.
    /// </summary>
    public static string DataFolder { get; } = IsPackaged
        ? ApplicationData.Current.LocalFolder.Path
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinIsland");

    private static bool DetectPackage()
    {
        try
        {
            return Package.Current.Id is not null;
        }
        catch
        {
            return false; // "The process has no package identity"
        }
    }
}
