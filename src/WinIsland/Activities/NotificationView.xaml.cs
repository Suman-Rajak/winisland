using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinIsland.Services;

namespace WinIsland.Activities;

/// <summary>The compact banner: icon, sender/title, app name, and the first line of the message.</summary>
public sealed partial class NotificationView : UserControl
{
    public NotificationView()
    {
        InitializeComponent();
    }

    internal void Show(IslandNotification notification, ImageSource? icon)
    {
        Icon.ImageSource = icon;
        TitleText.Text = notification.Title;
        AppText.Text = notification.AppName;
        BodyText.Text = notification.Body;
        BodyText.Visibility = notification.Body.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
