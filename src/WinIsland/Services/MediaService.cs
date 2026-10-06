using Microsoft.UI.Dispatching;
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
    /// <summary>Changes whenever the artwork should be reloaded.</summary>
    public string ArtworkKey => $"{AppName}|{Title}|{Artist}|{Thumbnail is not null}";

    /// <summary>Apps only report the position occasionally, so extrapolate while playing.</summary>
    public TimeSpan EstimatePosition(DateTimeOffset now)
    {
        var position = IsPlaying ? Position + (now - PositionUpdatedAt) : Position;
        return position < TimeSpan.Zero ? TimeSpan.Zero : position > Duration ? Duration : position;
    }
}

/// <summary>
/// Wraps the Windows media session API (the same one the volume flyout uses), so it sees Spotify,
/// browsers, Media Player and anything else that publishes "now playing" info.
/// <see cref="Changed"/> is always raised on the UI thread.
/// </summary>
internal sealed class MediaService
{
    private readonly DispatcherQueue _dispatcher;
    private readonly bool _demo;
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private int _refreshQueued;

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
        var session = _manager?.GetCurrentSession();
        Attach(session);

        MediaSnapshot? snapshot = null;
        if (session is not null)
        {
            try
            {
                var properties = await session.TryGetMediaPropertiesAsync();
                var playback = session.GetPlaybackInfo();
                var timeline = session.GetTimelineProperties();

                if (!string.IsNullOrWhiteSpace(properties?.Title))
                {
                    snapshot = new MediaSnapshot(
                        properties.Title,
                        properties.Artist ?? string.Empty,
                        FriendlyAppName(session.SourceAppUserModelId),
                        playback?.PlaybackStatus == PlaybackStatus.Playing,
                        properties.Thumbnail,
                        timeline.Position - timeline.StartTime,
                        timeline.EndTime - timeline.StartTime,
                        timeline.LastUpdatedTime);
                }
            }
            catch (Exception ex)
            {
                // The session can disappear between GetCurrentSession and the property read.
                Log.Error("Reading media session failed", ex);
            }
        }

        Current = snapshot;
        Changed?.Invoke(this, EventArgs.Empty);
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
        var id = appUserModelId.ToLowerInvariant();
        return id switch
        {
            _ when id.Contains("spotify") => "Spotify",
            _ when id.Contains("msedge") => "Microsoft Edge",
            _ when id.Contains("chrome") => "Chrome",
            _ when id.Contains("brave") => "Brave",
            _ when id.Contains("firefox") || id == "308046b0af4a39cb" => "Firefox",
            _ when id.Contains("zunemusic") => "Media Player",
            _ => Path.GetFileNameWithoutExtension(appUserModelId.Split('!')[0]),
        };
    }
}
