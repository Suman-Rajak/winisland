using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WinIsland.Services;

/// <summary>
/// Google's Gemini (a "Flash" model: fast, and on Google's free tier) through its REST API, with your
/// own API key from aistudio.google.com. Drafts come back as function calls, so they're structured
/// (to, subject, body…) and never sent by the model. It can also search the web (Google Search, asked
/// for with a function call) and turn spoken requests into text (with the quicker Flash-Lite).
/// </summary>
internal sealed class GeminiBrain(string apiKey) : IBrain
{
    public const string KeyName = "GeminiApiKey";

    private const string Endpoint = "https://generativelanguage.googleapis.com/v1beta/";
    // Google's alias for its newest Flash-Lite model. Flash itself writes a little better, but on the free
    // tier it was overloaded for hours at a time (8 s waits, then "busy"), while Flash-Lite kept answering.
    private const string DefaultModel = "gemini-flash-lite-latest";

    private static readonly HttpClient s_http = new() { Timeout = TimeSpan.FromSeconds(40) };

    private static readonly JsonArray s_tools =
    [
        new JsonObject
        {
            ["function_declarations"] = new JsonArray
            {
                Function("draft_email",
                    "Write an email for the user to review. Use this whenever they ask to write, send or reply to an email. The user sends it themselves.",
                    ("to", "The recipient's email address, only if the user said one; otherwise empty."),
                    ("recipient_name", "Who it's for, as the user called them (\"Rahul\", \"my manager\"); may be empty."),
                    ("subject", "A short subject line."),
                    ("body", "The full email body, ready to send, signed off without a name.")),
                Function("draft_whatsapp",
                    "Write a WhatsApp message for the user to review. Use this whenever they ask to message, text or WhatsApp someone. The user sends it themselves.",
                    ("recipient_name", "Who it's for, as the user called them; may be empty."),
                    ("phone", "Their phone number with country code, digits only, only if the user said one; otherwise empty."),
                    ("message", "The message, ready to send, in the user's tone and language.")),
                Function("set_clipboard",
                    "Replace the user's copied text with a new version: translated, summarised, rewritten, proofread, a reply to it… " +
                    "Use this whenever the request is about the copied text.",
                    ("text", "The new text, complete."),
                    ("summary", "What you did, in a few words: \"Translated to Hindi\", \"Summarised\".")),
                Function("get_weather",
                    "Get the weather now and the forecast for the next few days at a place. Use this for any question about weather, " +
                    "temperature or rain, once per place (twice to compare two).",
                    ("place", "The place as people call it: \"Kolkata\"."),
                    ("latitude", "Its latitude in degrees, north positive: \"22.57\"."),
                    ("longitude", "Its longitude in degrees, east positive: \"88.36\".")),
                Function("search_web",
                    "Look something up on the web. Use this for anything current or changing (news, sports results, prices, " +
                    "exchange rates, recent events) and for facts you're unsure of, rather than saying you can't browse.",
                    ("query", "What to search for, as you'd type it into a search engine.")),
            },
        },
    ];

    private const string FastModel = "gemini-flash-lite-latest"; // Google's alias for its newest Flash-Lite: quickest to answer
    private static readonly TimeSpan StandInFor = TimeSpan.FromHours(1); // the usual one was busy for hours at a time

    /// <summary>
    /// The model one kind of request goes to: its usual one, or, for a while after that was busy, the one
    /// that answered instead (rather than waiting on the busy one every time).
    /// </summary>
    private sealed class Lane(string name, Func<string> usual)
    {
        private string? _model;
        private DateTime _standInSince;

        public string Name => name;

        public string Model
        {
            get
            {
                string model = usual();
                if (_model is null || (_model != model && DateTime.UtcNow - _standInSince > StandInFor))
                {
                    _model = model;
                }
                return _model;
            }
        }

        public void StandIn(string model)
        {
            Log.Info($"Using Gemini {model} for {name} for now");
            _model = model;
            _standInSince = DateTime.UtcNow;
        }
    }

