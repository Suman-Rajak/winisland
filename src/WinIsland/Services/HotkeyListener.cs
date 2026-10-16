using System.Runtime.InteropServices;
using WinIsland.Interop;

namespace WinIsland.Services;

/// <summary>
/// A system-wide keyboard shortcut (works whichever app you're in), like "Ctrl+Alt+Space".
/// Create on the UI thread; <see cref="Pressed"/> is raised there.
/// </summary>
internal sealed class HotkeyListener
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;
    private const uint VK_SPACE = 0x20;
    private const int HotkeyId = 1;

    /// <summary>The talk shortcuts offered in Settings; the first is the default.</summary>
    public static readonly IReadOnlyList<string> Choices = ["Ctrl+Alt+S", "Ctrl+Alt+Space", "Ctrl+Shift+Space", "Alt+Shift+Space"];

    /// <summary>The type shortcuts offered in Settings (none shared with <see cref="Choices"/>); the first is the default.</summary>
    public static readonly IReadOnlyList<string> TypeChoices = ["Ctrl+Alt+G", "Ctrl+Shift+G", "Alt+Shift+G"];

    private readonly MessageWindow _window;
    private bool _registered;

    /// <param name="name">Tells this shortcut's hidden window apart from another listener's.</param>
    public HotkeyListener(string name)
    {
        _window = new MessageWindow(name);
        _window.MessageReceived += (message, wParam, _) =>
        {
            if (message == WM_HOTKEY && wParam == HotkeyId)
            {
                Pressed?.Invoke();
            }
        };
    }

    public event Action? Pressed;

    /// <summary>Listens for <paramref name="shortcut"/> instead of the previous one. False if another app already owns it.</summary>
    public bool Register(string shortcut)
    {
        Unregister();
        if (!TryParse(shortcut, out uint modifiers, out uint key))
        {
            return false;
        }

        _registered = RegisterHotKey(_window.Handle, HotkeyId, modifiers | MOD_NOREPEAT, key);
        return _registered;
    }

    public void Unregister()
    {
        if (_registered)
        {
            UnregisterHotKey(_window.Handle, HotkeyId);
            _registered = false;
        }
    }

    /// <summary>"Ctrl+Alt+Space" → modifier flags and a virtual key (Space, or a letter or digit).</summary>
    private static bool TryParse(string shortcut, out uint modifiers, out uint key)
    {
        modifiers = 0;
        key = 0;
        foreach (string part in shortcut.Split('+', StringSplitOptions.TrimEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl": modifiers |= MOD_CONTROL; break;
                case "alt": modifiers |= MOD_ALT; break;
                case "shift": modifiers |= MOD_SHIFT; break;
                case "win": modifiers |= MOD_WIN; break;
                case "space": key = VK_SPACE; break;
                case { Length: 1 } single when char.IsAsciiLetterOrDigit(single[0]): key = char.ToUpperInvariant(single[0]); break;
                default: return false;
            }
        }
        return modifiers != 0 && key != 0;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
