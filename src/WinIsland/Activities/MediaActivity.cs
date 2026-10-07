using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinIsland.Services;

namespace WinIsland.Activities;

/// <summary>Now playing: artwork and level bars in the compact pill; title, progress and controls when expanded.</summary>
internal sealed class MediaActivity : IslandActivity
{
    private readonly MediaService _media;
    private readonly MediaCompactView _compactView = new();
    private readonly MediaExpandedView _expandedView;
    private readonly DispatcherQueueTimer _progressTimer;

    private string? _artworkTrack;
    private bool _artworkFromThumbnail;
    private GlassTheme? _theme;
    private bool _findArtworkOnline = AppSettings.Current.FindArtworkOnline;
    private bool _compactShown;
    private bool _expandedShown;

    public MediaActivity(DispatcherQueue dispatcher, bool demo)
    {
        _media = new MediaService(dispatcher, demo);
        _media.Changed += OnMediaChanged;
        _expandedView = new MediaExpandedView(_media);

        _progressTimer = dispatcher.CreateTimer();
        _progressTimer.Interval = TimeSpan.FromMilliseconds(500);
        _progressTimer.Tick += (_, _) => _expandedView.ShowProgress(_media.Current);

        AppSettings.Current.Changed += OnSettingsChanged;
    }

    public override string Name => "Media";

    public override int Priority => 50;

    public override string Glyph => ""; // Audio (music note)

    private bool IsPlaying => _media.Current?.IsPlaying == true;

    public override bool WantsCompact => IsPlaying;

    /// <summary>Paused music still gets the expanded view, so you can press play.</summary>
    public override bool HasExpanded => _media.Current is not null;

    public override PillShape CompactShape => new(250, 34, 17);

    public override PillShape ExpandedShape => new(420, 172, 38);

    public override FrameworkElement CompactView => _compactView;

    public override FrameworkElement ExpandedView => _expandedView;

    /// <summary>The glass takes the artwork's colours while music plays.</summary>
    public override GlassTheme? Theme => IsPlaying ? _theme : null;

    public override Task StartAsync() => _media.StartAsync();

    public override void OnShown(bool compact, bool expanded)
    {
        _compactShown = compact;
        _expandedShown = expanded;
        UpdateAnimations();
    }

    private void UpdateAnimations()
    {
        _compactView.SetBarsRunning(_compactShown && IsPlaying);
        _expandedView.SetBarsRunning(_expandedShown && IsPlaying);

        if (_expandedShown)
        {
            _expandedView.ShowProgress(_media.Current);
            _progressTimer.Start();
        }
        else
        {
            _progressTimer.Stop();
        }
    }

    private async void OnMediaChanged(object? sender, EventArgs e)
    {
        var media = _media.Current;
        if (media is not null)
        {
            _expandedView.ShowTrack(media);
        }

        UpdateAnimations();
        RaiseChanged();

        // Reload when the track changes, or when the app starts publishing a thumbnail for a track we
        // only had a fallback (or nothing) for. Apps often republish the same track with and without one.
        var track = media?.TrackKey;
        bool hasThumbnail = media?.Thumbnail is not null;
        if (track == _artworkTrack && !(hasThumbnail && !_artworkFromThumbnail))
        {
            return;
        }

        _artworkTrack = track;
        _artworkFromThumbnail = hasThumbnail;
        var artwork = media is null ? null : await ArtworkService.LoadAsync(media);
        if (_artworkTrack != track)
        {
            return; // the track changed while we were loading
        }

        _theme = artwork is null ? null : new GlassTheme(artwork.Glow, artwork.Tint, artwork.Accent);
        _compactView.Artwork = artwork?.Cover;
        _expandedView.Artwork = artwork?.Cover;
        RaiseChanged();
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        if (AppSettings.Current.FindArtworkOnline != _findArtworkOnline)
        {
            _findArtworkOnline = AppSettings.Current.FindArtworkOnline;
            _artworkTrack = null; // re-fetch (or drop) looked-up artwork
            OnMediaChanged(this, EventArgs.Empty);
        }
    }
}
