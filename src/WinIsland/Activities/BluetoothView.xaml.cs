using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using WinIsland.Services;

namespace WinIsland.Activities;

public sealed partial class BluetoothView : UserControl, IEntrance
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

    /// <summary>Replaces "Connected" with the device's battery once it's known (it slides in, filling up).</summary>
    internal void ShowBattery(int percent)
    {
        var color = BatteryIcon.ColorFor(percent);
        bool news = Battery.Visibility != Visibility.Visible;
        StatusText.Text = $"{percent}%";
        StatusText.Foreground = new SolidColorBrush(color);
        Battery.Show(percent, color);
        Battery.Visibility = Visibility.Visible;
        if (news)
        {
            Motion.Enter(StatusText, new Vector2(0, 6));
            Motion.Pop(Battery, from: 0.6f);
            Battery.FillUp(delay: 80);
        }
    }

    /// <summary>The device's icon pops in, its name and status slide in from either side.</summary>
    public void Enter()
    {
        Motion.Pop(Glyph, delay: 20, from: 0.4f);
        Motion.Enter(NameText, new Vector2(-10, 0), delay: 70);
        Motion.Enter(StatusText, new Vector2(10, 0), delay: 120);
        if (Battery.Visibility == Visibility.Visible)
        {
            Battery.FillUp(delay: 150);
        }
    }
}
