using System.Text;

namespace NovaClip.App;

internal static class StartupDiagnostics
{
    private static readonly object Gate = new();

    public static string LogPath
    {
        get
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NovaClip", "Logs");
            Directory.CreateDirectory(root);
            return Path.Combine(root, "startup.log");
        }
    }

    public static void Info(string message) => Write("INFO", message, null);

    public static void Warning(string message, Exception? exception = null) => Write("WARN", message, exception);

    public static void Error(string message, Exception exception) => Write("ERROR", message, exception);

    private static void Write(string level, string message, Exception? exception)
    {
        try
        {
            var builder = new StringBuilder();
            builder.Append(DateTimeOffset.Now.ToString("O"));
            builder.Append(" [").Append(level).Append("] ").AppendLine(message);
            if (exception is not null) builder.AppendLine(exception.ToString());
            lock (Gate)
            {
                File.AppendAllText(LogPath, builder.ToString(), Encoding.UTF8);
            }
        }
        catch
        {
            // Diagnostics must never create a second startup failure.
        }
    }
}
