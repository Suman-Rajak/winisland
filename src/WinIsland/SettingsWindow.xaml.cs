using System.Reflection;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using WinIsland.Activities;
using WinIsland.Interop;
using WinIsland.Services;
using WinRT.Interop;

namespace WinIsland;

/// <summary>Settings apply as soon as you change them; there's no Save button.</summary>
public sealed partial class SettingsWindow : Window
{
    private const int WidthDip = 560;
    private const int HeightDip = 720;

    private readonly AppSettings _settings = AppSettings.Current;
    private bool _loading;

    public SettingsWindow()
    {
        InitializeComponent();

        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredTheme = TitleBarTheme.UseDefaultAppMode;

        string icon = Path.Combine(AppContext.BaseDirectory, "Assets", "island.ico");
        AppWindow.SetIcon(icon);
        TitleBarIcon.Source = new BitmapImage(new Uri(icon));

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMaximizable = false;
        }

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = $"WinIsland {version?.ToString(3)}";

        SizeAndCentre();
        LoadValues();
    }

    private void SizeAndCentre()
    {
        double scale = Native.GetDpiForWindow(WindowNative.GetWindowHandle(this)) / 96.0;
        int width = (int)(WidthDip * scale), height = (int)(HeightDip * scale);
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        height = Math.Min(height, work.Height - 40);
        AppWindow.MoveAndResize(new RectInt32(
            work.X + (work.Width - width) / 2,
            work.Y + (work.Height - height) / 2,
            width,
            height));
    }

    private void LoadValues()
    {
        _ = LoadStartupAsync();

        _loading = true;
        try
        {
            DisplayCombo.Items.Clear();
            DisplayCombo.Items.Add(new ComboBoxItem { Content = "Main display", Tag = null });
            var displays = Displays.All();
            for (int i = 0; i < displays.Count; i++)
            {
                var d = displays[i];
                string label = $"Display {i + 1}  ·  {d.Bounds.Width} × {d.Bounds.Height}{(d.IsPrimary ? "  (main)" : "")}";
                DisplayCombo.Items.Add(new ComboBoxItem { Content = label, Tag = d.DeviceName });
            }
            DisplayCombo.SelectedItem = DisplayCombo.Items.Cast<ComboBoxItem>()
                .FirstOrDefault(item => (string?)item.Tag == _settings.Display) ?? DisplayCombo.Items[0];

            DistanceSlider.Value = _settings.DistanceFromTop;
            DistanceLabel.Text = $"{_settings.DistanceFromTop:0} px";
            HideIdleToggle.IsOn = _settings.HideWhenIdle;

            SpeedCombo.SelectedItem = SpeedCombo.Items.Cast<ComboBoxItem>()
                .First(item => (string)item.Tag == _settings.AnimationSpeed.ToString());
            TransparencySlider.Value = Math.Round(_settings.GlassTransparency * 100);
            TransparencyLabel.Text = $"{_settings.GlassTransparency * 100:0}%";
            MusicColorsToggle.IsOn = _settings.MatchMusicColors;

            LevelsToggle.IsOn = _settings.ShowLevelIndicators;
            BatteryToggle.IsOn = _settings.ShowBattery;
            BluetoothToggle.IsOn = _settings.ShowBluetooth;
            PrivacyToggle.IsOn = _settings.ShowPrivacy;
            ClipboardToggle.IsOn = _settings.ClipboardHistory;
            ShelfToggle.IsOn = _settings.FileShelf;
            NotificationsToggle.IsOn = _settings.ShowNotifications;
            NotificationsDescription.Text = AppPaths.IsPackaged
                ? "Show other apps' notifications on the island; hover one to read it all. Quiet while Do Not Disturb is on."
                : "Needs the installed version of WinIsland: this copy isn't installed as a package, so Windows won't share notifications with it.";
            ArtworkToggle.IsOn = _settings.FindArtworkOnline;

            AssistantHeader.Text = Assistant.Name;
            AssistantTitle.Text = $"{Assistant.Name} voice assistant";
            AssistantToggle.IsOn = _settings.VoiceAssistant;
            AssistantDescription.Text =
                "Press the shortcut below (or the mic on the expanded island) and speak: “pause”, “next song”, " +
                "“volume 30”, “open Chrome”. Your voice is turned into text on this PC and never sent anywhere." +
                (SpeechToText.IsModelDownloaded ? "" : " The first time, it downloads its speech model (465 MB).");
            ShortcutCombo.ItemsSource = HotkeyListener.Choices;
            ShortcutCombo.SelectedItem = HotkeyListener.Choices.Contains(_settings.AssistantShortcut)
                ? _settings.AssistantShortcut
                : HotkeyListener.Choices[0];
            UpdateShortcutDescription();
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>Applies a change unless we're the ones filling in the controls.</summary>
    private void Update(Action<AppSettings> change)
    {
        if (_loading)
        {
            return;
        }

        change(_settings);
        _settings.Save();
    }

    private async Task LoadStartupAsync()
    {
        bool enabled = await StartupRegistration.IsEnabledAsync();
        _loading = true;
        StartupToggle.IsOn = enabled;
        _loading = false;
    }

    private async void OnStartupToggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        bool wanted = StartupToggle.IsOn;
        bool result;
        try
        {
            result = await StartupRegistration.SetEnabledAsync(wanted);
            StartupDescription.Text = result == wanted
                ? "Open WinIsland when you sign in."
                : "Windows has this turned off. Turn it back on in Task Manager → Startup apps.";
        }
        catch (Exception ex)
        {
            Log.Error("Changing startup registration failed", ex);
            StartupDescription.Text = "Couldn't change this setting. See the log for details.";
            result = await StartupRegistration.IsEnabledAsync();
        }

        if (result != wanted)
        {
            _loading = true;
            StartupToggle.IsOn = result;
            _loading = false;
        }
    }

    private void OnDisplayChanged(object sender, SelectionChangedEventArgs e) =>
        Update(s => s.Display = (DisplayCombo.SelectedItem as ComboBoxItem)?.Tag as string);

    private void OnDistanceChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        DistanceLabel.Text = $"{e.NewValue:0} px";
        Update(s => s.DistanceFromTop = e.NewValue);
    }

    private void OnHideIdleToggled(object sender, RoutedEventArgs e) =>
        Update(s => s.HideWhenIdle = HideIdleToggle.IsOn);

    private void OnSpeedChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SpeedCombo.SelectedItem is ComboBoxItem { Tag: string tag } && Enum.TryParse<AnimationSpeed>(tag, out var speed))
        {
            Update(s => s.AnimationSpeed = speed);
        }
    }

    private void OnTransparencyChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        TransparencyLabel.Text = $"{e.NewValue:0}%";
        Update(s => s.GlassTransparency = e.NewValue / 100);
    }

    private void OnMusicColorsToggled(object sender, RoutedEventArgs e) =>
        Update(s => s.MatchMusicColors = MusicColorsToggle.IsOn);

    private void OnLevelsToggled(object sender, RoutedEventArgs e) =>
        Update(s => s.ShowLevelIndicators = LevelsToggle.IsOn);

    private void OnBatteryToggled(object sender, RoutedEventArgs e) =>
        Update(s => s.ShowBattery = BatteryToggle.IsOn);

    private void OnBluetoothToggled(object sender, RoutedEventArgs e) =>
        Update(s => s.ShowBluetooth = BluetoothToggle.IsOn);

    private void OnPrivacyToggled(object sender, RoutedEventArgs e) =>
        Update(s => s.ShowPrivacy = PrivacyToggle.IsOn);

    private void OnClipboardToggled(object sender, RoutedEventArgs e) =>
        Update(s => s.ClipboardHistory = ClipboardToggle.IsOn);

    private void OnShelfToggled(object sender, RoutedEventArgs e) =>
        Update(s => s.FileShelf = ShelfToggle.IsOn);

    private void OnNotificationsToggled(object sender, RoutedEventArgs e) =>
        Update(s => s.ShowNotifications = NotificationsToggle.IsOn);

    private void OnAssistantToggled(object sender, RoutedEventArgs e)
    {
        Update(s => s.VoiceAssistant = AssistantToggle.IsOn);
        UpdateShortcutDescription();
    }

    private void OnShortcutChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ShortcutCombo.SelectedItem is string shortcut)
        {
            Update(s => s.AssistantShortcut = shortcut);
            UpdateShortcutDescription();
        }
    }

    /// <summary>The island registers the shortcut as soon as the setting changes, so whether it got it is known right away.</summary>
    private void UpdateShortcutDescription()
    {
        ShortcutCombo.IsEnabled = _settings.VoiceAssistant;
        ShortcutDescription.Text = !_settings.VoiceAssistant || AssistantActivity.ShortcutWorks
            ? "Works in any app. Press it again to stop early."
            : $"Another app already uses {_settings.AssistantShortcut}, so it does nothing here. Pick another.";
        ShortcutDescription.Foreground = !_settings.VoiceAssistant || AssistantActivity.ShortcutWorks
            ? (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            : (Brush)Application.Current.Resources["SystemFillColorCautionBrush"];
    }

    private void OnArtworkToggled(object sender, RoutedEventArgs e) =>
        Update(s => s.FindArtworkOnline = ArtworkToggle.IsOn);

    private void OnResetClick(object sender, RoutedEventArgs e)
    {
        _settings.ResetToDefaults();
        LoadValues();
    }
}
