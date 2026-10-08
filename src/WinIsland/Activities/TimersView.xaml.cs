using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WinIsland.Activities;

/// <summary>One line in the Timers tab.</summary>
public sealed class TimerRow : INotifyPropertyChanged
{
    private string _remaining = "";

    internal TimerRow(object item, string glyph, string title, string when)
    {
        Item = item;
        Glyph = glyph;
        Title = title;
        When = when;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Glyph { get; }

    public string Title { get; }

    public string When { get; }

    /// <summary>Time left, updated every second.</summary>
    public string Remaining
    {
        get => _remaining;
        set
        {
            if (value != _remaining)
            {
                _remaining = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Remaining)));
            }
        }
    }

    /// <summary>The timer, alarm or reminder shown.</summary>
    internal object Item { get; }
}

public sealed partial class TimersView : UserControl
{
    public TimersView()
    {
        InitializeComponent();
    }

    public ObservableCollection<TimerRow> Rows { get; } = [];

    internal event Action<TimerRow>? CancelClicked;

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: TimerRow row })
        {
            CancelClicked?.Invoke(row);
        }
    }
}