    private static readonly Lane s_answers = new("answers", () => AppSettings.Current.GeminiModel ?? DefaultModel);
    private static readonly Lane s_speech = new("speech", () => FastModel); // you're waiting to see what it heard
    private static readonly Lane s_search = new("web searches", () => AppSettings.Current.GeminiModel ?? DefaultModel);

    /// <summary>Gemini asked to look things up (the weather, the web): done here, never returned.</summary>
    private sealed record Lookups(IReadOnlyList<(string Name, Dictionary<string, string> Args)> Calls) : BrainResult;

    /// <summary>Until when Gemini's own Google Search is taken not to work with this key (it needs billing on).</summary>
    private static DateTime s_noGoogleSearchUntil;

    public string Name => "Gemini";

    public bool CanHear => true;

    public async Task<BrainResult> AskAsync(string request, string? copiedText, IReadOnlyList<ConversationTurn> conversation, CancellationToken cancellation)
    {
        string prompt = copiedText is null
            ? request
            : $"{request}\n\nThe copied text:\n\"\"\"\n{copiedText}\n\"\"\"";

        var body = new JsonObject
        {
            ["system_instruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = SystemPrompt() }) },
            ["contents"] = Contents(conversation, prompt),
            ["tools"] = s_tools.DeepClone(),
            ["generationConfig"] = new JsonObject { ["temperature"] = 0.6, ["maxOutputTokens"] = 2048 },
        };

        var result = Read(await PostAsync(body, s_answers, cancellation));
        return result is Lookups lookups ? await AnswerWithLookupsAsync(request, lookups, conversation, cancellation) : result;
    }

    /// <summary>The conversation so far (each exchange as you asking and the model answering), then the new message.</summary>
    private static JsonArray Contents(IReadOnlyList<ConversationTurn> conversation, string message)
    {
        var contents = new JsonArray();
        foreach (var turn in conversation)
        {
            contents.Add(new JsonObject { ["role"] = "user", ["parts"] = new JsonArray(new JsonObject { ["text"] = turn.User }) });
            contents.Add(new JsonObject { ["role"] = "model", ["parts"] = new JsonArray(new JsonObject { ["text"] = turn.Assistant }) });
        }
        contents.Add(new JsonObject { ["role"] = "user", ["parts"] = new JsonArray(new JsonObject { ["text"] = message }) });
        return contents;
    }

    /// <summary>
    /// Looks up what Gemini asked for, then has it answer from that. A web search goes to Gemini's own
    /// Google Search when the key allows it (it needs billing on); otherwise, and for the weather, to the
    /// free sources in <see cref="WebLookups"/>.
    /// </summary>
    private async Task<BrainResult> AnswerWithLookupsAsync(string request, Lookups lookups, IReadOnlyList<ConversationTurn> conversation, CancellationToken cancellation)
    {
        if (lookups.Calls.FirstOrDefault(c => c.Name == "search_web") is { Name: "search_web", Args: var search } && DateTime.UtcNow > s_noGoogleSearchUntil)
        {
            try
            {
                return await GoogleSearchAsync(request, search.GetValueOrDefault("query", request), conversation, cancellation);
            }
            catch (BrainException ex)
            {
                // Usually it's the key (no billing), which doesn't change by itself: ask again tomorrow
                // (or when Glyde restarts, say after billing's turned on).
                Log.Info($"Gemini's Google Search isn't available ({ex.Message}); using the free sources for a day");
                s_noGoogleSearchUntil = DateTime.UtcNow.AddDays(1);
            }
        }

        var found = await Task.WhenAll(lookups.Calls.Select(call => call.Name switch
        {
            "get_weather" when double.TryParse(call.Args.GetValueOrDefault("latitude"), CultureInfo.InvariantCulture, out double latitude)
                            && double.TryParse(call.Args.GetValueOrDefault("longitude"), CultureInfo.InvariantCulture, out double longitude) =>
                WebLookups.WeatherAsync(call.Args.GetValueOrDefault("place", "there"), latitude, longitude, cancellation),
            "search_web" => WebLookups.SearchAsync(call.Args.GetValueOrDefault("query", request), cancellation),
            _ => Task.FromResult($"(Couldn't look up {call.Name}.)"),
        }));
        Log.Info($"Looked up {string.Join(", ", lookups.Calls.Select(c => c.Name))} for Gemini");

        var body = new JsonObject
        {
            ["system_instruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = SystemPrompt(searching: true) }) },
            ["contents"] = Contents(conversation, $"{request}\n\nWhat {Assistant.Name} looked up for this:\n\n{string.Join("\n\n", found)}"),
            ["generationConfig"] = new JsonObject { ["temperature"] = 0.4, ["maxOutputTokens"] = 1024 },
        };
        using var response = await PostAsync(body, s_answers, cancellation);
        string answer = TextOf(response).Replace("**", "").Replace("*", "").Trim();
        return new BrainAnswer(answer.Length > 0 ? answer : "I couldn't find that.");
    }

    /// <summary>
    /// Answers with Gemini's own Google Search: it searches, reads the results and answers, in one
    /// request. Asked on its own (not with the draft tools: older models can't mix the two). Google may
    /// not let a key search with every model, so a refusal moves on to the others.
    /// </summary>
    private async Task<BrainResult> GoogleSearchAsync(string request, string query, IReadOnlyList<ConversationTurn> conversation, CancellationToken cancellation)
    {
        Log.Info("Gemini is searching the web");
        var body = new JsonObject
        {
            ["system_instruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = SystemPrompt(searching: true) }) },
            ["contents"] = Contents(conversation, $"{request}\n\n(Search for: {query})"),
            ["tools"] = new JsonArray(new JsonObject { ["google_search"] = new JsonObject() }),
            ["generationConfig"] = new JsonObject { ["temperature"] = 0.4, ["maxOutputTokens"] = 1024 },
        };

        using var response = await PostAsync(body, s_search, cancellation, searching: true);
        string answer = TextOf(response).Replace("**", "").Replace("*", "").Trim();
        return new BrainAnswer(answer.Length > 0 ? answer : "I couldn't find that.");
    }

    public async Task<string> TranscribeAsync(float[] samples, CancellationToken cancellation)
    {
        var body = new JsonObject
        {
            ["system_instruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = TranscriptionPrompt }) },
            ["contents"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["parts"] = new JsonArray(
                    new JsonObject { ["inline_data"] = new JsonObject { ["mime_type"] = "audio/wav", ["data"] = Convert.ToBase64String(ToWav(samples)) } },
                    new JsonObject { ["text"] = "Transcribe this." }),
            }),
            ["generationConfig"] = new JsonObject { ["temperature"] = 0, ["maxOutputTokens"] = 400 },
        };

        using var response = await PostAsync(body, s_speech, cancellation);

        // Asked for nothing but the words; anything in brackets is a note about the sound ("[music]").
        string heard = System.Text.RegularExpressions.Regex.Replace(TextOf(response), @"\[[^\]]*\]|\([^)]*\)", " ");
        return System.Text.RegularExpressions.Regex.Replace(heard, @"\s+", " ").Trim().Trim('"', '“', '”').Trim();
    }

    public async Task<SpokenAccent?> AccentOfAsync(float[] samples, CancellationToken cancellation)
    {
        // The first few seconds are plenty to tell, and keep the upload small.
        const int Plenty = MicrophoneRecorder.SampleRate * 6;
        if (samples.Length > Plenty)
        {
            samples = samples[..Plenty];
        }

        var body = new JsonObject
        {
            ["system_instruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = AccentPrompt }) },
            ["contents"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["parts"] = new JsonArray(
                    new JsonObject { ["inline_data"] = new JsonObject { ["mime_type"] = "audio/wav", ["data"] = Convert.ToBase64String(ToWav(samples)) } },
                    new JsonObject { ["text"] = "Which accent?" }),
            }),
            ["generationConfig"] = new JsonObject { ["temperature"] = 0, ["maxOutputTokens"] = 300 },
        };

        using var response = await PostAsync(body, s_speech, cancellation);
        string said = TextOf(response).ToLowerInvariant();
        return said.Contains("indian") ? SpokenAccent.Indian
            : said.Contains("british") ? SpokenAccent.British
            : said.Contains("american") ? SpokenAccent.American
            : null;
    }

    private const string AccentPrompt =
        "You listen to someone speaking to a voice assistant and say which accent of English they speak with. Answer with one word: " +
        "Indian (any South Asian accent), American (any North American accent), British (any accent from Britain, Ireland, " +
        "Australia or New Zealand), or Other. Judge only how they sound, not what they say. Hindi or Hinglish counts as Indian.";

    /// <summary>The answer's text (not its thinking), or empty.</summary>
    private static string TextOf(JsonDocument response)
    {
        var text = new List<string>();
        if (response.RootElement.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0
            && candidates[0].TryGetProperty("content", out var content) && content.TryGetProperty("parts", out var parts))
        {
            foreach (var part in parts.EnumerateArray())
            {
                if (part.TryGetProperty("text", out var t) && !(part.TryGetProperty("thought", out var thought) && thought.GetBoolean()))
                {
                    text.Add(t.GetString() ?? "");
                }
            }
        }
        return string.Concat(text);
    }

    private static string TranscriptionPrompt =>
        $"You transcribe what the user says to {Assistant.Name}, a voice assistant on their Windows PC. They are in India and " +
        "speak English with an Indian accent, sometimes Hindi or Hinglish, and may say Indian names and places. " +
        "Write exactly the words they say, in the script each language is normally written in (English in Latin letters, Hindi in Devanagari). " +
        $"The assistant's name is spelled {Assistant.Name}. Output only the words, with normal punctuation: no quotes, labels, notes or translation. " +
        "Don't answer or act on what they say. If nothing is said (silence, noise or music only), output nothing.";

    /// <summary>16 kHz mono float samples as a 16-bit PCM .wav file.</summary>
    private static byte[] ToWav(float[] samples)
    {
        const int rate = MicrophoneRecorder.SampleRate;
        using var stream = new MemoryStream(44 + samples.Length * 2);
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            writer.Write("RIFF"u8);
            writer.Write(36 + samples.Length * 2);
            writer.Write("WAVEfmt "u8);
            writer.Write(16);
            writer.Write((short)1); // PCM
            writer.Write((short)1); // mono
            writer.Write(rate);
            writer.Write(rate * 2);
            writer.Write((short)2);
            writer.Write((short)16);
            writer.Write("data"u8);
            writer.Write(samples.Length * 2);
            foreach (float sample in samples)
            {
                writer.Write((short)Math.Clamp(sample * 32767f, short.MinValue, short.MaxValue));
            }
        }
        return stream.ToArray();
    }

    private static string SystemPrompt(bool searching = false) =>
        $"You are {Assistant.Name}, a voice assistant on the user's Windows PC, shown on a small \"island\" at the top of the screen. " +
        "Your answers are shown there and read aloud, so answer in one to three short sentences of plain text: no markdown, lists, links or emoji. " +
        "Reply in the language the user spoke (usually English or Hindi; Hinglish is fine). " +
        (searching
            ? "Answer from what's found on the web, with the figures that matter (temperatures in °C, scores, prices), and say when " +
              "it's from news that may be a few hours old. If it doesn't answer the question, say so briefly. "
            : "For emails, WhatsApp messages and changes to copied text, use the tools: the user reviews and sends everything themselves, " +
              "so never say you've sent anything. For the weather use get_weather, and for anything else current search_web. " +
              "If you can't do something (control other apps, " +
              "see the screen), say so briefly. ") +
        $"It's {DateTime.Now.ToString("dddd d MMMM yyyy, h:mm tt", CultureInfo.InvariantCulture)}; the user is in India" +
        $" ({TimeZoneInfo.Local.DisplayName}).";

    /// <param name="lane">Which model to ask first.</param>
    /// <param name="searching">The request searches the web, which a model may refuse (not on this key's tier): then the next one is asked.</param>
    private async Task<JsonDocument> PostAsync(JsonObject body, Lane lane, CancellationToken cancellation, bool searching = false)
    {
        string first = lane.Model;
        var response = await SendAsync(first, body, cancellation);

        // The usual model's gone (Google retires them): switch to the current Flash model for good.
        if (response.StatusCode == HttpStatusCode.NotFound && lane == s_answers
            && (await FlashModelsAsync(cancellation)).FirstOrDefault(m => m != first && !m.Contains("lite")) is { } replacement)
        {
            Log.Info($"Gemini model {first} isn't available; using {replacement}");
            first = replacement;
            AppSettings.Current.GeminiModel = replacement;
            AppSettings.Current.Save();
            response.Dispose();
            response = await SendAsync(first, body, cancellation);
        }

        // Busy ("high demand", common on the free tier), out of free quota or too slow: straight on to
        // another Flash model, which has its own capacity and its own free limit. (Trying the same one
        // again a moment later was usually busy again, and its "busy" can take 10 s to arrive.)
        bool TryAnother(HttpStatusCode status) => IsBusy(status) || status == HttpStatusCode.NotFound
            || (searching && status is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden);
        if (TryAnother(response.StatusCode))
        {
            // A copy: models found missing are dropped from the list on the way.
            var others = (await FlashModelsAsync(cancellation)).ToList();
            if (searching)
            {
                others = [.. others.OrderBy(m => !m.StartsWith("gemini-2.5", StringComparison.Ordinal))];
            }

            var tried = new HashSet<string> { first };
            string model = first;
            foreach (string other in others)
            {
                if (!TryAnother(response.StatusCode) || tried.Count > (searching ? 3 : 2))
                {
                    break;
                }
                if (tried.Add(other))
                {
                    Log.Info($"Gemini {model} {(IsBusy(response.StatusCode) ? "is busy" : "refused")} ({(int)response.StatusCode}); trying {other}");
                    if (response.StatusCode == HttpStatusCode.NotFound)
                    {
                        s_flashModels?.Remove(model); // listed, but not for this key: don't ask it again
                    }
                    model = other;
                    response.Dispose();
                    response = await SendAsync(other, body, cancellation);
                }
            }
            if (response.IsSuccessStatusCode && model != first)
            {
                lane.StandIn(model);
            }
        }

        using (response)
        {
            string json = await response.Content.ReadAsStringAsync(cancellation);
            if (!response.IsSuccessStatusCode)
            {
                throw new BrainException(Problem(response.StatusCode, json, searching));
            }
            return JsonDocument.Parse(json);
        }
    }

    /// <summary>
    /// One try with one model. Taking longer than <see cref="AttemptTimeout"/> counts as busy (a 504), so
    /// the next model gets its turn.
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(string model, JsonObject body, CancellationToken cancellation)
    {
        SetThinking(body, model);
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        attempt.CancelAfter(AttemptTimeout);
        try
        {
            var response = await PostOnceAsync(model, body, attempt.Token);

            // A model that doesn't take the thinking setting (they differ by version): without it, for good.
            if (response.StatusCode == HttpStatusCode.BadRequest && !s_thinkingRefused.Contains(model)
                && (await response.Content.ReadAsStringAsync(attempt.Token)).Contains("thinking", StringComparison.OrdinalIgnoreCase))
            {
                Log.Info($"Gemini {model} doesn't take the thinking setting; asking without it");
                s_thinkingRefused.Add(model);
                response.Dispose();
                SetThinking(body, model);
                response = await PostOnceAsync(model, body, attempt.Token);
            }
            return response;
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            Log.Info($"Gemini {model} took over {AttemptTimeout.TotalSeconds:0} s");
            return new HttpResponseMessage(HttpStatusCode.GatewayTimeout);
        }
        catch (HttpRequestException ex)
        {
            throw new BrainException("I can't reach Gemini. Check the internet connection.", ex);
        }
    }

    private async Task<HttpResponseMessage> PostOnceAsync(string model, JsonObject body, CancellationToken cancellation)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, $"{Endpoint}models/{model}:generateContent")
        {
            Content = JsonContent.Create(body),
        };
        message.Headers.Add("x-goog-api-key", apiKey);
        return await s_http.SendAsync(message, cancellation);
    }

    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(8);

