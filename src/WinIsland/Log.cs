namespace WinIsland;

/// <summary>Tiny file logger. The island has no console, so errors go to %LOCALAPPDATA%\WinIsland\log.txt.</summary>
internal static class Log
{
    private static readonly object s_lock = new();
    private static readonly string s_path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinIsland", "log.txt");

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (s_lock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(s_path)!);
                File.AppendAllText(s_path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never take the island down.
        }
    }
}
