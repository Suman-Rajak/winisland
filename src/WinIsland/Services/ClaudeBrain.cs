using System.Globalization;
using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;

namespace WinIsland.Services;

/// <summary>
/// Anthropic's Claude Haiku 5.5 (its fastest model) through Anthropic's SDK, with your own API key from
/// console.anthropic.com. Paid, but a spoken question costs a fraction of a cent, and a paid key isn't
/// turned away as "busy" the way Gemini's free tier is, so answers come back quickly every time.
/// Drafts come back as tool calls, as with Gemini; lookups (the weather, the web) are tool calls answered
/// here from the free sources in <see cref="WebLookups"/>, in the same conversation.
/// Claude can't hear audio: a spoken request's words, and its accent, come from Gemini when there's a key
/// for it (else Deepgram or this PC hears it).
/// </summary>
internal sealed class ClaudeBrain(string apiKey) : IBrain
{
    public const string KeyName = "AnthropicApiKey";

    private const string Model = "claude-haiku-5-5";
    private const int MostLookupRounds = 3;

    private static AnthropicClient? s_client;
    private static string? s_clientKey;

    public string Name => "Claude";

    /// <summary>Gemini hears spoken requests for it, when there's a Gemini key.</summary>
    public bool CanHear => Listener is not null;

    private static GeminiBrain? Listener => SecretStore.Get(GeminiBrain.KeyName) is { Length: > 0 } key ? new GeminiBrain(key) : null;

    /// <summary>One client per key, kept: it holds the connection open between questions.</summary>
    private AnthropicClient Client
    {
        get
        {
            if (s_client is null || s_clientKey != apiKey)
            {
                s_client = new AnthropicClient
                {
                    ApiKey = apiKey,
                    Timeout = TimeSpan.FromSeconds(20), // (you're waiting with the island open)
                    MaxRetries = 1,
                };
                s_clientKey = apiKey;
            }
            return s_client;
        }
    }

    public async Task<BrainResult> AskAsync(string request, string? copiedText, IReadOnlyList<ConversationTurn> conversation, CancellationToken cancellation)
    {
        string prompt = copiedText is null
            ? request
            : $"{request}\n\nThe copied text:\n\"\"\"\n{copiedText}\n\"\"\"";

        List<MessageParam> messages = [];
        foreach (var turn in conversation)
        {
            messages.Add(new() { Role = Role.User, Content = turn.User });
            messages.Add(new() { Role = Role.Assistant, Content = turn.Assistant });
        }
        messages.Add(new() { Role = Role.User, Content = prompt });

        // It may look things up (the weather in two places, say) before answering: a few rounds at most.
        for (int round = 0; ; round++)
        {
            var response = await CreateAsync(messages, cancellation, withTools: round < MostLookupRounds);
            if (response.StopReason == "refusal")
            {
                Log.Info($"Claude declined{(response.StopDetails is { } details ? $" ({details.Category})" : "")}");
                return new BrainAnswer("Sorry, I can't help with that one.");
            }

            List<ContentBlockParam> said = [];
            List<ToolUseBlock> lookups = [];
            var text = new System.Text.StringBuilder();
            foreach (var block in response.Content)
            {
                if (block.TryPickText(out TextBlock? t))
                {
                    text.Append(t.Text);
                    said.Add(new TextBlockParam { Text = t.Text });
                }
                else if (block.TryPickThinking(out ThinkingBlock? thinking))
                {
                    said.Add(new ThinkingBlockParam { Thinking = thinking.Thinking, Signature = thinking.Signature });
                }
                else if (block.TryPickRedactedThinking(out RedactedThinkingBlock? redacted))
                {
                    said.Add(new RedactedThinkingBlockParam { Data = redacted.Data });
                }
                else if (block.TryPickToolUse(out ToolUseBlock? tool))
                {
                    // A draft is the answer; lookups are done here and handed back.
                    switch (tool.Name)
                    {
                        case "draft_email":
                            return new EmailDraft(Arg(tool, "to"), Arg(tool, "recipient_name"), Arg(tool, "subject"), Arg(tool, "body"));
                        case "draft_whatsapp":
                            return new WhatsAppDraft(Arg(tool, "recipient_name"), new string(Arg(tool, "phone").Where(char.IsDigit).ToArray()), Arg(tool, "message"));
                        case "set_clipboard":
                            return new ClipboardResult(Arg(tool, "text"), Arg(tool, "summary"));
                    }
                    said.Add(new ToolUseBlockParam { ID = tool.ID, Name = tool.Name, Input = tool.Input });
                    lookups.Add(tool);
                }
            }

            if (lookups.Count == 0)
            {
                string answer = text.ToString().Replace("**", "").Replace("*", "").Trim();
                return new BrainAnswer(answer.Length > 0 ? answer : "I don't have an answer for that.");
            }

            var found = await Task.WhenAll(lookups.Select(tool => LookUpAsync(tool, request, cancellation)));
            Log.Info($"Looked up {string.Join(", ", lookups.Select(l => l.Name))} for Claude");
            List<ContentBlockParam> results = [];
            for (int i = 0; i < lookups.Count; i++)
            {
                results.Add(new ToolResultBlockParam { ToolUseID = lookups[i].ID, Content = found[i] });
            }
            messages.Add(new() { Role = Role.Assistant, Content = said });
            messages.Add(new() { Role = Role.User, Content = results });
        }
    }

