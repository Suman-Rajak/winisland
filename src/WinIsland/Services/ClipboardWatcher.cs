using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.UI.Dispatching;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;

namespace WinIsland.Services;

internal enum ClipKind
{
    Text,
    Link,
    Image,
    Files,
}

/// <summary>Something that was copied. Exactly one of Text, ImageBytes or FilePaths is set, by kind.</summary>
/// <param name="Key">Identifies the content, so copying the same thing again isn't a new entry.</param>
internal sealed record Clip(ClipKind Kind, string Key, string? Text, byte[]? ImageBytes, IReadOnlyList<string>? FilePaths);

/// <summary>
/// Tells us whenever anything is copied, through a hidden message-only window registered as a
/// clipboard format listener, and reads what was copied. Content that password managers and similar
/// apps mark as private (the same markers Windows' own clipboard history respects) is skipped.
/// Call <see cref="Start"/> on the UI thread; <see cref="Copied"/> is raised there.
/// </summary>
internal sealed class ClipboardWatcher
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;
    private static readonly IntPtr HWND_MESSAGE = new(-3);
    private const long MaxImageBytes = 20 * 1024 * 1024;

    private readonly DispatcherQueue _dispatcher;
    private readonly WndProc _wndProc; // kept alive: Windows holds a pointer to it
    private IntPtr _window;
    private bool _readQueued;
    private DateTime _ignoreUntil;

    public ClipboardWatcher(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        _wndProc = OnMessage;
    }

    public event Action<Clip>? Copied;

    /// <summary>
    /// Ignore clipboard changes for a moment: it's us putting an old entry back. (Waiting for "the next
    /// change" instead could swallow a real copy if ours never produced one.)
    /// </summary>
    public void IgnoreChangesFor(TimeSpan duration) => _ignoreUntil = DateTime.UtcNow + duration;

    public void Start()
    {
        var windowClass = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = GetModuleHandle(null),
            lpszClassName = "WinIsland.ClipboardWatcher",
        };
        RegisterClassEx(ref windowClass);
        _window = CreateWindowEx(0, windowClass.lpszClassName, "", 0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, windowClass.hInstance, IntPtr.Zero);
        if (_window == IntPtr.Zero || !AddClipboardFormatListener(_window))
        {
            throw new InvalidOperationException($"Couldn't listen to the clipboard (error {Marshal.GetLastWin32Error()})");
        }
    }

    private IntPtr OnMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        // Apps often set the clipboard several times in a row; read once they're done.
        if (message == WM_CLIPBOARDUPDATE && !_readQueued)
        {
            _readQueued = true;
            _dispatcher.TryEnqueue(DispatcherQueuePriority.Low, async () =>
            {
                await Task.Delay(100);
                _readQueued = false;
                await ReadAsync();
            });
        }
        return DefWindowProc(hwnd, message, wParam, lParam);
    }

    private async Task ReadAsync()
    {
        if (DateTime.UtcNow < _ignoreUntil)
        {
            return;
        }

        try
        {
            var content = Clipboard.GetContent();
            if (await IsPrivateAsync(content))
            {
                return;
            }

            Clip? clip = null;
            if (content.Contains(StandardDataFormats.StorageItems))
            {
                var paths = (await content.GetStorageItemsAsync()).Select(i => i.Path).Where(p => p.Length > 0).ToList();
                if (paths.Count > 0)
                {
                    clip = new Clip(ClipKind.Files, "files:" + string.Join("|", paths), null, null, paths);
                }
            }
            else if (content.Contains(StandardDataFormats.Bitmap))
            {
                var bytes = await ReadAllAsync(await content.GetBitmapAsync());
                if (bytes is not null)
                {
                    clip = new Clip(ClipKind.Image, "image:" + Convert.ToHexString(SHA1.HashData(bytes)), null, bytes, null);
                }
            }
            else if (content.Contains(StandardDataFormats.Text))
            {
                string text = await content.GetTextAsync();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    bool link = Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
                    clip = new Clip(link ? ClipKind.Link : ClipKind.Text, "text:" + text, text, null, null);
                }
            }

            if (clip is not null)
            {
                Copied?.Invoke(clip);
            }
        }
        catch (Exception ex)
        {
            // The clipboard can be busy (another app holding it open) or hold delay-rendered data that fails.
            Log.Error("Reading the clipboard failed", ex);
        }
    }

    /// <summary>
    /// Password managers mark what they copy as private: "ExcludeClipboardContentFromMonitorProcessing",
    /// "Clipboard Viewer Ignore", or "CanIncludeInClipboardHistory" set to 0.
    /// </summary>
    private static async Task<bool> IsPrivateAsync(DataPackageView content)
    {
        var formats = content.AvailableFormats;
        if (formats.Contains("ExcludeClipboardContentFromMonitorProcessing") || formats.Contains("Clipboard Viewer Ignore"))
        {
            return true;
        }

        if (!formats.Contains("CanIncludeInClipboardHistory"))
        {
            return false;
        }

        try
        {
            // A DWORD; anything but a readable non-zero value counts as "don't keep this".
            if (await content.GetDataAsync("CanIncludeInClipboardHistory") is IRandomAccessStream stream)
            {
                using var reader = new DataReader(stream.GetInputStreamAt(0)) { ByteOrder = ByteOrder.LittleEndian };
                return await reader.LoadAsync(4) < 4 || reader.ReadUInt32() == 0;
            }
        }
        catch
        {
            // fall through
        }
        return true;
    }

    private static async Task<byte[]?> ReadAllAsync(RandomAccessStreamReference reference)
    {
        using var stream = await reference.OpenReadAsync();
        if (stream.Size == 0 || stream.Size > (ulong)MaxImageBytes)
        {
            return null; // nothing, or too big to keep in memory
        }

        using var reader = new DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync((uint)stream.Size);
        var bytes = new byte[stream.Size];
        reader.ReadBytes(bytes);
        return bytes;
    }

    // ---------------------------------------------------------------- Win32

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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
