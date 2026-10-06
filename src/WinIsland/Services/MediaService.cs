using System.Text.RegularExpressions;
using Microsoft.UI.Dispatching;
using Windows.ApplicationModel;
using Windows.Media.Control;
using Windows.Storage.Streams;
using PlaybackStatus = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus;

namespace WinIsland.Services;

internal sealed record MediaSnapshot(
    string Title,
    string Artist,
    string AppName,
    bool IsPlaying,
    IRandomAccessStreamReference? Thumbnail,
    TimeSpan Position,
    TimeSpan Duration,
    DateTimeOffset PositionUpdatedAt)
{
    /// <summary>Some apps (Amazon Music, for one) publish an artist but no title, at least while paused.</summary>
    public string DisplayTitle => Title.Length > 0 ? Title : Artist;

    public string DisplaySubtitle => Title.Length > 0 ? Artist : AppName;

    public string TrackKey => $"{AppName}|{Title}|{Artist}";

    /// <summary>Apps only report the position occasionally, so extrapolate while playing.</summary>
    public TimeSpan EstimatePosition(DateTimeOffset now)
    {
        var position = IsPlaying ? Position + (now - PositionUpdatedAt) : Position;
        return position < TimeSpan.Zero ? TimeSpan.Zero : position > Duration ? Duration : position;
    }
}

/// <summary>
/// Wraps the Windows media session API (the same one the volume flyout uses), so it sees Spotify,
/// browsers, Amazon Music, Media Player and anything else that publishes "now playing" info.
/// <see cref="Changed"/> is always raised on the UI thread.
/// </summary>
internal sealed partial class MediaService
{
    // Session events are unreliable in some apps, so also poll. Reads are cheap.
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    // Reads fail transiently (ERROR_NOT_READY while the app is busy, e.g. changing track).
    // Keep showing the last good info through a few failures instead of flashing to "nothing playing".
    private const int MaxConsecutiveFailures = 3;

    private static readonly Dictionary<string, string> s_appNames = new(StringComparer.OrdinalIgnoreCase);

    private readonly DispatcherQueue _dispatcher;
    private readonly bool _demo;
    private DispatcherQueueTimer? _pollTimer;
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private int _refreshQueued;
    private int _failures;

    public MediaService(DispatcherQueue dispatcher, bool demo)
    {
        _dispatcher = dispatcher;
        _demo = demo;
    }

    public MediaSnapshot? Current { get; private set; }

    public event EventHandler? Changed;

