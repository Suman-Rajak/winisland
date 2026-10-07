using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using WinIsland.Services;

namespace WinIsland.Activities;

public sealed partial class BluetoothView : UserControl
{
    private static readonly SolidColorBrush Muted = new(Color.FromArgb(0x99, 255, 255, 255));
    private static readonly SolidColorBrush Faint = new(Color.FromArgb(0x66, 255, 255, 255));
    private static readonly SolidColorBrush Bright = new(Color.FromArgb(255, 255, 255, 255));

    public BluetoothView()
    {
        InitializeComponent();
    }

    internal void Show(BluetoothDeviceInfo device, bool connected)
    {
        Glyph.Glyph = device.Kind switch
        {
            BluetoothDeviceKind.Headphones => "", // Headphone
            BluetoothDeviceKind.Speaker => "",    // Speakers
            BluetoothDeviceKind.Phone => "",      // CellPhone
            BluetoothDeviceKind.Mouse => "",      // Mouse
            BluetoothDeviceKind.Keyboard => "",   // KeyboardClassic
            BluetoothDeviceKind.Gamepad => "",    // Game
            BluetoothDeviceKind.Tablet => "",     // Tablet
            BluetoothDeviceKind.Computer => "",   // DeviceLaptopNoPic
            _ => "",                              // Bluetooth
        };
        Glyph.Foreground = connected ? Bright : Faint;
        NameText.Text = device.Name;
        NameText.Foreground = connected ? Bright : Muted;
        StatusText.Text = connected ? "Connected" : "Disconnected";
        StatusText.Foreground = Muted;
        Battery.Visibility = Visibility.Collapsed;
    }

    /// <summary>Replaces "Connected" with the device's battery once it's known.</summary>
    internal void ShowBattery(int percent)
    {
        var color = BatteryIcon.ColorFor(percent);
        StatusText.Text = $"{percent}%";
        StatusText.Foreground = new SolidColorBrush(color);
        Battery.Show(percent, color);
        Battery.Visibility = Visibility.Visible;
    }
}
