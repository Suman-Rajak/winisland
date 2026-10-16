using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinIsland.Services;

namespace WinIsland.Activities;

/// <summary>The expanded notification: icon, app, title, the whole message, Open and Clear.</summary>
public sealed partial class NotificationExpandedView : UserControl, IEntrance
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

    /// <summary>The icon pops in; the title, the message and the buttons follow it in.</summary>
    public void Enter()
    {
        Motion.Pop(IconHost, delay: 30, from: 0.5f);
        Motion.Enter(Header, new Vector2(12, 0), delay: 70);
        Motion.Enter(BodyText, new Vector2(0, 8), delay: 120);
        Motion.Cascade(Actions.Children, new Vector2(0, 8), delay: 170, step: 45);
    }

    private void OnOpenClick(object sender, RoutedEventArgs e) => OpenClicked?.Invoke();

    private void OnClearClick(object sender, RoutedEventArgs e) => ClearClicked?.Invoke();
}
