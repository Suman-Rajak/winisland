using Microsoft.Win32;
using Windows.ApplicationModel;

namespace WinIsland;

/// <summary>
/// "Start with Windows". The plain build uses the per-user Run key; the installed (MSIX) build uses its
/// manifest's startup task, which also shows up in Task Manager's Startup apps. Always read from the
/// system so it reflects reality (you can turn it off in Task Manager too).
/// </summary>
internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = Product.Name;
    private const string TaskId = "GlydeStartup"; // Package.appxmanifest

    /// <summary>
    /// Left in the installed app's data folder by build-package.ps1 when it removes the plain build's Run
    /// entry, so the installed app turns its own startup task on instead. (A packaged app can't see or
    /// change the real Run key itself: Windows gives it a private copy of the registry.)
    /// </summary>
    private static readonly string EnableFlag = Path.Combine(AppPaths.DataFolder, "enable-startup");

    public static async Task<bool> IsEnabledAsync()
    {
        if (AppPaths.IsPackaged)
        {
            var task = await StartupTask.GetAsync(TaskId);
            return task.State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
        }

        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string;
    }

    /// <summary>Returns whether it ended up enabled: Windows refuses if you turned it off in Task Manager.</summary>
    public static async Task<bool> SetEnabledAsync(bool enabled)
    {
        if (AppPaths.IsPackaged)
        {
            var task = await StartupTask.GetAsync(TaskId);
            if (!enabled)
            {
                task.Disable();
                return false;
            }
            return await task.RequestEnableAsync() is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
        }

        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        key.DeleteValue(Product.FormerName, throwOnMissingValue: false); // the entry under the app's old name
        if (enabled)
        {
            key.SetValue(ValueName, $"\"{Environment.ProcessPath}\"");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        return enabled;
    }

    /// <summary>Carries "Start with Windows" over from the plain build, once (see <see cref="EnableFlag"/>).</summary>
    public static async Task MigrateToPackageAsync()
    {
        if (!AppPaths.IsPackaged || !File.Exists(EnableFlag))
        {
            return;
        }

        try
        {
            bool enabled = await SetEnabledAsync(true);
            File.Delete(EnableFlag);
            Log.Info($"Turned on 'Start with Windows' for the installed app, carried over from the plain build (enabled: {enabled})");
        }
        catch (Exception ex)
        {
            Log.Error("Carrying over 'Start with Windows' failed", ex);
        }
    }
}
