using System.Runtime.InteropServices;

namespace WinIsland.Interop;

/// <summary>
/// A hidden message-only window, for Win32 APIs that report through window messages (clipboard
/// changes, global hotkeys). Create it on the UI thread; messages arrive there.
/// </summary>
/// <remarks>
/// Some need a real window instead, <c>topLevel</c> (hidden until shown, filled with <c>background</c>):
/// app bars, and hearing what Windows tells every window, such as Explorer restarting (see
/// <see cref="Services.TopStrip"/>).
/// </remarks>
internal sealed class MessageWindow
{
    private static readonly IntPtr HWND_MESSAGE = new(-3);
    private const int WS_POPUP = unchecked((int)0x80000000);

    private readonly WndProc _wndProc; // kept alive: Windows holds a pointer to it

    public MessageWindow(string name, bool topLevel = false, IntPtr background = default)
    {
        _wndProc = OnMessage;
        var windowClass = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = GetModuleHandle(null),
            hbrBackground = background,
            lpszClassName = $"{Product.Name}.{name}",
        };
        RegisterClassEx(ref windowClass);
        Handle = topLevel
            ? CreateWindowEx((int)(Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE), windowClass.lpszClassName, Product.Name, WS_POPUP,
                0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, windowClass.hInstance, IntPtr.Zero)
            : CreateWindowEx(0, windowClass.lpszClassName, "", 0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, windowClass.hInstance, IntPtr.Zero);
        if (Handle == IntPtr.Zero)
        {
            throw new InvalidOperationException($"Couldn't create the {name} window (error {Marshal.GetLastWin32Error()})");
        }
    }

    public IntPtr Handle { get; }

    /// <summary>Message, wParam, lParam.</summary>
    public event Action<uint, IntPtr, IntPtr>? MessageReceived;

    private IntPtr OnMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        MessageReceived?.Invoke(message, wParam, lParam);
        return DefWindowProc(hwnd, message, wParam, lParam);
    }

    private delegate IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
