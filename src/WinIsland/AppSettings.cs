using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinIsland;

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

    /// <summary>Fade the island out when there's nothing to show; hovering the top of the screen brings it back.</summary>
    public bool HideWhenIdle { get; set; }

    public AnimationSpeed AnimationSpeed { get; set; } = AnimationSpeed.Normal;

    /// <summary>0 = solid glass, 0.3 = very see-through.</summary>
    public double GlassTransparency { get; set; } = 0.08;

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

    [JsonIgnore]
    public double GlassOpacity => 1 - Math.Clamp(GlassTransparency, 0, 0.3);

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
        HideWhenIdle = defaults.HideWhenIdle;
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
