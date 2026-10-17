using Microsoft.UI.Xaml;

namespace WinIsland;

public partial class App : Application
{
    private static Mutex? s_instanceMutex;
    private static EventWaitHandle? s_showSettingsSignal;
    private static EventWaitHandle? s_quitSignal;

    private IslandWindow? _island;
    private TrayIconController? _tray;
    private SettingsWindow? _settings;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => Log.Error("Unhandled exception", e.Exception);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Launching WinIsland again while it's running opens Settings in the running copy.
        s_showSettingsSignal = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{Product.Name}.ShowSettings");
        s_instanceMutex = new Mutex(initiallyOwned: true, $@"Local\{Product.Name}.SingleInstance", out bool isFirstInstance);
        if (!isFirstInstance)
        {
            s_showSettingsSignal.Set();
            Exit();
            return;
        }

        Log.Info($"{Product.Name} {typeof(App).Assembly.GetName().Version?.ToString(3)} starting " +
                 $"({(AppPaths.IsPackaged ? "installed package" : "unpackaged")}, data in {AppPaths.DataFolder})");

        var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        ThreadPool.RegisterWaitForSingleObject(s_showSettingsSignal,
            (_, _) => dispatcher.TryEnqueue(OpenSettings), null, Timeout.Infinite, executeOnlyOnce: false);

        // Reinstalling (build-package.ps1) asks the running copy to quit rather than killing it, so the
        // microphone is closed properly: killing it while it recorded has twice left Windows' audio unable
        // to open any microphone until the Windows Audio service was restarted.
        s_quitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{Product.Name}.Quit");
        ThreadPool.RegisterWaitForSingleObject(s_quitSignal, (_, _) => dispatcher.TryEnqueue(() =>
        {
            Log.Info("Asked to quit (reinstalling)");
            Quit();
        }), null, Timeout.Infinite, executeOnlyOnce: true);

        _island = new IslandWindow(IslandOptions.FromCommandLine(Environment.GetCommandLineArgs()));
        _tray = new TrayIconController(_island, OpenSettings, Quit);
        _island.SettingsRequested += OpenSettings;
        _island.Start();
        _ = StartupRegistration.MigrateToPackageAsync();
    }

    private void OpenSettings()
    {
        if (_settings is null)
        {
            _settings = new SettingsWindow();
            _settings.Closed += (_, _) => _settings = null;
        }

        _settings.Activate();
    }

    private void Quit()
    {
        _tray?.Dispose();
        _settings?.Close();
        _island?.Close();
        Exit();
    }
}
