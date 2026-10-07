using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WinIsland.Activities;

public sealed partial class ClipboardView : UserControl
{
    public ClipboardView()
    {
        InitializeComponent();
        Entries.CollectionChanged += (_, _) => UpdateEmptyState();
        UpdateEmptyState();
    }

    public ObservableCollection<ClipboardEntry> Entries { get; } = [];

    /// <summary>A row was clicked: put it back on the clipboard.</summary>
    internal event Action<ClipboardEntry>? EntryClicked;

    /// <summary>A row's × was clicked: forget it.</summary>
    internal event Action<ClipboardEntry>? EntryRemoved;

    private void OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ClipboardEntry entry)
        {
            EntryClicked?.Invoke(entry);
        }
    }

    private void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ClipboardEntry entry })
        {
            EntryRemoved?.Invoke(entry);
        }
    }

    private void UpdateEmptyState()
    {
        bool empty = Entries.Count == 0;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        List.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
    }
}
