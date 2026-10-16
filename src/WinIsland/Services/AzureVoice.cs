using System.Net;
using System.Net.Http.Headers;
using System.Security;
using System.Text;
using Windows.Media.Core;
using Windows.Storage.Streams;

namespace WinIsland.Services;

/// <summary>An accent of English, as heard in a request and used for the reply.</summary>
internal enum SpokenAccent
{
    Indian,
    American,
    British,
}

/// <summary>
/// Microsoft's natural voices (Azure AI Speech text to speech, with your own key: free for half a million
/// characters a month), so the assistant answers in a natural voice with an Indian, American or British
/// accent, or in Hindi. Only what it says is sent to Microsoft, never what you said.
/// </summary>
internal static class AzureVoice
{
    public const string KeyName = "AzureSpeechKey";

    private static readonly HttpClient s_http = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>The saved key, or null.</summary>
    public static string? Key => SecretStore.Get(KeyName) is { Length: > 0 } key ? key : null;

    /// <summary>On in Settings, with a key and a region: replies use these voices (else Windows' own).</summary>
    public static bool IsReady => AppSettings.Current.NaturalVoice && Key is not null && IsRegion(AppSettings.Current.AzureRegion);

    /// <summary>An Azure region's short name ("centralindia", "eastus"): letters and digits only.</summary>
    public static bool IsRegion(string? region) => region is { Length: > 0 } && region.All(char.IsAsciiLetterOrDigit);

    /// <summary>Turns what's typed in Settings ("Central India", "centralindia ") into a region's short name.</summary>
    public static string NormalizeRegion(string typed) => new(typed.Where(char.IsAsciiLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>
    /// The voice for a reply: a Hindi voice for Hindi text (in Devanagari), else one with the accent. The
    /// standard neural voices (not the HD ones), which the free tier covers.
    /// </summary>
    public static string VoiceFor(string text, SpokenAccent accent, bool male)
    {
        if (IsHindi(text))
        {
            return male ? "hi-IN-MadhurNeural" : "hi-IN-SwaraNeural";
        }
        return accent switch
        {
            SpokenAccent.Indian => male ? "en-IN-PrabhatNeural" : "en-IN-NeerjaNeural",
            SpokenAccent.British => male ? "en-GB-RyanNeural" : "en-GB-SoniaNeural",
            _ => male ? "en-US-AndrewNeural" : "en-US-AvaNeural",
        };
    }

    /// <summary>Text in Devanagari (Hindi).</summary>
    public static bool IsHindi(string text) => text.Any(c => c is >= 'ऀ' and <= 'ॿ');

    /// <summary>Says <paramref name="text"/> in <paramref name="voice"/>: the audio, as a .wav.</summary>
    /// <exception cref="AzureVoiceException">With a message fit to show ("Azure doesn't accept the key…").</exception>
    public static async Task<byte[]> SynthesizeAsync(string text, string voice, string key, string region, CancellationToken cancellation)
    {
        if (!IsRegion(region))
        {
            throw new AzureVoiceException("Type your Speech resource's region, like centralindia or eastus.");
        }

        string locale = voice[..5]; // "en-IN-NeerjaNeural" → "en-IN"
        string ssml = $"<speak version='1.0' xml:lang='{locale}'><voice name='{voice}'>{SecurityElement.Escape(text)}</voice></speak>";
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://{region}.tts.speech.microsoft.com/cognitiveservices/v1")
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(ssml)),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/ssml+xml");
        request.Headers.Add("Ocp-Apim-Subscription-Key", key);
        request.Headers.Add("X-Microsoft-OutputFormat", "riff-24khz-16bit-mono-pcm");
        request.Headers.UserAgent.ParseAdd(Product.Name);

        HttpResponseMessage response;
        try
        {
            response = await s_http.SendAsync(request, cancellation);
        }
        catch (HttpRequestException ex)
        {
            throw new AzureVoiceException($"Couldn't reach Azure in {region}. Check the region and your connection.", ex);
        }
        catch (TaskCanceledException ex) when (!cancellation.IsCancellationRequested)
        {
            throw new AzureVoiceException("Azure took too long to answer.", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new AzureVoiceException(response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                        $"Azure doesn't accept the key for {region}. Check the key, and that the region is your Speech resource's.",
                    HttpStatusCode.TooManyRequests => "Azure's free voice limit is used up for now.",
                    HttpStatusCode.BadRequest => "Azure didn't take the request (the voice may not be offered in this region).",
                    _ => $"Azure couldn't say it ({(int)response.StatusCode}).",
                });
            }
            return await response.Content.ReadAsByteArrayAsync(cancellation);
        }
    }

    /// <summary>A .wav in memory, ready for a MediaPlayer.</summary>
    public static async Task<MediaSource> ToMediaSourceAsync(byte[] wav)
    {
        var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(wav);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }
        stream.Seek(0);
        return MediaSource.CreateFromStream(stream, "audio/wav");
    }
}

/// <summary>Azure couldn't say something, with a message fit to show.</summary>
internal sealed class AzureVoiceException(string message, Exception? inner = null) : Exception(message, inner);
