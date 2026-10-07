using System.ComponentModel;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using WinIsland.Services;

namespace WinIsland.Activities;

/// <summary>One row of the clipboard list.</summary>
public sealed partial class ClipboardEntry : INotifyPropertyChanged
{
    private string _age = string.Empty;

    internal ClipboardEntry(Clip clip, ImageSource? thumbnail, string? imageSize)
    {
        Clip = clip;
        CopiedAt = DateTime.Now;
        Thumbnail = thumbnail;
        Preview = clip.Kind switch
        {
            ClipKind.Image => imageSize is null ? "Image" : $"Image · {imageSize}",
            ClipKind.Files when clip.FilePaths!.Count == 1 => Path.GetFileName(clip.FilePaths[0]),
            ClipKind.Files => $"{clip.FilePaths!.Count} files: {string.Join(", ", clip.FilePaths.Take(3).Select(Path.GetFileName))}",
            _ => OneLine(clip.Text!),
        };
        Glyph = clip.Kind switch
        {
            ClipKind.Link => "",  // Link
            ClipKind.Image => "", // Picture
            ClipKind.Files => "", // Folder
            _ => "",              // Document
        };
        UpdateAge();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal Clip Clip { get; }

    internal DateTime CopiedAt { get; set; }

    public string Preview { get; }

    public string Glyph { get; }

    public ImageSource? Thumbnail { get; }

    public Visibility ThumbnailVisibility => Thumbnail is null ? Visibility.Collapsed : Visibility.Visible;

    public Visibility GlyphVisibility => Thumbnail is null ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>"now", "2m", "3h"… or a short confirmation after you click the entry.</summary>
    public string Age
    {
        get => _age;
        set
        {
            if (_age != value)
            {
                _age = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Age)));
            }
        }
    }

    public void UpdateAge()
    {
        var age = DateTime.Now - CopiedAt;
        Age = age.TotalSeconds < 60 ? "now"
            : age.TotalMinutes < 60 ? $"{(int)age.TotalMinutes}m"
            : age.TotalHours < 24 ? $"{(int)age.TotalHours}h"
            : $"{(int)age.TotalDays}d";
    }

    /// <summary>The text's first non-empty line, whitespace collapsed, shortened.</summary>
    private static string OneLine(string text)
    {
        string line = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? string.Empty;
        line = Whitespace().Replace(line, " ");
        return line.Length > 160 ? line[..160] + "…" : line;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
