namespace AShareWidget;

static class ProviderLog
{
    static readonly object Gate = new();
    static readonly string LogPath = ResolveLogPath();

    static string ResolveLogPath()
    {
        try
        {
            return Path.Combine(AppPaths.UserDataDirectory, "provider.log");
        }
        catch
        {
            return Path.Combine(Path.GetTempPath(), "AShareWidget-provider.log");
        }
    }

    public static string PathOnDisk => LogPath;

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception? ex = null)
    {
        Write("ERROR", ex is null ? message : message + " | " + ex);
    }

    static void Write(string level, string message)
    {
        try
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] [pid={Environment.ProcessId} tid={Environment.CurrentManagedThreadId}] {message}{Environment.NewLine}";
            lock (Gate)
            {
                File.AppendAllText(LogPath, line);
            }
        }
        catch
        {
            // never throw from logging
        }
    }
}
