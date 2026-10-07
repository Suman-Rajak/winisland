using Microsoft.UI.Xaml.Media;
using Windows.Storage;

namespace WinIsland.Activities;

/// <summary>One file or folder on the shelf. The shelf only points at it; the file stays where it is.</summary>
public sealed class ShelfItem
{
    internal ShelfItem(IStorageItem storageItem, ImageSource? thumbnail)
    {
        StorageItem = storageItem;
        Thumbnail = thumbnail;
    }

    internal IStorageItem StorageItem { get; }

    public string Path => StorageItem.Path;

    public string Name => StorageItem.Name;

    public ImageSource? Thumbnail { get; }
}
