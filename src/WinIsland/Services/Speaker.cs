using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.SpeechSynthesis;

namespace WinIsland.Services;

/// <summary>Says things out loud with Windows' built-in voice (the one chosen in Settings → Time &amp; language → Speech).</summary>
internal sealed class Speaker
{
    private readonly SpeechSynthesizer _synthesizer = new();
    private readonly MediaPlayer _player = new() { AudioCategory = MediaPlayerAudioCategory.Speech };

    public Speaker()
    {
        // Otherwise Windows lists the assistant's voice as "now playing" media, and the island would show it.
        _player.CommandManager.IsEnabled = false;
    }

    public async void Speak(string text)
    {
        try
        {
            var stream = await _synthesizer.SynthesizeTextToStreamAsync(text);
            _player.Source = MediaSource.CreateFromStream(stream, stream.ContentType);
            _player.Play();
        }
        catch (Exception ex)
        {
            Log.Error("Speaking failed", ex);
        }
    }

    public void Stop()
    {
        _player.Pause();
        _player.Source = null;
    }
}