    private static readonly HashSet<string> s_thinkingRefused = [];

    /// <summary>
    /// Thinking first makes Flash models slower to answer by seconds, and short spoken answers and drafts
    /// don't need much of it. Gemini 3 and later take a level ("low": 3.8 refuses "minimal"); 2.5 takes a
    /// budget (0 is off).
    /// </summary>
    private static void SetThinking(JsonObject body, string model)
    {
        var config = (JsonObject)body["generationConfig"]!;
        config.Remove("thinkingConfig");
        if (!s_thinkingRefused.Contains(model))
        {
            config["thinkingConfig"] = model.StartsWith("gemini-2", StringComparison.Ordinal)
                ? new JsonObject { ["thinkingBudget"] = 0 }
                : new JsonObject { ["thinkingLevel"] = "low" };
        }
    }

    private static bool IsBusy(HttpStatusCode status) => status is HttpStatusCode.ServiceUnavailable or HttpStatusCode.InternalServerError
        or HttpStatusCode.GatewayTimeout or HttpStatusCode.TooManyRequests;

    private static List<string>? s_flashModels;

    /// <summary>
    /// The Flash models your key can chat with, best first: the "…-latest" aliases (the newest Flash,
    /// then the newest Flash-Lite: smaller and quicker, and often free when the others are busy), then
    /// the newest, then the other "lite" ones. Asked once.
    /// </summary>
    private async Task<List<string>> FlashModelsAsync(CancellationToken cancellation)
    {
        if (s_flashModels is not null)
        {
            return s_flashModels;
        }
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Get, $"{Endpoint}models?pageSize=200");
            message.Headers.Add("x-goog-api-key", apiKey);
            using var response = await s_http.SendAsync(message, cancellation);
            using var list = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
            s_flashModels = list.RootElement.GetProperty("models").EnumerateArray()
                .Where(m => m.TryGetProperty("supportedGenerationMethods", out var methods)
                            && methods.EnumerateArray().Any(x => x.GetString() == "generateContent"))
                .Select(m => m.GetProperty("name").GetString()!.Replace("models/", ""))
                .Where(n => n.Contains("flash") && !new[] { "image", "tts", "live", "audio", "omni", "thinking", "exp", "preview" }.Any(n.Contains))
                .OrderByDescending(n => n.EndsWith("-latest"))
                .ThenBy(n => n.Contains("lite"))
                .ThenByDescending(n => n, StringComparer.Ordinal)
                .ToList();
            Log.Info($"Gemini models available: {string.Join(", ", s_flashModels)}");
            return s_flashModels;
        }
        catch (Exception ex)
        {
            Log.Error("Listing Gemini models failed", ex);
            return [];
        }
    }

    /// <summary>The first tool call (a draft), else the text.</summary>
    private static BrainResult Read(JsonDocument response)
    {
        using (response)
        {
            var root = response.RootElement;
            if (!root.TryGetProperty("candidates", out var candidates) || candidates.GetArrayLength() == 0)
            {
                throw new BrainException("Gemini wouldn't answer that.");
            }

            var candidate = candidates[0];
            if (!candidate.TryGetProperty("content", out var content) || !content.TryGetProperty("parts", out var parts))
            {
                throw new BrainException(candidate.TryGetProperty("finishReason", out var reason) && reason.GetString() == "SAFETY"
                    ? "Gemini wouldn't answer that."
                    : "Gemini didn't answer. Try again.");
            }

            var text = new List<string>();
            var lookups = new List<(string, Dictionary<string, string>)>();
            foreach (var part in parts.EnumerateArray())
            {
                if (part.TryGetProperty("functionCall", out var call))
                {
                    var args = new Dictionary<string, string>();
                    if (call.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var arg in a.EnumerateObject())
                        {
                            args[arg.Name] = (arg.Value.ValueKind == JsonValueKind.String ? arg.Value.GetString() : arg.Value.GetRawText())?.Trim() ?? "";
                        }
                    }
                    string Arg(string name) => args.GetValueOrDefault(name, "");

                    // A draft is the answer; lookups (there can be several: the weather in two places) come first.
                    switch (call.GetProperty("name").GetString())
                    {
                        case "draft_email":
                            return new EmailDraft(Arg("to"), Arg("recipient_name"), Arg("subject"), Arg("body"));
                        case "draft_whatsapp":
                            return new WhatsAppDraft(Arg("recipient_name"), new string(Arg("phone").Where(char.IsDigit).ToArray()), Arg("message"));
                        case "set_clipboard":
                            return new ClipboardResult(Arg("text"), Arg("summary"));
                        case { } name and ("get_weather" or "search_web"):
                            lookups.Add((name, args));
                            break;
                    }
                }
                if (part.TryGetProperty("text", out var t) && !(part.TryGetProperty("thought", out var thought) && thought.GetBoolean()))
                {
                    text.Add(t.GetString() ?? "");
                }
            }
            if (lookups.Count > 0)
            {
                return new Lookups(lookups);
            }

            string answer = string.Concat(text).Replace("**", "").Replace("*", "").Trim();
            return new BrainAnswer(answer.Length > 0 ? answer : "I don't have an answer for that.");
        }
    }

    /// <summary>Google's errors, put plainly.</summary>
    /// <param name="searching">The request searched the web: refusals are about that, not the key.</param>
    private static string Problem(HttpStatusCode status, string json, bool searching = false)
    {
        string detail = "";
        try
        {
            detail = JsonDocument.Parse(json).RootElement.GetProperty("error").GetProperty("message").GetString() ?? "";
        }
        catch
        {
            // not JSON
        }
        Log.Info($"Gemini said {(int)status}: {detail}");

        return status switch
        {
            HttpStatusCode.TooManyRequests when searching => "Gemini's free web searches are used up for now. Try again later.",
            HttpStatusCode.BadRequest or HttpStatusCode.Forbidden when searching =>
                "Gemini won't search the web with this key: Google may need billing turned on for its searches.",
            HttpStatusCode.TooManyRequests => "Gemini's free limit is used up for now. Try again later.",
            HttpStatusCode.ServiceUnavailable or HttpStatusCode.InternalServerError or HttpStatusCode.GatewayTimeout =>
                "Gemini is busy right now. Try again in a minute.",
            HttpStatusCode.BadRequest when detail.Contains("API key", StringComparison.OrdinalIgnoreCase) =>
                "Gemini doesn't accept the API key. Check it in Settings.",
            HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized => "Gemini doesn't accept the API key. Check it in Settings.",
            HttpStatusCode.BadRequest when detail.Contains("location", StringComparison.OrdinalIgnoreCase) =>
                "Gemini isn't available in this country.",
            _ => $"Gemini had a problem ({(int)status}). Try again.",
        };
    }

    private static JsonObject Function(string name, string description, params (string Name, string Description)[] parameters)
    {
        var properties = new JsonObject();
        foreach (var (parameter, about) in parameters)
        {
            properties[parameter] = new JsonObject { ["type"] = "STRING", ["description"] = about };
        }
        return new JsonObject
        {
            ["name"] = name,
            ["description"] = description,
            ["parameters"] = new JsonObject
            {
                ["type"] = "OBJECT",
                ["properties"] = properties,
                ["required"] = new JsonArray(parameters.Select(p => (JsonNode)p.Name).ToArray()),
            },
        };
    }
}
