using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinIsland;

internal enum AiProvider
{
    Off,
    Gemini, // free, with your own key; often busy on the free tier
    Claude, // Claude Haiku: fastest, paid with your own key
}

internal enum SpeechLanguages
{
    English,
    EnglishAndHindi,
}

/// <summary>The accent the assistant answers in.</summary>
internal enum AccentChoice
{
    MatchMine, // the accent you speak with, heard from each request
    Indian,
    American,
    British,
}

internal enum AnimationSpeed
{
    Relaxed,
    Normal,
    Snappy,
}

/// <summary>
/// User settings, stored as JSON in settings.json in <see cref="AppPaths.DataFolder"/>.
/// Change properties on the UI thread, then call <see cref="Save"/>: it raises <see cref="Changed"/>
/// straight away and writes the file shortly after, so dragging a slider doesn't hammer the disk.
/// </summary>
internal sealed class AppSettings
{
    private static readonly string s_path = Path.Combine(AppPaths.DataFolder, "settings.json");

    private static readonly JsonSerializerOptions s_json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly Timer _writeTimer;
    private string? _pendingJson;

    public AppSettings()
    {
        _writeTimer = new Timer(_ => WritePending());
    }

    public static AppSettings Current { get; } = Load();

    /// <summary>Which display the island sits on (e.g. <c>\\.\DISPLAY2</c>); null follows the main display.</summary>
    public string? Display { get; set; }

    /// <summary>Gap between the top of the screen (or a top-docked taskbar) and the island, in pixels at 100% scale.</summary>
    public double DistanceFromTop { get; set; } = 8;

    /// <summary>
    /// The island as a Mac's notch: black, hanging from the top edge, always there, in a frosted bar as tall
    /// as it is at rest (distance and "hide when idle" don't apply). Windows keeps the bar as it keeps the
    /// bottom for the taskbar, so maximized and snapped windows start below it.
    /// </summary>
    public bool Notch { get; set; } = true;

    /// <summary>
    /// With the notch, a Mac's menu bar either side of it: Glyde's menu, the app you're in and Go on the
    /// left; pinned apps, Glyde, Wi-Fi, sound, battery and the time on the right.
    /// </summary>
    public bool MenuBar { get; set; } = true;

    /// <summary>Fade the island out when there's nothing to show; hovering the top of the screen brings it back.</summary>
    public bool HideWhenIdle { get; set; }

    /// <summary>
    /// With an app's window under the island (a browser's tabs), clicks go through to it and the island
    /// opens only when the pointer rests on it; over the desktop it opens on hover as usual.
    /// </summary>
    public bool StepAsideForApps { get; set; } = true;

    public AnimationSpeed AnimationSpeed { get; set; } = AnimationSpeed.Normal;

    /// <summary>0 = solid glass, 0.3 = very see-through.</summary>
    public double GlassTransparency { get; set; } = 0;

    /// <summary>Tint the glass with the artwork's colours while music plays.</summary>
    public bool MatchMusicColors { get; set; } = true;

    /// <summary>Look up artwork online (iTunes, MusicBrainz) when the music app provides none. Sends artist + title to them.</summary>
    public bool FindArtworkOnline { get; set; } = true;

    /// <summary>Briefly show volume and brightness changes on the island.</summary>
    public bool ShowLevelIndicators { get; set; } = true;

    /// <summary>Show "Charging", "Fully charged" and low-battery warnings.</summary>
    public bool ShowBattery { get; set; } = true;

    /// <summary>Show Bluetooth devices connecting and disconnecting.</summary>
    public bool ShowBluetooth { get; set; } = true;

    /// <summary>Show a dot while an app uses the microphone or camera, and say which app.</summary>
    public bool ShowPrivacy { get; set; } = true;

    /// <summary>Keep the last things you copied (in memory only) in a clipboard tab on the expanded island.</summary>
    public bool ClipboardHistory { get; set; } = true;

    /// <summary>A file shelf tab: drop files on the island to keep them handy, drag them out later.</summary>
    public bool FileShelf { get; set; } = true;

    /// <summary>Show other apps' notifications on the island (needs the installed, packaged WinIsland).</summary>
    public bool ShowNotifications { get; set; } = true;

    /// <summary>The voice assistant, started with <see cref="AssistantShortcut"/> or the island's mic button.</summary>
    public bool VoiceAssistant { get; set; } = true;

    /// <summary>One of <see cref="Services.HotkeyListener.Choices"/>.</summary>
    public string AssistantShortcut { get; set; } = Services.HotkeyListener.Choices[0];

    /// <summary>Opens a box to type a request instead of speaking. One of <see cref="Services.HotkeyListener.TypeChoices"/>.</summary>
    public string TypeShortcut { get; set; } = Services.HotkeyListener.TypeChoices[0];

    /// <summary>Listen for "Hey Glyde" all the time (the microphone stays open; nothing leaves the PC).</summary>
    public bool WakeWord { get; set; }

    /// <summary>After answering out loud, listen a few seconds more for a follow-up question (no shortcut needed).</summary>
    public bool FollowUps { get; set; } = true;

