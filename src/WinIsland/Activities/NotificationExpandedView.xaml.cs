using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinIsland.Services;

namespace WinIsland.Activities;

/// <summary>The expanded notification: icon, app, title, the whole message, Open and Clear.</summary>
public sealed partial class NotificationExpandedView : UserControl
{
    public NotificationExpandedView()
    {
        InitializeComponent();
    }

    internal event Action? OpenClicked;

    internal event Action? ClearClicked;

    internal void Show(IslandNotification notification, ImageSource? icon)
    {
        Icon.ImageSource = icon;
        AppText.Text = notification.AppName;
        TitleText.Text = notification.Title;
        BodyText.Text = notification.Body;
    }

    private void OnOpenClick(object sender, RoutedEventArgs e) => OpenClicked?.Invoke();

    private void OnClearClick(object sender, RoutedEventArgs e) => ClearClicked?.Invoke();
}