    public Task<string> TranscribeAsync(float[] samples, CancellationToken cancellation) =>
        Listener?.TranscribeAsync(samples, cancellation)
        ?? throw new BrainException("Claude can't hear audio; add a Gemini key for that.");

    public Task<SpokenAccent?> AccentOfAsync(float[] samples, CancellationToken cancellation) =>
        Listener?.AccentOfAsync(samples, cancellation) ?? Task.FromResult<SpokenAccent?>(null);

    /// <summary>One request, its errors put plainly.</summary>
    /// <param name="withTools">Offer the tools (else it must answer from what it has).</param>
    private async Task<Message> CreateAsync(List<MessageParam> messages, CancellationToken cancellation, bool withTools)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var response = await Client.Messages.Create(new MessageCreateParams
            {
                Model = Model,
                MaxTokens = 2048, // (room for a little thinking before a short answer or a draft)
                System = SystemPrompt(),
                OutputConfig = new OutputConfig { Effort = Effort.Low }, // quick: it thinks only when a question needs it
                Tools = withTools ? s_tools : null,
                Messages = messages,
            }, cancellation);
            Log.Info($"Claude answered in {watch.ElapsedMilliseconds} ms ({response.Usage.InputTokens} tokens in, {response.Usage.OutputTokens} out)");
            return response;
        }
        catch (AnthropicUnauthorizedException ex)
        {
            throw new BrainException("Claude doesn't accept the API key. Check it in Settings.", ex);
        }
        catch (AnthropicForbiddenException ex)
        {
            throw new BrainException("This Anthropic key isn't allowed to use Claude. Check it in Settings.", ex);
        }
        catch (AnthropicRateLimitException ex)
        {
            throw new BrainException("Claude is getting too many questions at once. Try again in a moment.", ex);
        }
        catch (AnthropicBadRequestException ex) when (ex.Message.Contains("credit", StringComparison.OrdinalIgnoreCase))
        {
            throw new BrainException("Your Anthropic credit has run out. Add some at console.anthropic.com.", ex);
        }
        catch (Anthropic5xxException ex)
        {
            throw new BrainException("Claude is having a problem right now. Try again in a minute.", ex);
        }
        catch (AnthropicIOException ex)
        {
            throw new BrainException("I can't reach Claude. Check the internet connection.", ex);
        }
        catch (AnthropicApiException ex)
        {
            Log.Info($"Claude said: {ex.Message}");
            throw new BrainException("Claude had a problem with that. Try again.", ex);
        }
    }

    /// <summary>What the model asked to look up: the weather or the web, from the free sources.</summary>
    private static Task<string> LookUpAsync(ToolUseBlock tool, string request, CancellationToken cancellation) => tool.Name switch
    {
        "get_weather" when Number(tool, "latitude") is { } latitude && Number(tool, "longitude") is { } longitude =>
            WebLookups.WeatherAsync(Arg(tool, "place") is { Length: > 0 } place ? place : "there", latitude, longitude, cancellation),
        "search_web" => WebLookups.SearchAsync(Arg(tool, "query") is { Length: > 0 } query ? query : request, cancellation),
        _ => Task.FromResult($"(Couldn't look up {tool.Name}.)"),
    };

    private static string Arg(ToolUseBlock tool, string name) =>
        tool.Input.TryGetValue(name, out var value)
            ? (value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText())?.Trim() ?? ""
            : "";

    private static double? Number(ToolUseBlock tool, string name) =>
        tool.Input.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble()
        : double.TryParse(Arg(tool, name), CultureInfo.InvariantCulture, out double parsed) ? parsed
        : null;

    private static string SystemPrompt() =>
        $"You are {Assistant.Name}, a voice assistant on the user's Windows PC, shown on a small \"island\" at the top of the screen. " +
        "Your answers are shown there and read aloud, so answer in one to three short sentences of plain text: no markdown, lists, links or emoji. " +
        "Reply in the language the user spoke (usually English or Hindi; Hinglish is fine). " +
        "For emails, WhatsApp messages and changes to copied text, use the tools: the user reviews and sends everything themselves, " +
        "so never say you've sent anything. For the weather use get_weather, and for anything current or that you're unsure of, " +
        "search_web; answer from what's found, with the figures that matter (temperatures in °C, scores, prices). " +
        "If you can't do something (control other apps, see the screen), say so briefly. " +
        $"It's {DateTime.Now.ToString("dddd d MMMM yyyy, h:mm tt", CultureInfo.InvariantCulture)}; the user is in India" +
        $" ({TimeZoneInfo.Local.DisplayName}).";

    private static readonly List<ToolUnion> s_tools =
    [
        Tool("draft_email",
            "Write an email for the user to review. Use this whenever they ask to write, send or reply to an email. The user sends it themselves.",
            ("to", "string", "The recipient's email address, only if the user said one; otherwise empty."),
            ("recipient_name", "string", "Who it's for, as the user called them (\"Rahul\", \"my manager\"); may be empty."),
            ("subject", "string", "A short subject line."),
            ("body", "string", "The full email body, ready to send, signed off without a name.")),
        Tool("draft_whatsapp",
            "Write a WhatsApp message for the user to review. Use this whenever they ask to message, text or WhatsApp someone. The user sends it themselves.",
            ("recipient_name", "string", "Who it's for, as the user called them; may be empty."),
            ("phone", "string", "Their phone number with country code, digits only, only if the user said one; otherwise empty."),
            ("message", "string", "The message, ready to send, in the user's tone and language.")),
        Tool("set_clipboard",
            "Replace the user's copied text with a new version: translated, summarised, rewritten, proofread, a reply to it… " +
            "Use this whenever the request is about the copied text.",
            ("text", "string", "The new text, complete."),
            ("summary", "string", "What you did, in a few words: \"Translated to Hindi\", \"Summarised\".")),
        Tool("get_weather",
            "Get the weather now and the forecast for the next few days at a place. Use this for any question about weather, " +
            "temperature or rain, once per place (twice to compare two).",
            ("place", "string", "The place as people call it: \"Kolkata\"."),
            ("latitude", "number", "Its latitude in degrees, north positive: 22.57."),
            ("longitude", "number", "Its longitude in degrees, east positive: 88.36.")),
        Tool("search_web",
            "Look something up on the web. Use this for anything current or changing (news, sports results, prices, " +
            "exchange rates, recent events) and for facts you're unsure of, rather than saying you can't browse.",
            ("query", "string", "What to search for, as you'd type it into a search engine.")),
    ];

    private static Tool Tool(string name, string description, params (string Name, string Type, string Description)[] parameters) => new()
    {
        Name = name,
        Description = description,
        InputSchema = new()
        {
            Properties = parameters.ToDictionary(
                p => p.Name,
                p => JsonSerializer.SerializeToElement(new { type = p.Type, description = p.Description })),
            Required = [.. parameters.Select(p => p.Name)],
        },
    };
}
