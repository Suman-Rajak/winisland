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
        VersionText.Text = $"{Product.Name} {version?.ToString(3)}";

        SizeAndCentre();
        LoadValues();
        AddMotion();
        ShowFace(version);
    }

    /// <summary>
    /// The header's face: alive while the window's open, its eyes following the pointer round the
    /// window, smiling while you point at it.
    /// </summary>
    private void ShowFace(Version? version)
    {
        HeroCaption.Text = $"Your voice island for Windows  ·  version {version?.ToString(3)}";
        HeroFace.SetAlive(true);
        Closed += (_, _) => HeroFace.SetAlive(false);

        var root = (UIElement)Content;
        root.AddHandler(UIElement.PointerMovedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, e) =>
        {
            var at = e.GetCurrentPoint(HeroFace).Position;
            HeroFace.LookAt(new System.Numerics.Vector2(
                (float)(at.X - HeroFace.ActualWidth / 2) / 220f,
                (float)(at.Y - HeroFace.ActualHeight / 2) / 220f));
        }), handledEventsToo: true);
        root.PointerExited += (_, _) => HeroFace.LookAt(System.Numerics.Vector2.Zero);
        Hero.PointerEntered += (_, _) => HeroFace.SetMood(FaceMood.Happy);
        Hero.PointerExited += (_, _) => HeroFace.SetMood(FaceMood.Calm);
    }

    /// <summary>
    /// The window's motion, besides the cards rising in as it opens (ChildrenTransitions in the XAML):
    /// cards lighten under the pointer, and rows that come and go inside them (a key box, a warning)
    /// fade in and out rather than popping.
    /// </summary>
    private void AddMotion()
    {
        if (Application.Current.Resources.TryGetValue("CardBackgroundFillColorSecondaryBrush", out var found) && found is Brush hover)
        {
            foreach (var card in Cards.Children.OfType<Border>())
            {
                card.BackgroundTransition = new BrushTransition { Duration = TimeSpan.FromMilliseconds(160) };
                card.PointerEntered += (_, _) => card.Background = hover;
                card.PointerExited += (_, _) => card.ClearValue(Border.BackgroundProperty); // (back to the theme's)
            }
        }

        foreach (var row in new UIElement[] { BrainChoiceRow, ClaudeKeyRow, ClaudeLink, GeminiLink, BrainKeyRow, CloudSpeechRow, DeepgramKeyRow, DeepgramLink, DeepgramStatusRow, WakeWordStatus,
                     VoiceKeyRow, AzureLink, VoiceStatusRow, AccentRow })
        {
            Motion.CrossfadeVisibility(row, rise: 4);
        }
    }

    /// <summary>Test is asking (a spinner by the status), or has its answer (which rises in).</summary>
    private static void SetTesting(ProgressRing ring, TextBlock status, bool testing)
    {
        ring.IsActive = testing;
        ring.Visibility = testing ? Visibility.Visible : Visibility.Collapsed;
        if (!testing)
        {
            Motion.Enter(status, new System.Numerics.Vector2(0, 4));
        }
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
            StepAsideToggle.IsOn = _settings.StepAsideForApps;

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
                : $"Needs the installed version of {Product.Name}: this copy isn't installed as a package, so Windows won't share notifications with it.";
            ArtworkToggle.IsOn = _settings.FindArtworkOnline;

            AssistantHeader.Text = Assistant.Name;
            AssistantTitle.Text = $"{Assistant.Name} voice assistant";
            AssistantToggle.IsOn = _settings.VoiceAssistant;
            AssistantDescription.Text =
                "Press the talk shortcut below (or the mic on the expanded island) and speak: “pause”, “next song”, " +
                "“volume 30”, “open Chrome”. Or press the type shortcut and type it. Your voice is turned into text on this PC, " +
                "or by Deepgram or Gemini if you choose that below." +
                (SpeechToText.RequestModel.IsDownloaded ? "" : $" The first time, it downloads its speech model ({SpeechToText.RequestModel.Size}).");
            ShortcutCombo.ItemsSource = HotkeyListener.Choices;
            ShortcutCombo.SelectedItem = HotkeyListener.Choices.Contains(_settings.AssistantShortcut)
                ? _settings.AssistantShortcut
                : HotkeyListener.Choices[0];
            TypeShortcutCombo.ItemsSource = HotkeyListener.TypeChoices;
            TypeShortcutCombo.SelectedItem = HotkeyListener.TypeChoices.Contains(_settings.TypeShortcut)
                ? _settings.TypeShortcut
                : HotkeyListener.TypeChoices[0];
            UpdateShortcutDescription();

            LanguagesCombo.ItemsSource = new[] { "English", "English and Hindi" }; // in SpeechLanguages order
            LanguagesCombo.SelectedIndex = (int)_settings.SpeechLanguages;
            LanguagesDescription.Text =
                $"What you talk to {Assistant.Name} in. With Hindi too, each request is first checked for which " +
                "language it is, and that check sometimes takes English for Hindi, so leave it on English unless you speak Hindi to it.";

            WakeWordTitle.Text = $"Listen for “{Assistant.WakePhrase}”";
            WakeWordDescription.Text =
                $"Say “{Assistant.WakePhrase}” instead of pressing the shortcut, or say it with your request: “{Assistant.WakePhrase}, next song”. " +
                "The microphone stays on (Windows shows its mic icon in the taskbar). Only the first moment of each thing said near the PC " +
                "is checked for the name, on this PC; nothing is kept or sent anywhere. A Bluetooth headset's microphone is never used for this. " +
                "It can now and then wake when you didn't call it; leave this off to keep the microphone closed until you press a shortcut.";
            WakeWordToggle.IsOn = _settings.WakeWord;
            UpdateWakeWordStatus();

            FollowUpsToggle.IsOn = _settings.FollowUps;
            FollowUpsDescription.Text =
                $"After answering out loud, {Assistant.Name} listens 5 seconds more, so you can just ask the next thing: “and tomorrow?”, " +
                "“what about Delhi?”. Either way it remembers the last few questions for 3 minutes, so a follow-up with the shortcut works too.";
            LoadBrain();
            LoadDeepgram();
            LoadVoice();
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
                ? $"Open {Product.Name} when you sign in."
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

    private void OnStepAsideToggled(object sender, RoutedEventArgs e) => Update(s => s.StepAsideForApps = StepAsideToggle.IsOn);

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
        UpdateWakeWordStatus();
        UpdateBrainEnabled();
        UpdateDeepgramEnabled();
        UpdateVoiceEnabled();
    }

    private async void OnWakeWordToggled(object sender, RoutedEventArgs e)
    {
        Update(s => s.WakeWord = WakeWordToggle.IsOn);
        UpdateWakeWordStatus();

        // The microphone opens in the background; say how that went.
        await Task.Delay(1500);
        UpdateWakeWordStatus();
    }

    private void LoadBrain()
    {
        BrainTitle.Text = $"{Assistant.Name}'s brain";
        BrainDescription.Text =
            $"For what {Assistant.Name} can't do on the PC: questions, Hindi, writing emails and WhatsApp messages (you review and send them), " +
            "and “translate / summarise this” on copied text. Only those requests (and the copied text, when you say “this”) go to the " +
            "model's maker: Anthropic for Claude, Google for Gemini. On Gemini's free tier, Google may use them to improve its products.";
        BrainChoiceDescription.Text =
            "Claude Haiku answers fastest, every time, for a fraction of a cent a question with your own Anthropic key. " +
            "Gemini is free, but its free tier is often busy. Claude can't hear audio: with a Gemini key saved too, Gemini still hears " +
            "your words (when Deepgram doesn't) and your accent.";
        BrainToggle.IsOn = _settings.AiProvider != AiProvider.Off;
        bool hasClaudeKey = SecretStore.Get(ClaudeBrain.KeyName) is { Length: > 0 };
        var choice = _settings.AiProvider != AiProvider.Off ? _settings.AiProvider : hasClaudeKey ? AiProvider.Claude : AiProvider.Gemini;
        BrainCombo.SelectedItem = BrainCombo.Items.Cast<ComboBoxItem>().First(i => (string)i.Tag == choice.ToString());
        ClaudeKeyBox.Password = "";
        ClaudeKeyBox.PlaceholderText = hasClaudeKey ? "Saved (type a new key to replace it)" : "Anthropic API key";
        bool hasKey = SecretStore.Get(GeminiBrain.KeyName) is { Length: > 0 };
        GeminiKeyBox.Password = "";
        GeminiKeyBox.PlaceholderText = hasKey ? "Gemini key saved (type a new key to replace it)" : "Gemini API key";
        BrainStatus.Text = choice == AiProvider.Claude
            ? hasClaudeKey ? "" : "No Anthropic key yet: create one at console.anthropic.com (API keys), paste it here, then Save."
            : hasKey ? "" : "No key yet: paste one from Google AI Studio (free, no card needed), then Save.";
        CloudSpeechToggle.IsOn = _settings.CloudSpeech;
        CloudSpeechDescription.Text =
            "Much more accurate with Indian accents, names and Hinglish than the PC's own speech recognition. What you say after the " +
            $"shortcut or “{Assistant.WakePhrase}” is sent to Google (listening for “{Assistant.WakePhrase}” itself stays on this PC). " +
            "Off, or when Gemini is busy or offline, your voice is turned into text on this PC.";
        UpdateBrainEnabled();
    }

    /// <summary>The model chosen in the list (whether or not the brain is on).</summary>
    private AiProvider ChosenBrain => BrainCombo.SelectedItem is ComboBoxItem { Tag: string tag } && Enum.TryParse<AiProvider>(tag, out var provider)
        ? provider
        : AiProvider.Gemini;

    private void UpdateBrainEnabled()
    {
        bool on = _settings.VoiceAssistant && _settings.AiProvider != AiProvider.Off;
        bool claude = on && ChosenBrain == AiProvider.Claude;
        BrainToggle.IsEnabled = _settings.VoiceAssistant;
        BrainChoiceRow.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        foreach (var part in new FrameworkElement[] { ClaudeKeyRow, ClaudeLink })
        {
            part.Visibility = claude ? Visibility.Visible : Visibility.Collapsed;
        }
        // Gemini's key: needed for Gemini; with Claude, optional (it hears speech and accents for it).
        foreach (var part in new FrameworkElement[] { BrainKeyRow, GeminiLink, CloudSpeechRow })
        {
            part.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        }
        TestBrainButton.Visibility = claude ? Visibility.Collapsed : Visibility.Visible; // (Claude's row has its own)
    }

    private void OnBrainChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_settings.AiProvider != AiProvider.Off)
        {
            Update(s => s.AiProvider = ChosenBrain);
        }
        if (!_loading)
        {
            BrainStatus.Text = "";
        }
        UpdateBrainEnabled();
    }

    private void OnSaveClaudeKeyClick(object sender, RoutedEventArgs e)
    {
        if (ClaudeKeyBox.Password.Trim() is not { Length: > 0 } key)
        {
            return;
        }
        SecretStore.Set(ClaudeBrain.KeyName, key);
        ClaudeKeyBox.Password = "";
        ClaudeKeyBox.PlaceholderText = "Saved (type a new key to replace it)";
        BrainStatus.Text = "Saved. Press Test to check it works.";
    }

    private void OnCloudSpeechToggled(object sender, RoutedEventArgs e) => Update(s => s.CloudSpeech = CloudSpeechToggle.IsOn);

    private void OnBrainToggled(object sender, RoutedEventArgs e)
    {
        Update(s => s.AiProvider = BrainToggle.IsOn ? ChosenBrain : AiProvider.Off);
        UpdateBrainEnabled();
    }

    private void OnSaveKeyClick(object sender, RoutedEventArgs e)
    {
        if (GeminiKeyBox.Password.Trim() is not { Length: > 0 } key)
        {
            return;
        }
        SecretStore.Set(GeminiBrain.KeyName, key);
        GeminiKeyBox.Password = "";
        GeminiKeyBox.PlaceholderText = "Saved (type a new key to replace it)";
        BrainStatus.Text = "Saved. Press Test to check it works.";
    }

    private async void OnTestBrainClick(object sender, RoutedEventArgs e)
    {
        if (GeminiKeyBox.Password.Trim() is { Length: > 0 })
        {
            OnSaveKeyClick(sender, e); // test what's typed
        }
        if (ClaudeKeyBox.Password.Trim() is { Length: > 0 })
        {
            OnSaveClaudeKeyClick(sender, e);
        }
        if (Brains.Current is not { } brain)
        {
            BrainStatus.Text = "Paste a key and press Save first.";
            return;
        }

        TestBrainButton.IsEnabled = TestClaudeButton.IsEnabled = false;
        BrainStatus.Text = $"Asking {brain.Name}…";
        SetTesting(BrainTesting, BrainStatus, true);
        try
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var reply = await brain.AskAsync("Say hello in five words or fewer.", null, [], CancellationToken.None);
            BrainStatus.Text = reply is BrainAnswer answer
                ? $"Works! {brain.Name} says: “{answer.Text}” ({watch.Elapsed.TotalSeconds:0.0} s)"
                : "Works!";
        }
        catch (BrainException ex)
        {
            BrainStatus.Text = ex.Message;
        }
        finally
        {
            TestBrainButton.IsEnabled = TestClaudeButton.IsEnabled = true;
            SetTesting(BrainTesting, BrainStatus, false);
        }
    }

    private void LoadDeepgram()
    {
        DeepgramDescription.Text =
            "Your words show as you speak, and are understood as soon as you stop, with no wait for Gemini to hear them. " +
            $"What you say after the shortcut or “{Assistant.WakePhrase}” is streamed to Deepgram (listening for “{Assistant.WakePhrase}” itself " +
            "stays on this PC). New Deepgram accounts get free credit, no card needed. If Deepgram can't be reached, Gemini or this PC steps in.";
        DeepgramToggle.IsOn = _settings.DeepgramSpeech;
        bool hasKey = DeepgramStream.Key is not null;
        DeepgramKeyBox.Password = "";
        DeepgramKeyBox.PlaceholderText = hasKey ? "Saved (type a new key to replace it)" : "Deepgram API key";
        DeepgramStatus.Text = hasKey ? "" : "No key yet: sign up at Deepgram (free credit, no card), create an API key, paste it here, then Save.";
        UpdateDeepgramEnabled();
    }

    private void UpdateDeepgramEnabled()
    {
        bool on = _settings.VoiceAssistant && _settings.DeepgramSpeech;
        DeepgramToggle.IsEnabled = _settings.VoiceAssistant;
        foreach (var part in new FrameworkElement[] { DeepgramKeyRow, DeepgramLink, DeepgramStatusRow })
        {
            part.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void OnDeepgramToggled(object sender, RoutedEventArgs e)
    {
        Update(s => s.DeepgramSpeech = DeepgramToggle.IsOn);
        UpdateDeepgramEnabled();
    }

    private void OnSaveDeepgramKeyClick(object sender, RoutedEventArgs e)
    {
        if (DeepgramKeyBox.Password.Trim() is not { Length: > 0 } key)
        {
            return;
        }
        SecretStore.Set(DeepgramStream.KeyName, key);
        DeepgramKeyBox.Password = "";
        DeepgramKeyBox.PlaceholderText = "Saved (type a new key to replace it)";
        DeepgramStatus.Text = "Saved. Press Test to check it works.";
    }

    private async void OnTestDeepgramClick(object sender, RoutedEventArgs e)
    {
        if (DeepgramKeyBox.Password.Trim() is { Length: > 0 })
        {
            OnSaveDeepgramKeyClick(sender, e); // test what's typed
        }
        if (DeepgramStream.Key is not { } key)
        {
            DeepgramStatus.Text = "Paste a key and press Save first.";
            return;
        }

        TestDeepgramButton.IsEnabled = false;
        DeepgramStatus.Text = "Connecting to Deepgram…";
        SetTesting(DeepgramTesting, DeepgramStatus, true);
        try
        {
            DeepgramStatus.Text = await DeepgramStream.TestAsync(key) is { } problem
                ? problem
                : $"Works! {Assistant.Name} will hear your requests with Deepgram.";
        }
        finally
        {
            TestDeepgramButton.IsEnabled = true;
            SetTesting(DeepgramTesting, DeepgramStatus, false);
        }
    }

    // ---------------------------------------------------------------- the voice it answers in

    private static readonly Windows.Media.Playback.MediaPlayer s_voiceSample = new() { AudioCategory = Windows.Media.Playback.MediaPlayerAudioCategory.Speech };

    private void LoadVoice()
    {
        VoiceDescription.Text =
            $"{Assistant.Name} answers in a natural Microsoft voice instead of Windows' own, with an Indian, American or British accent " +
            "(and in Hindi when it answers in Hindi). Free for half a million characters a month with your own Azure Speech key " +
            "(the Azure sign-up asks for a card to check it's you). What it says is sent to Microsoft to be spoken; what you say isn't. " +
            "Off, or when Azure can't be reached, Windows' voice answers.";
        AccentDescription.Text =
            "Match mine: Gemini hears the start of each spoken request to tell your accent (so that goes to Google too).";
        VoiceToggle.IsOn = _settings.NaturalVoice;
        bool hasKey = AzureVoice.Key is not null;
        AzureKeyBox.Password = "";
        AzureKeyBox.PlaceholderText = hasKey ? "Saved (type a new key to replace it)" : "Azure Speech key";
        AzureRegionBox.Text = _settings.AzureRegion ?? "";
        VoiceStatus.Text = hasKey ? "" : "No key yet: create a Speech resource in the Azure portal (the free F0 tier), then copy Key 1 and its Region from Keys and Endpoint.";
        AccentCombo.SelectedItem = AccentCombo.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == _settings.ReplyAccent.ToString()) ?? AccentCombo.Items[0];
        VoiceGenderCombo.SelectedIndex = _settings.MaleVoice ? 1 : 0;
        UpdateVoiceEnabled();
    }

    private void UpdateVoiceEnabled()
    {
        bool on = _settings.VoiceAssistant && _settings.NaturalVoice;
        VoiceToggle.IsEnabled = _settings.VoiceAssistant;
        foreach (var part in new FrameworkElement[] { VoiceKeyRow, AzureLink, VoiceStatusRow })
        {
            part.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        }
        AccentRow.Visibility = _settings.VoiceAssistant ? Visibility.Visible : Visibility.Collapsed; // (Windows' voices take the accent too)
    }

    private void OnVoiceToggled(object sender, RoutedEventArgs e)
    {
        Update(s => s.NaturalVoice = VoiceToggle.IsOn);
        UpdateVoiceEnabled();
    }

    private void OnAccentChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AccentCombo.SelectedItem is ComboBoxItem { Tag: string tag } && Enum.TryParse<AccentChoice>(tag, out var accent))
        {
            Update(s => s.ReplyAccent = accent);
        }
    }

    private void OnVoiceGenderChanged(object sender, SelectionChangedEventArgs e) =>
        Update(s => s.MaleVoice = VoiceGenderCombo.SelectedIndex == 1);

    /// <summary>Saves what's typed: a new key (if one's typed) and the region. False (with a note) when there's no region.</summary>
    private bool SaveAzure()
    {
        string region = AzureVoice.NormalizeRegion(AzureRegionBox.Text);
        if (!AzureVoice.IsRegion(region))
        {
            VoiceStatus.Text = "Type your Speech resource's region (shown under Keys and Endpoint), like centralindia or eastus.";
            return false;
        }
        AzureRegionBox.Text = region;
        Update(s => s.AzureRegion = region);
        if (AzureKeyBox.Password.Trim() is { Length: > 0 } key)
        {
            SecretStore.Set(AzureVoice.KeyName, key);
            AzureKeyBox.Password = "";
            AzureKeyBox.PlaceholderText = "Saved (type a new key to replace it)";
        }
        return true;
    }

    private void OnSaveAzureClick(object sender, RoutedEventArgs e)
    {
        if (SaveAzure())
        {
            VoiceStatus.Text = AzureVoice.Key is null ? "Region saved. Paste the key too." : "Saved. Press Test to hear it.";
        }
    }

    /// <summary>Says a line in the voice chosen (Match mine: the accent heard last, else Indian), so you hear it as it'll answer.</summary>
    private async void OnTestVoiceClick(object sender, RoutedEventArgs e)
    {
        if (!SaveAzure())
        {
            return;
        }
        if (AzureVoice.Key is not { } key)
        {
            VoiceStatus.Text = "Paste the key and press Save first.";
            return;
        }

        string voice = AzureVoice.VoiceFor("", Speaker.ReplyAccent, _settings.MaleVoice);
        TestVoiceButton.IsEnabled = false;
        VoiceStatus.Text = "Asking Azure…";
        SetTesting(VoiceTesting, VoiceStatus, true);
        try
        {
            var wav = await AzureVoice.SynthesizeAsync($"Hi, I'm {Assistant.Name}. This is how I'll answer you.", voice, key, _settings.AzureRegion!, CancellationToken.None);
            s_voiceSample.Source = await AzureVoice.ToMediaSourceAsync(wav);
            s_voiceSample.Play();
            VoiceStatus.Text = $"Works! That was {voice}.";
        }
        catch (AzureVoiceException ex)
        {
            VoiceStatus.Text = ex.Message;
        }
        finally
        {
            TestVoiceButton.IsEnabled = true;
            SetTesting(VoiceTesting, VoiceStatus, false);
        }
    }

    private void UpdateWakeWordStatus()
    {
        WakeWordToggle.IsEnabled = _settings.VoiceAssistant;
        string? problem = _settings.VoiceAssistant && _settings.WakeWord ? AssistantActivity.WakeWordProblem : null;
        WakeWordStatus.Text = problem ?? "";
        WakeWordStatus.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnShortcutChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ShortcutCombo.SelectedItem is string shortcut)
        {
            Update(s => s.AssistantShortcut = shortcut);
            UpdateShortcutDescription();
        }
    }

    private void OnFollowUpsToggled(object sender, RoutedEventArgs e) => Update(s => s.FollowUps = FollowUpsToggle.IsOn);

    private void OnLanguagesChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LanguagesCombo.SelectedIndex >= 0)
        {
            Update(s => s.SpeechLanguages = (SpeechLanguages)LanguagesCombo.SelectedIndex);
        }
    }

    private void OnTypeShortcutChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TypeShortcutCombo.SelectedItem is string shortcut)
        {
            Update(s => s.TypeShortcut = shortcut);
            UpdateShortcutDescription();
        }
    }

    /// <summary>The island registers the shortcuts as soon as the setting changes, so whether it got them is known right away.</summary>
    private void UpdateShortcutDescription()
    {
        TypeShortcutCombo.IsEnabled = _settings.VoiceAssistant;
        LanguagesCombo.IsEnabled = _settings.VoiceAssistant;
        FollowUpsToggle.IsEnabled = _settings.VoiceAssistant;
        bool typeWorks = !_settings.VoiceAssistant || AssistantActivity.TypeShortcutWorks;
        TypeShortcutDescription.Text = typeWorks
            ? "Type a request instead of speaking; the answer is shown, not spoken. Enter asks, Esc closes."
            : $"Another app already uses {_settings.TypeShortcut}, so it does nothing here. Pick another.";
        TypeShortcutDescription.Foreground = typeWorks
            ? (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            : (Brush)Application.Current.Resources["SystemFillColorCautionBrush"];

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
