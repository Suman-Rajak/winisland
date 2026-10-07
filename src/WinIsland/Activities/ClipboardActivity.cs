using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using WinIsland.Services;

namespace WinIsland.Activities;

/// <summary>
/// Clipboard history, as its own tab in the expanded island: the last <see cref="MaxEntries"/> things
/// you copied (text, links, images, files), newest first; click one to copy it again. Kept in memory
/// only, so it's gone when WinIsland quits; content marked private by password managers is never kept
/// (see <see cref="ClipboardWatcher"/>).
/// </summary>
internal sealed class ClipboardActivity : IslandActivity
{
    private const int MaxEntries = 20;
    private const int MaxImages = 5; // images are the big ones
    private const int VisibleRows = 5;
    private const int RowHeight = 36;
    private static readonly TimeSpan CopiedFeedback = TimeSpan.FromSeconds(1.5);

    private readonly ClipboardWatcher _watcher;
    private readonly ClipboardView _view = new();
    private readonly DispatcherQueueTimer _ageTimer;

    public ClipboardActivity(DispatcherQueue dispatcher)
    {
        _watcher = new ClipboardWatcher(dispatcher);
        _watcher.Copied += clip => _ = OnCopiedAsync(clip);
        _view.EntryClicked += entry => _ = CopyAgainAsync(entry);
        _view.EntryRemoved += entry => _view.Entries.Remove(entry);

        _ageTimer = dispatcher.CreateTimer();
        _ageTimer.Interval = TimeSpan.FromSeconds(20);
        _ageTimer.Tick += (_, _) => UpdateAges();

        AppSettings.Current.Changed += (_, _) =>
        {
            if (!IsEnabled && _view.Entries.Count > 0)
            {
                _view.Entries.Clear(); // turning it off forgets everything
            }
            RaiseChanged();
        };
    }

    private static bool IsEnabled => AppSettings.Current.ClipboardHistory;

    public override string Name => "Clipboard";

    public override int Priority => 0;

    public override bool IsTab => true;

    public override string Glyph => "\uE77F"; // Paste

    public override bool HasExpanded => IsEnabled;

    public override PillShape ExpandedShape => new(420, VisibleRows * RowHeight + 12, 32);

    public override FrameworkElement ExpandedView => _view;

    public override Task StartAsync()
    {
        try
        {
            _watcher.Start();
        }
        catch (Exception ex)
        {
            Log.Error("Clipboard history unavailable", ex);
        }
        return Task.CompletedTask;
    }

    public override void OnShown(bool compact, bool expanded)
    {
        if (expanded)
        {
            UpdateAges();
            _ageTimer.Start();
        }
        else
        {
            _ageTimer.Stop();
        }
    }

    /// <summary>A few made-up entries; for the --demo-clipboard switch.</summary>
    public void Demo()
    {
        _ = OnCopiedAsync(new Clip(ClipKind.Text, "demo:3", "Pooja & Patola lyrics, verse 2", null, null));
        _ = OnCopiedAsync(new Clip(ClipKind.Files, "demo:2", null, null, [@"C:\Users\demo\Documents\Report.pdf"]));
        _ = OnCopiedAsync(new Clip(ClipKind.Link, "demo:1", "https://github.com/Suman-Rajak/WinIsland", null, null));
    }

    private async Task OnCopiedAsync(Clip clip)
    {
        if (!IsEnabled)
        {
            return;
        }

        // Copying something already in the list moves it back to the top.
        var existing = _view.Entries.FirstOrDefault(e => e.Clip.Key == clip.Key);
        if (existing is not null)
        {
            MoveToTop(existing);
            return;
        }

        var (thumbnail, size) = clip.Kind == ClipKind.Image ? await ThumbnailAsync(clip.ImageBytes!) : (null, null);
        _view.Entries.Insert(0, new ClipboardEntry(clip, thumbnail, size));

        while (_view.Entries.Count > MaxEntries)
        {
            _view.Entries.RemoveAt(_view.Entries.Count - 1);
        }
        foreach (var oldImage in _view.Entries.Where(e => e.Clip.Kind == ClipKind.Image).Skip(MaxImages).ToList())
        {
            _view.Entries.Remove(oldImage);
        }
    }

    private async Task CopyAgainAsync(ClipboardEntry entry)
    {
        try
        {
            var package = new DataPackage();
            var clip = entry.Clip;
            switch (clip.Kind)
            {
                case ClipKind.Link:
                    package.SetText(clip.Text!);
                    package.SetWebLink(new Uri(clip.Text!.Trim()));
                    break;
                case ClipKind.Image:
                    var stream = new InMemoryRandomAccessStream();
                    await stream.WriteAsync(clip.ImageBytes!.AsBuffer());
                    stream.Seek(0);
                    package.SetBitmap(RandomAccessStreamReference.CreateFromStream(stream));
                    break;
                case ClipKind.Files:
                    var items = new List<IStorageItem>();
                    foreach (string path in clip.FilePaths!)
                    {
                        if (File.Exists(path)) items.Add(await StorageFile.GetFileFromPathAsync(path));
                        else if (Directory.Exists(path)) items.Add(await StorageFolder.GetFolderFromPathAsync(path));
                    }
                    if (items.Count == 0)
                    {
                        await FlashAsync(entry, "Gone");
                        return;
                    }
                    package.SetStorageItems(items);
                    break;
                default:
                    package.SetText(clip.Text!);
                    break;
            }

            // Putting it back fires a clipboard change of our own; it's already in the list.
            _watcher.IgnoreChangesFor(TimeSpan.FromSeconds(1));
            Clipboard.SetContent(package);
            Clipboard.Flush(); // stays on the clipboard even if WinIsland quits

            MoveToTop(entry);
            await FlashAsync(entry, "Copied");
        }
        catch (Exception ex)
        {
            Log.Error("Copying a clipboard entry again failed", ex);
            await FlashAsync(entry, "Failed");
        }
    }

    private void MoveToTop(ClipboardEntry entry)
    {
        entry.CopiedAt = DateTime.Now;
        int index = _view.Entries.IndexOf(entry);
        if (index > 0)
        {
            _view.Entries.Move(index, 0);
        }
        entry.UpdateAge();
    }

    /// <summary>Shows a word in place of the age for a moment.</summary>
    private async Task FlashAsync(ClipboardEntry entry, string message)
    {
        entry.Age = message;
        await Task.Delay(CopiedFeedback);
        entry.UpdateAge();
    }

    private void UpdateAges()
    {
        foreach (var entry in _view.Entries)
        {
            entry.UpdateAge();
        }
    }

    private static async Task<(BitmapImage? Thumbnail, string? Size)> ThumbnailAsync(byte[] bytes)
    {
        try
        {
            // Not disposed: the BitmapImage may still decode from it after SetSourceAsync returns.
            var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(bytes.AsBuffer());
            stream.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(stream);
            string size = $"{decoder.OrientedPixelWidth} × {decoder.OrientedPixelHeight}";

            stream.Seek(0);
            var thumbnail = new BitmapImage { DecodePixelHeight = 44 };
            await thumbnail.SetSourceAsync(stream);
            return (thumbnail, size);
        }
        catch (Exception ex)
        {
            Log.Error("Making a clipboard thumbnail failed", ex);
            return (null, null);
        }
    }
}
