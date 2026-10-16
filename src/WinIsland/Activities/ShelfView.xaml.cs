using System.Collections.ObjectModel;
using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;

namespace WinIsland.Activities;

public sealed partial class ShelfView : UserControl, IEntrance
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

    /// <summary>Highlights the drop zone while files are being dragged over the island: it brightens and gently swells, ready to catch them.</summary>
    public void SetDragOver(bool dragOver)
    {
        bool changed = dragOver != _dragOver;
        _dragOver = dragOver;
        UpdateDropZone();
        if (changed)
        {
            DropZone.UpdateLayout(); // (it may have just appeared: its size, to swell about its middle)
            Motion.Breathe(DropOutline, dragOver, peak: 1.02f, period: 1200);
        }
    }

    /// <summary>The tiles come in one after another (or the "drop files here" note rises in).</summary>
    public void Enter()
    {
        if (Items.Count == 0)
        {
            Motion.Enter(EmptyText, new Vector2(0, 8), delay: 60);
            return;
        }
        Motion.Cascade(Items.Select((_, i) => Tiles.ContainerFromIndex(i)).OfType<UIElement>(), new Vector2(0, 10), delay: 50, step: 35);
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
