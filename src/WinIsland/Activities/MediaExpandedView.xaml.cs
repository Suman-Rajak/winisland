using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinIsland.Services;

namespace WinIsland.Activities;

public sealed partial class MediaExpandedView : UserControl, IEntrance
{
    private readonly MediaService _media;
    private bool _barsRunning = true; // so the first call settles them

    internal MediaExpandedView(MediaService media)
    {
        _media = media;
        InitializeComponent();
    }

    /// <summary>A new song's artwork pops in.</summary>
    public ImageSource? Artwork
    {
        set
        {
            Art.ImageSource = value;
            if (value is not null)
            {
                Motion.Pop(ArtHost, from: 0.8f);
            }
        }
    }

    /// <summary>The artwork pops in; the title, progress and buttons follow it in, one after another.</summary>
    public void Enter()
    {
        Motion.Pop(ArtHost, delay: 30, from: 0.6f);
        Motion.Enter(TextStack, new Vector2(14, 0), delay: 80);
        Motion.Enter(Bars, Vector2.Zero, delay: 140);
        Motion.Enter(ProgressRow, new Vector2(0, 8), delay: 120);
        Motion.Cascade(Controls.Children, new Vector2(0, 8), delay: 170, step: 45);
    }

    internal void ShowTrack(MediaSnapshot media)
    {
        // Another song: its title slides in. Play/pause: the button's icon pops.
        if (TitleText.Text.Length > 0 && media.DisplayTitle != TitleText.Text)
        {
            Motion.Enter(TextStack, new Vector2(16, 0), duration: 380);
        }
        string glyph = media.IsPlaying ? "" : "";
        if (PlayPauseIcon.Glyph != glyph)
        {
            PlayPauseIcon.Glyph = glyph;
            Motion.Pop(PlayPauseIcon, from: 0.6f);
        }

        TitleText.Text = media.DisplayTitle;
        ArtistText.Text = media.Artist;
        SourceText.Text = media.AppName;
        ShowProgress(media);
    }

    /// <summary>Apps that don't report a track length (Amazon Music) get no progress row.</summary>
    internal void ShowProgress(MediaSnapshot? media)
    {
        if (media is null || media.Duration <= TimeSpan.Zero)
        {
            ProgressRow.Visibility = Visibility.Collapsed;
            return;
        }

        var position = media.EstimatePosition(DateTimeOffset.Now);
        ProgressRow.Visibility = Visibility.Visible;
        Progress.Value = position / media.Duration;
        ElapsedText.Text = FormatTime(position);
        RemainingText.Text = "-" + FormatTime(media.Duration - position);
    }

    /// <summary>While the music plays (and the island shows it): the level bars bounce and the artwork slowly swells, as if breathing with it.</summary>
    public void SetBarsRunning(bool running)
    {
        if (running != _barsRunning)
        {
            _barsRunning = running;
            LevelMeter.Animate(Bars.Children, running);
            Motion.Breathe(ArtBreath, running, peak: 1.04f, period: 4200);
        }
    }

    private static string FormatTime(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    private void OnPreviousClick(object sender, RoutedEventArgs e) => _ = _media.PreviousAsync();

    private void OnPlayPauseClick(object sender, RoutedEventArgs e) => _ = _media.TogglePlayPauseAsync();

    private void OnNextClick(object sender, RoutedEventArgs e) => _ = _media.NextAsync();
}
