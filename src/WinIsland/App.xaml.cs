using Microsoft.UI.Xaml;

namespace WinIsland;

public partial class App : Application
{
    private static Mutex? s_instanceMutex;

    private IslandWindow? _island;
    private TrayIconController? _tray;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => Log.Error("Unhandled exception", e.Exception);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        s_instanceMutex = new Mutex(initiallyOwned: true, @"Local\WinIsland.SingleInstance", out bool isFirstInstance);
        if (!isFirstInstance)
        {
            Exit();
            return;
        }

        _island = new IslandWindow(IslandOptions.FromCommandLine(Environment.GetCommandLineArgs()));
        _tray = new TrayIconController(_island, Quit);
        _island.Start();
    }

    private void Quit()
    {
        _tray?.Dispose();
        _island?.Close();
        Exit();
    }
}
