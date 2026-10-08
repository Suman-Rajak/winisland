namespace WinIsland.Services;

/// <summary>
/// Tells speech from background noise by loudness, about 30 readings a second. The background level
/// follows quiet moments quickly and loud ones slowly, so speech barely moves it, and speech has to be
/// clearly louder than it. A few loud readings in a row (~90 ms) count as speech, so a click doesn't.
/// </summary>
internal sealed class SpeechDetector
{
    private const int LoudReadingsForSpeech = 3;

    private float _noiseDb = -50;
    private int _loudInARow;

    /// <summary>The latest reading was loud enough to be speech.</summary>
    public bool IsLoud { get; private set; }

    /// <summary>Loud for long enough to be someone talking.</summary>
    public bool IsSpeech => _loudInARow >= LoudReadingsForSpeech;

    public static float ToDecibels(float rms) => 20 * MathF.Log10(rms + 1e-7f);

    /// <param name="db">The latest loudness, from <see cref="ToDecibels"/>.</param>
    public void Update(float db)
    {
        _noiseDb += (db - _noiseDb) * (db < _noiseDb ? 0.3f : 0.01f);
        IsLoud = db > Math.Max(-42, _noiseDb + 12);
        _loudInARow = IsLoud ? _loudInARow + 1 : 0;
    }

    public void Reset()
    {
        _noiseDb = -50;
        _loudInARow = 0;
        IsLoud = false;
    }
}
