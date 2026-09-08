using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NovaClip.App;

internal static class StartupDiagnostics
{
    private const long MaxLogFileBytes = 1_000_000;
    private const int RetainedLogFiles = 3;
    private const int MaxEntryCharacters = 32_000;

    private static readonly object Gate = new();
    private static readonly Regex SecretPattern = new(
        @"(?ix)(?<key>cookie|set-cookie|authorization|proxy-authorization|x-api-key|access_token|refresh_token|sessdata|bili_jct)\s*[:=]\s*(?<value>[^\r\n,;]*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex UrlQueryPattern = new(
        @"(?<base>https?://[^\s?]+)\?[^\s#]*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static bool _debugEnabled;

    public static string LogPath
    {
        get
        {
            var root = GetLogRoot();
            Directory.CreateDirectory(root);
            return Path.Combine(root, "startup.log");
        }
    }

    public static void Configure(bool debugEnabled) => Volatile.Write(ref _debugEnabled, debugEnabled);

    public static void Debug(string message)
    {
        if (Volatile.Read(ref _debugEnabled)) Write("DEBUG", message, null);
    }

    public static void Info(string message) => Write("INFO", message, null);

    public static void Warning(string message, Exception? exception = null) => Write("WARN", message, exception);

    public static void Error(string message, Exception exception) => Write("ERROR", message, exception);

    private static void Write(string level, string message, Exception? exception)
    {
        try
        {
            var entry = new LogEntry(
                DateTimeOffset.UtcNow,
                level,
                Redact(message),
                exception is null ? null : Redact(exception.ToString()));
            var serialized = JsonSerializer.Serialize(entry);
            if (serialized.Length > MaxEntryCharacters)
            {
                serialized = serialized[..MaxEntryCharacters] + "\"}";
            }

            var path = LogPath;
            lock (Gate)
            {
                if (File.Exists(path) && new FileInfo(path).Length >= MaxLogFileBytes)
                {
                    Roll(path);
                }

                File.AppendAllText(
                    path,
                    serialized + Environment.NewLine,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
        }
        catch
        {
            // Diagnostics must never create a second startup failure.
        }
    }

    private static string Redact(string value)
    {
        var redacted = value ?? string.Empty;
        redacted = SecretPattern.Replace(
            redacted,
            match => match.Groups["key"].Value + "=[REDACTED]");
        redacted = UrlQueryPattern.Replace(
            redacted,
            match => match.Groups["base"].Value + "?[REDACTED]");
        return redacted.Length <= MaxEntryCharacters
            ? redacted
            : redacted[..MaxEntryCharacters] + "…";
    }

    private static void Roll(string path)
    {
        for (var index = RetainedLogFiles - 1; index >= 1; index--)
        {
            var source = path + "." + index;
            var destination = path + "." + (index + 1);
            if (File.Exists(source)) File.Move(source, destination, overwrite: true);
        }

        if (File.Exists(path)) File.Move(path, path + ".1", overwrite: true);
    }

    private static string GetLogRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NovaClip",
        "Logs");

    private sealed record LogEntry(
        DateTimeOffset Timestamp,
        string Level,
        string Message,
        string? Exception);
}
