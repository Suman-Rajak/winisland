namespace WinIsland;

/// <summary>Tiny file logger. The island has no console, so errors go to log.txt in <see cref="AppPaths.DataFolder"/>.</summary>
internal static class Log
{
    private static readonly object s_lock = new();
    private static readonly string s_path = Path.Combine(AppPaths.DataFolder, "log.txt");

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
        catch (Exception ex)
        {
            // Logging must never take the island down, but a log that silently stops is hard to debug:
            // say why in a fallback file in %TEMP%.
            try
            {
                File.AppendAllText(Path.Combine(Path.GetTempPath(), $"{Product.Name}-log-failures.txt"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} couldn't write {s_path}: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}");
            }
            catch
            {
                // Nothing left to try.
            }
        }
    }
}
