using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinIsland.Services;

namespace WinIsland.Activities;

public sealed partial class MediaExpandedView : UserControl
{
    private readonly MediaService _media;
    private bool _barsRunning = true; // so the first call settles them

    internal MediaExpandedView(MediaService media)
    {
        _media = media;
        InitializeComponent();
    }

    public ImageSource? Artwork
    {
        set => Art.ImageSource = value;
    }

    internal void ShowTrack(MediaSnapshot media)
    {
        TitleText.Text = media.DisplayTitle;
        ArtistText.Text = media.Artist;
        SourceText.Text = media.AppName;
        PlayPauseIcon.Glyph = media.IsPlaying ? "" : "";
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

    public void SetBarsRunning(bool running)
    {
        if (running != _barsRunning)
        {
            _barsRunning = running;
            LevelMeter.Animate(Bars.Children, running);
        }
    }

    private static string FormatTime(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    private void OnPreviousClick(object sender, RoutedEventArgs e) => _ = _media.PreviousAsync();

    private void OnPlayPauseClick(object sender, RoutedEventArgs e) => _ = _media.TogglePlayPauseAsync();

    private void OnNextClick(object sender, RoutedEventArgs e) => _ = _media.NextAsync();
}
