using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace WinIsland.Activities;

/// <summary>
/// The file shelf, a tab in the expanded island: drag files onto the island to park them, drag them out
/// later (always as a copy), click to open, × to take off the shelf. The shelf only remembers where
/// files are (in %LOCALAPPDATA%\WinIsland\shelf.json, so it survives restarts); files never move.
/// The one exception is things that aren't files yet, like an image dragged out of a browser: those
/// are saved into %LOCALAPPDATA%\WinIsland\Shelf so they have somewhere to live.
/// </summary>
internal sealed class ShelfActivity : IslandActivity
{
    private const int MaxItems = 30;
    private const int VisibleRows = 2;
    private const int TileHeight = 84; // tile plus its margins

    private static readonly string DataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinIsland");
    private static readonly string StoreFile = Path.Combine(DataFolder, "shelf.json");
    private static readonly string SavedDropsFolder = Path.Combine(DataFolder, "Shelf");

    private readonly ShelfView _view = new();
    private bool _persist = true;

    public ShelfActivity()
    {
        _view.ItemClicked += Open;
        _view.ItemRemoved += Remove;
        AppSettings.Current.Changed += (_, _) => RaiseChanged();
    }

    public override string Name => "Shelf";

    public override int Priority => 0;

    public override bool IsTab => true;

    public override string Glyph => ""; // Folder

    public override bool HasExpanded => AppSettings.Current.FileShelf;

    public override PillShape ExpandedShape => new(420, VisibleRows * TileHeight + 14, 32);

    public override FrameworkElement ExpandedView => _view;

    /// <summary>Highlights the drop zone while files are dragged over the island.</summary>
    public void SetDragOver(bool dragOver) => _view.SetDragOver(dragOver);

    public override async Task StartAsync()
    {
        if (!_persist || !File.Exists(StoreFile))
        {
            return;
        }

        try
        {
            var paths = JsonSerializer.Deserialize<List<string>>(await File.ReadAllTextAsync(StoreFile)) ?? [];
            var items = new List<IStorageItem>();
            foreach (string path in paths)
            {
                // Files deleted or moved since are quietly dropped.
                if (File.Exists(path)) items.Add(await StorageFile.GetFileFromPathAsync(path));
                else if (Directory.Exists(path)) items.Add(await StorageFolder.GetFolderFromPathAsync(path));
            }
            items.Reverse(); // AddAsync puts each at the front; keep the saved order
            await AddAsync(items, save: false);
        }
        catch (Exception ex)
        {
            Log.Error("Loading the shelf failed", ex);
        }
    }

    /// <summary>Puts a few of the project's own files on the shelf (not saved); for the --demo-shelf switch.</summary>
    public async Task DemoAsync()
    {
        _persist = false;
        string root = AppContext.BaseDirectory;
        var items = new List<IStorageItem>
        {
            await StorageFile.GetFileFromPathAsync(Path.Combine(root, "Assets", "island.ico")),
            await StorageFile.GetFileFromPathAsync(Path.Combine(root, "WinIsland.dll")),
            await StorageFolder.GetFolderFromPathAsync(Path.Combine(root, "Assets")),
        };
        await AddAsync(items, save: false);
    }

    /// <summary>Adds dropped files and folders to the front of the shelf.</summary>
    public async Task AddAsync(IReadOnlyList<IStorageItem> items, bool save = true)
    {
        foreach (var dropped in items)
        {
            try
            {
                var item = string.IsNullOrEmpty(dropped.Path) && dropped is StorageFile virtualFile
                    ? await SaveDroppedAsync(virtualFile)
                    : dropped;
                if (string.IsNullOrEmpty(item.Path))
                {
                    continue;
                }

                var existing = _view.Items.FirstOrDefault(i => string.Equals(i.Path, item.Path, StringComparison.OrdinalIgnoreCase));
                if (existing is not null)
                {
                    _view.Items.Move(_view.Items.IndexOf(existing), 0);
                    continue;
                }

                _view.Items.Insert(0, new ShelfItem(item, await ThumbnailAsync(item)));
            }
            catch (Exception ex)
            {
                Log.Error($"Adding '{dropped.Name}' to the shelf failed", ex);
            }
        }

        while (_view.Items.Count > MaxItems)
        {
            _view.Items.RemoveAt(_view.Items.Count - 1);
        }

        if (save)
        {
            Save();
        }
    }

    /// <summary>Something that isn't a file on disk yet (a browser image): save a copy of our own.</summary>
    private static async Task<IStorageItem> SaveDroppedAsync(StorageFile file)
    {
        Directory.CreateDirectory(SavedDropsFolder);
        var folder = await StorageFolder.GetFolderFromPathAsync(SavedDropsFolder);
        return await file.CopyAsync(folder, file.Name, NameCollisionOption.GenerateUniqueName);
    }

    private void Remove(ShelfItem item)
    {
        _view.Items.Remove(item);
        Save();

        // Only ever deletes our own saved copies of dropped browser images and the like, never your files.
        string folder = Path.GetFullPath(SavedDropsFolder) + Path.DirectorySeparatorChar;
        if (Path.GetFullPath(item.Path).StartsWith(folder, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                File.Delete(item.Path);
            }
            catch (Exception ex)
            {
                Log.Error("Deleting a saved shelf drop failed", ex);
            }
        }
    }

    private static void Open(ShelfItem item)
    {
        try
        {
            Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error($"Opening '{item.Path}' failed", ex);
        }
    }

    private void Save()
    {
        if (!_persist)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(DataFolder);
            File.WriteAllText(StoreFile, JsonSerializer.Serialize(_view.Items.Select(i => i.Path).ToList()));
        }
        catch (Exception ex)
        {
            Log.Error("Saving the shelf failed", ex);
        }
    }

    /// <summary>Windows' own icon for the file, or a preview for images.</summary>
    private static async Task<BitmapImage?> ThumbnailAsync(IStorageItem item)
    {
        try
        {
            if (item is IStorageItemProperties properties)
            {
                var thumbnail = await properties.GetThumbnailAsync(ThumbnailMode.SingleItem, 64);
                if (thumbnail is not null)
                {
                    var bitmap = new BitmapImage();
                    await bitmap.SetSourceAsync(thumbnail);
                    return bitmap;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Thumbnail for '{item.Name}' failed", ex);
        }
        return null;
    }
}