    public async Task StartAsync()
    {
        if (_demo)
        {
            Current = new MediaSnapshot("Blinding Lights", "The Weeknd", "Spotify", IsPlaying: true, Thumbnail: null,
                TimeSpan.FromSeconds(83), TimeSpan.FromSeconds(200), DateTimeOffset.Now);
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }

        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.CurrentSessionChanged += (_, _) => QueueRefresh();
            _manager.SessionsChanged += (_, _) => QueueRefresh();

            _pollTimer = _dispatcher.CreateTimer();
            _pollTimer.Interval = PollInterval;
            _pollTimer.Tick += (_, _) => QueueRefresh();
            _pollTimer.Start();

            await RefreshAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Media sessions unavailable", ex);
        }
    }

    public Task TogglePlayPauseAsync() => Run(s => s.TryTogglePlayPauseAsync().AsTask());

    public Task NextAsync() => Run(s => s.TrySkipNextAsync().AsTask());

    public Task PreviousAsync() => Run(s => s.TrySkipPreviousAsync().AsTask());

    private async Task Run(Func<GlobalSystemMediaTransportControlsSession, Task<bool>> command)
    {
        if (_session is null)
        {
            return;
        }

        try
        {
            await command(_session);
            QueueRefresh();
        }
        catch (Exception ex)
        {
            Log.Error("Media command failed", ex);
        }
    }

    /// <summary>Session events arrive on background threads and in bursts; coalesce them into one UI-thread refresh.</summary>
    private void QueueRefresh()
    {
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 0)
        {
            _dispatcher.TryEnqueue(async () =>
            {
                Interlocked.Exchange(ref _refreshQueued, 0);
                await RefreshAsync();
            });
        }
    }

    private async Task RefreshAsync()
    {
        var session = PickSession();
        Attach(session);

        MediaSnapshot? snapshot = null;
        if (session is not null)
        {
            try
            {
                var properties = await session.TryGetMediaPropertiesAsync();
                var playback = session.GetPlaybackInfo();
                var timeline = session.GetTimelineProperties();

                string title = properties?.Title?.Trim() ?? string.Empty;
                string artist = properties?.Artist?.Trim() ?? string.Empty;
                if (title.Length > 0 || artist.Length > 0)
                {
                    snapshot = new MediaSnapshot(
                        title,
                        artist,
                        FriendlyAppName(session.SourceAppUserModelId),
                        playback?.PlaybackStatus == PlaybackStatus.Playing,
                        properties!.Thumbnail,
                        timeline.Position - timeline.StartTime,
                        timeline.EndTime - timeline.StartTime,
                        timeline.LastUpdatedTime);
                }

                _failures = 0;
            }
            catch (Exception ex)
            {
                if (_failures++ == 0)
                {
                    Log.Error("Reading media session failed (will retry)", ex);
                }

                if (_failures <= MaxConsecutiveFailures)
                {
                    return; // keep the last good snapshot; the poll timer retries
                }
            }
        }

        Current = snapshot;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Prefer whatever is actually playing; Windows' "current" session can be a paused app.</summary>
    private GlobalSystemMediaTransportControlsSession? PickSession()
    {
        if (_manager is null)
        {
            return null;
        }

        var sessions = _manager.GetSessions();
        foreach (var session in sessions)
        {
            try
            {
                if (session.GetPlaybackInfo()?.PlaybackStatus == PlaybackStatus.Playing)
                {
                    return session;
                }
            }
            catch
            {
                // Session's app has gone away; skip it.
            }
        }

        return _manager.GetCurrentSession() ?? sessions.FirstOrDefault();
    }

    private void Attach(GlobalSystemMediaTransportControlsSession? session)
    {
        if (ReferenceEquals(session, _session))
        {
            return;
        }

        if (_session is not null)
        {
            _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
            _session.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
        }

        _session = session;
        _failures = 0;

        if (_session is not null)
        {
            _session.MediaPropertiesChanged += OnMediaPropertiesChanged;
            _session.PlaybackInfoChanged += OnPlaybackInfoChanged;
            _session.TimelinePropertiesChanged += OnTimelinePropertiesChanged;
        }
    }

    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args) => QueueRefresh();

    private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args) => QueueRefresh();

    private void OnTimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args) => QueueRefresh();

    private static string FriendlyAppName(string appUserModelId)
    {
        if (s_appNames.TryGetValue(appUserModelId, out var cached))
        {
            return cached;
        }

        var id = appUserModelId.ToLowerInvariant();
        string? name = id switch
        {
            _ when id.Contains("spotify") => "Spotify",
            _ when id.Contains("msedge") => "Microsoft Edge",
            _ when id.Contains("chrome") => "Chrome",
            _ when id.Contains("brave") => "Brave",
            _ when id.Contains("firefox") || id == "308046b0af4a39cb" => "Firefox",
            _ when id.Contains("zunemusic") => "Media Player",
            _ => null,
        };

        if (name is null)
        {
            try
            {
                // Store apps (Amazon Music, Apple Music…) can tell us their real display name.
                name = AppInfo.GetFromAppUserModelId(appUserModelId)?.DisplayInfo?.DisplayName;
            }
            catch
            {
                // Not a packaged app.
            }
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            // "Publisher.SomeApp_hash!SomeApp" or "someapp.exe" -> "Some App"
            var raw = appUserModelId.Split('!')[^1];
            if (raw.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                raw = raw[..^4];
            }
            raw = raw.Split('.')[^1];
            name = CamelCaseBoundary().Replace(raw, " $1");
        }

        return s_appNames[appUserModelId] = name;
    }

    [GeneratedRegex("(?<=[a-z])([A-Z])")]
    private static partial Regex CamelCaseBoundary();
}
