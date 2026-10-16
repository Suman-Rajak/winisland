namespace WinIsland.Services;

/// <summary>What the brain came back with: an answer to say, or something for you to look at first.</summary>
internal abstract record BrainResult;

internal sealed record BrainAnswer(string Text) : BrainResult;

/// <param name="To">An email address, only if you said one; else empty.</param>
internal sealed record EmailDraft(string To, string RecipientName, string Subject, string Body) : BrainResult;

/// <param name="Phone">A phone number with country code, only if you said one; else empty (WhatsApp lets you pick the chat).</param>
internal sealed record WhatsAppDraft(string RecipientName, string Phone, string Message) : BrainResult;

/// <summary>New text for the clipboard (copied text translated, summarised, rewritten…).</summary>
/// <param name="Summary">What was done, briefly: "Translated to Hindi".</param>
internal sealed record ClipboardResult(string Text, string Summary) : BrainResult;

/// <summary>One exchange of a conversation: what you asked, and what the assistant said or did.</summary>
internal sealed record ConversationTurn(string User, string Assistant);

/// <summary>Something went wrong, with a message fit to show you ("Gemini's free limit is used up for now").</summary>
internal sealed class BrainException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// The assistant's brain: an AI model for what the local commands can't do (questions, Hindi, emails,
/// WhatsApp messages, copied text). It never acts by itself: drafts come back to be confirmed.
/// </summary>
internal interface IBrain
{
    /// <summary>"Gemini", for messages.</summary>
    string Name { get; }

    /// <param name="request">What you said, without the wake phrase.</param>
    /// <param name="copiedText">The clipboard's text, when the request is about it ("translate this"); else null.</param>
    /// <param name="conversation">The exchanges just before, oldest first, so a follow-up ("and tomorrow?") makes sense.</param>
    Task<BrainResult> AskAsync(string request, string? copiedText, IReadOnlyList<ConversationTurn> conversation, CancellationToken cancellation);

    /// <summary>
    /// The words of a spoken request (16 kHz mono audio), for when the model understands speech better
    /// than the PC's own speech recognition. Empty if nothing was said.
    /// </summary>
    Task<string> TranscribeAsync(float[] samples, CancellationToken cancellation);

    /// <summary>
    /// Which accent of English a spoken request (16 kHz mono audio) is said in, so the reply can be said in
    /// the same one; null when it can't tell.
    /// </summary>
    Task<SpokenAccent?> AccentOfAsync(float[] samples, CancellationToken cancellation);
}

internal static class Brains
{
    /// <summary>The brain chosen in Settings, or null if there's none (no key yet, or turned off).</summary>
    public static IBrain? Current => AppSettings.Current.AiProvider == AiProvider.Gemini && SecretStore.Get(GeminiBrain.KeyName) is { Length: > 0 } key
        ? new GeminiBrain(key)
        : null;

    /// <summary>Asks for a changed version of a draft: "make it shorter", "add that I'll bring snacks".</summary>
    public static Task<BrainResult> ReviseAsync(IBrain brain, BrainResult draft, string change, CancellationToken cancellation)
    {
        string described = draft switch
        {
            EmailDraft e => $"an email to {Recipient(e.RecipientName, e.To)} with the subject \"{e.Subject}\" and this body:\n{e.Body}",
            WhatsAppDraft w => $"a WhatsApp message to {Recipient(w.RecipientName, w.Phone)}:\n{w.Message}",
            ClipboardResult c => $"this text (\"{c.Summary}\"):\n{c.Text}",
            _ => "an answer",
        };
        return brain.AskAsync($"You drafted {described}\n\nChange it as follows, and give the whole new version with the same tool: {change}", null, [], cancellation);
    }

    private static string Recipient(string name, string address) =>
        name.Length > 0 && address.Length > 0 ? $"{name} ({address})" : name.Length > 0 ? name : address.Length > 0 ? address : "someone";
}
