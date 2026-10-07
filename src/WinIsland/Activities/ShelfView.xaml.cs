using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;

namespace WinIsland.Activities;

public sealed partial class ShelfView : UserControl
{
    private static readonly SolidColorBrush Idle = new(Color.FromArgb(0x40, 255, 255, 255));
    private static readonly SolidColorBrush Hot = new(Color.FromArgb(0xCC, 255, 255, 255));
    private bool _dragOver;

    public ShelfView()
    {
        InitializeComponent();
        Items.CollectionChanged += (_, _) => UpdateDropZone();
        UpdateDropZone();
    }

    public ObservableCollection<ShelfItem> Items { get; } = [];

    internal event Action<ShelfItem>? ItemClicked;

    internal event Action<ShelfItem>? ItemRemoved;

    /// <summary>Highlights the drop zone while files are being dragged over the island.</summary>
    public void SetDragOver(bool dragOver)
    {
        _dragOver = dragOver;
        UpdateDropZone();
    }

    private void UpdateDropZone()
    {
        bool empty = Items.Count == 0;
        DropZone.Visibility = empty || _dragOver ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        DropOutline.Stroke = _dragOver ? Hot : Idle;
    }

    private void OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ShelfItem item)
        {
            ItemClicked?.Invoke(item);
        }
    }

    private void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ShelfItem item })
        {
            ItemRemoved?.Invoke(item);
        }
    }

    /// <summary>Dragging tiles out hands over the real files, as a copy: the originals never move.</summary>
    private void OnDragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        e.Data.SetStorageItems(e.Items.OfType<ShelfItem>().Select(i => i.StorageItem).ToList());
        e.Data.RequestedOperation = DataPackageOperation.Copy;
    }
}
