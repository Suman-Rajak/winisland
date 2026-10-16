using System.Diagnostics;
using Windows.ApplicationModel.DataTransfer;

namespace WinIsland.Services;

/// <summary>
/// Hands a confirmed draft to the app that sends it: Gmail's compose window in the browser, or WhatsApp
/// with the message typed in. You press Send there; nothing is sent from WinIsland. Copied-text results
/// go back on the clipboard. Call on the UI thread.
/// </summary>
internal static class Drafts
{
    public static void Open(BrainResult draft)
    {
        switch (draft)
        {
            case EmailDraft email:
                OpenUrl("https://mail.google.com/mail/?view=cm&fs=1" +
                        $"&to={Uri.EscapeDataString(email.To)}&su={Uri.EscapeDataString(email.Subject)}&body={Uri.EscapeDataString(email.Body)}");
                break;

            case WhatsAppDraft whatsApp:
                // The WhatsApp app if it's installed (it asks which chat when there's no number), else WhatsApp on the web.
                string query = (whatsApp.Phone.Length > 0 ? $"phone={whatsApp.Phone}&" : "") + $"text={Uri.EscapeDataString(whatsApp.Message)}";
                if (!TryOpen($"whatsapp://send?{query}"))
                {
                    OpenUrl(whatsApp.Phone.Length > 0
                        ? $"https://wa.me/{whatsApp.Phone}?text={Uri.EscapeDataString(whatsApp.Message)}"
                        : $"https://wa.me/?text={Uri.EscapeDataString(whatsApp.Message)}");
                }
                break;

            case ClipboardResult copied:
                var package = new DataPackage();
                package.SetText(copied.Text);
                Clipboard.SetContent(package);
                break;
        }
    }

    /// <summary>The clipboard's text, if it holds text (up to a limit, it goes to the AI model).</summary>
    public static async Task<string?> CopiedTextAsync()
    {
        const int MaxLength = 12_000;
        try
        {
            var content = Clipboard.GetContent();
            if (!content.Contains(StandardDataFormats.Text))
            {
                return null;
            }
            string text = await content.GetTextAsync();
            return string.IsNullOrWhiteSpace(text) ? null : text.Length > MaxLength ? text[..MaxLength] : text;
        }
        catch (Exception ex)
        {
            Log.Error("Reading the clipboard failed", ex);
            return null;
        }
    }

    private static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    private static bool TryOpen(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false; // nothing handles whatsapp:// here
        }
    }
}
