using Windows.ApplicationModel;
using Windows.Storage;

namespace WinIsland;

/// <summary>Where the app keeps its settings, shelf and log, and whether it's the installed (packaged) build.</summary>
internal static class AppPaths
{
    /// <summary>True when running as the installed MSIX package (which notifications need).</summary>
    public static bool IsPackaged { get; } = DetectPackage();

    /// <summary>
    /// %LOCALAPPDATA%\Glyde for the plain build (moved from %LOCALAPPDATA%\WinIsland, the app's old name,
    /// the first time). The installed build uses the package's own LocalState folder: Windows quietly
    /// redirects a packaged app's writes elsewhere in AppData, so that's the one place both it and you can
    /// rely on. build-package.ps1 moves the data across once.
    /// </summary>
    public static string DataFolder { get; } = IsPackaged ? ApplicationData.Current.LocalFolder.Path : PlainDataFolder();

    private static string PlainDataFolder()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string folder = Path.Combine(localAppData, Product.Name);
        string former = Path.Combine(localAppData, Product.FormerName);
        try
        {
            if (!Directory.Exists(folder) && Directory.Exists(former))
            {
                Directory.Move(former, folder);
            }
        }
        catch
        {
            // in use, say: start afresh rather than not at all
        }
        return folder;
    }

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
