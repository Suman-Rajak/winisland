using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using WinIsland.Services;

namespace WinIsland.Activities;

public sealed partial class PrivacyView : UserControl
{
    // The same colours as iPhone's privacy dots.
    internal static readonly Color MicrophoneColor = Color.FromArgb(255, 255, 159, 10);
    internal static readonly Color CameraColor = Color.FromArgb(255, 48, 209, 88);

    public PrivacyView()
    {
        InitializeComponent();
        MicIcon.Foreground = new SolidColorBrush(MicrophoneColor);
        CameraIcon.Foreground = new SolidColorBrush(CameraColor);
    }

    /// <summary>Who's using what, e.g. "Discord · Microphone" or "Zoom, Chrome · Mic &amp; camera".</summary>
    internal void Show(IReadOnlyCollection<CapabilityUse> uses)
    {
        bool mic = uses.Any(u => u.Capability == Capability.Microphone);
        bool camera = uses.Any(u => u.Capability == Capability.Camera);

        MicIcon.Visibility = mic ? Visibility.Visible : Visibility.Collapsed;
        CameraIcon.Visibility = camera ? Visibility.Visible : Visibility.Collapsed;
        AppText.Text = string.Join(", ", uses.Select(u => u.App).Distinct());
        WhatText.Text = mic && camera ? "Mic & camera" : camera ? "Camera" : "Microphone";
        WhatText.Foreground = new SolidColorBrush(camera ? CameraColor : MicrophoneColor);
    }
}
