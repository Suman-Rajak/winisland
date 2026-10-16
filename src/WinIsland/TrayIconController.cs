using System.Windows.Input;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.UI.Xaml.Controls;

namespace WinIsland;

/// <summary>
/// The notification-area icon: click for settings, right-click to hide or quit. It's the only way in
/// to an always-on-top, taskbar-less window.
/// </summary>
internal sealed class TrayIconController : IDisposable
{
    private readonly TaskbarIcon _icon;

    public TrayIconController(IslandWindow island, Action openSettings, Action quit)
    {
        var toggleItem = new MenuFlyoutItem { Text = "Hide island" };
        toggleItem.Command = new RelayCommand(() =>
        {
            island.UserHidden = !island.UserHidden;
            toggleItem.Text = island.UserHidden ? "Show island" : "Hide island";
        });

        var menu = new MenuFlyout();
        menu.Items.Add(new MenuFlyoutItem { Text = "Settings", Command = new RelayCommand(openSettings) });
        menu.Items.Add(toggleItem);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(new MenuFlyoutItem { Text = $"Quit {Product.Name}", Command = new RelayCommand(quit) });

        _icon = new TaskbarIcon
        {
            ToolTipText = Product.Name,
            Icon = new System.Drawing.Icon(Path.Combine(AppContext.BaseDirectory, "Assets", "island.ico")),
            ContextMenuMode = ContextMenuMode.PopupMenu,
            ContextFlyout = menu,
            LeftClickCommand = new RelayCommand(openSettings),
            NoLeftClickDelay = true,
        };

        // Efficiency mode would throttle the process and make the island's animations stutter.
        _icon.ForceCreate(enablesEfficiencyMode: false);
    }

    public void Dispose() => _icon.Dispose();

    private sealed class RelayCommand(Action execute) : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => execute();
    }
}