    /// <summary>What you speak to the assistant in. English alone unless you choose Hindi too: telling them apart isn't reliable.</summary>
    public SpeechLanguages SpeechLanguages { get; set; } = SpeechLanguages.English;

    /// <summary>The AI model for what the assistant can't do on the PC. Its API key is in <see cref="Services.SecretStore"/>.</summary>
    public AiProvider AiProvider { get; set; } = AiProvider.Gemini;

    /// <summary>
    /// Let the AI model turn spoken requests into text (more accurate with accents, names and Hinglish;
    /// the request's audio goes to Google). The PC's own speech recognition steps in when it can't.
    /// </summary>
    public bool CloudSpeech { get; set; } = true;

    /// <summary>
    /// Stream spoken requests to Deepgram while they're said (once its key is saved): your words show as you
    /// speak and are understood as soon as you stop. Gemini or the PC steps in when it can't.
    /// </summary>
    public bool DeepgramSpeech { get; set; } = true;

    /// <summary>
    /// Answer in Microsoft's natural voices (Azure AI Speech, once its key and <see cref="AzureRegion"/> are
    /// saved; the key is in <see cref="Services.SecretStore"/>). Off, or when Azure can't, Windows' own voice.
    /// </summary>
    public bool NaturalVoice { get; set; } = true;

    /// <summary>Your Azure Speech resource's region ("centralindia", "eastus"); null until it's set.</summary>
    public string? AzureRegion { get; set; }

    /// <summary>The accent replies are said in: yours (heard from each request), or always the one chosen.</summary>
    public AccentChoice ReplyAccent { get; set; } = AccentChoice.MatchMine;

    /// <summary>A male voice for replies rather than a female one.</summary>
    public bool MaleVoice { get; set; }

    /// <summary>The Gemini model found to work, when the default name stops working; null for the default.</summary>
    public string? GeminiModel { get; set; }

    [JsonIgnore]
    public double GlassOpacity => 1 - Math.Clamp(GlassTransparency, 0, MaxGlassTransparency);

    /// <summary>More than a hint of see-through, with nothing to blur what's behind, looks busy rather than glassy.</summary>
    public const double MaxGlassTransparency = 0.1;

    /// <summary>Multiplier for animation durations.</summary>
    [JsonIgnore]
    public double AnimationScale => AnimationSpeed switch
    {
        AnimationSpeed.Relaxed => 1.4,
        AnimationSpeed.Snappy => 0.75,
        _ => 1.0,
    };

    public event EventHandler? Changed;

    public void Save()
    {
        Changed?.Invoke(this, EventArgs.Empty);
        Interlocked.Exchange(ref _pendingJson, JsonSerializer.Serialize(this, s_json));
        _writeTimer.Change(TimeSpan.FromMilliseconds(400), Timeout.InfiniteTimeSpan);
    }

    public void ResetToDefaults()
    {
        var defaults = new AppSettings();
        Display = defaults.Display;
        DistanceFromTop = defaults.DistanceFromTop;
        Notch = defaults.Notch;
        MenuBar = defaults.MenuBar;
        HideWhenIdle = defaults.HideWhenIdle;
        StepAsideForApps = defaults.StepAsideForApps;
        AnimationSpeed = defaults.AnimationSpeed;
        GlassTransparency = defaults.GlassTransparency;
        MatchMusicColors = defaults.MatchMusicColors;
        FindArtworkOnline = defaults.FindArtworkOnline;
        ShowLevelIndicators = defaults.ShowLevelIndicators;
        ShowBattery = defaults.ShowBattery;
        ShowBluetooth = defaults.ShowBluetooth;
        ShowPrivacy = defaults.ShowPrivacy;
        ClipboardHistory = defaults.ClipboardHistory;
        FileShelf = defaults.FileShelf;
        ShowNotifications = defaults.ShowNotifications;
        VoiceAssistant = defaults.VoiceAssistant;
        AssistantShortcut = defaults.AssistantShortcut;
        TypeShortcut = defaults.TypeShortcut;
        WakeWord = defaults.WakeWord;
        SpeechLanguages = defaults.SpeechLanguages;
        FollowUps = defaults.FollowUps;
        AiProvider = defaults.AiProvider;
        CloudSpeech = defaults.CloudSpeech;
        DeepgramSpeech = defaults.DeepgramSpeech;
        NaturalVoice = defaults.NaturalVoice;
        ReplyAccent = defaults.ReplyAccent;
        MaleVoice = defaults.MaleVoice;
        Save();
    }

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(s_path))
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(s_path), s_json) ?? new AppSettings();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Settings file unreadable; using defaults", ex);
        }

        return new AppSettings();
    }

    private void WritePending()
    {
        var json = Interlocked.Exchange(ref _pendingJson, null);
        if (json is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(s_path)!);
            File.WriteAllText(s_path, json);
        }
        catch (Exception ex)
        {
            Log.Error("Saving settings failed", ex);
        }
    }
}
