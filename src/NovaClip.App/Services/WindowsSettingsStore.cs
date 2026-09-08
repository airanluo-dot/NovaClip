using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NovaClip.Core;

namespace NovaClip.App;

public sealed record WindowsSettingsSnapshot(
    string DownloadDirectory,
    int MaxConcurrentTasks,
    int MaxRetryAttempts,
    string DefaultQuality,
    string DefaultCodec,
    string RetryPreset,
    string BrowserStartup,
    string ExternalLinkBehavior,
    bool DebugLogging,
    string? FfmpegPath,
    bool MergeAfterDownload,
    bool DeleteTemporaryFilesAfterMerge,
    bool AutoCheckUpdates,
    UpdateChannel UpdateChannel,
    string UpdateFeedRepository,
    string Theme);

public sealed class WindowsSettingsStore
{
    public const int CurrentSchemaVersion = 3;
    private const int MaxSettingsBytes = 1_000_000;
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly object _saveGate = new();
    private readonly string _settingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NovaClip",
        "settings.json");

    public string DownloadDirectory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    public int MaxConcurrentTasks { get; set; } = 2;
    public int MaxRetryAttempts { get; set; } = 3;
    public string DefaultQuality { get; set; } = "Highest";
    public string DefaultCodec { get; set; } = "Auto";
    public string RetryPreset { get; set; } = "Standard";
    public string BrowserStartup { get; set; } = "Home";
    public string ExternalLinkBehavior { get; set; } = "System";
    public bool DebugLogging { get; set; }
    public string? FfmpegPath { get; set; }
    public bool MergeAfterDownload { get; set; } = true;
    public bool DeleteTemporaryFilesAfterMerge { get; set; } = true;
    public bool AutoCheckUpdates { get; set; } = true;
    public UpdateChannel UpdateChannel { get; set; } = UpdateChannel.Preview;
    public string UpdateFeedRepository { get; set; } = "airanluo-dot/NovaClip";
    public string Theme { get; set; } = "System";
    public static string? GitHubToken => Environment.GetEnvironmentVariable("NOVACLIP_GITHUB_TOKEN");

    public async Task LoadAsync()
    {
        if (!File.Exists(_settingsPath)) return;

        try
        {
            if (new FileInfo(_settingsPath).Length > MaxSettingsBytes) throw new InvalidDataException("The settings file is too large.");
            var json = await File.ReadAllTextAsync(_settingsPath).ConfigureAwait(true);
            var document = JsonSerializer.Deserialize<SettingsDocument>(json, JsonOptions);
            if (document is null || document.SchemaVersion > CurrentSchemaVersion) throw new InvalidDataException("The settings schema is newer than this application.");

            if (!string.IsNullOrWhiteSpace(document.DownloadDirectory) && Path.IsPathRooted(document.DownloadDirectory)) DownloadDirectory = document.DownloadDirectory;
            MaxConcurrentTasks = Math.Clamp(document.MaxConcurrentTasks, 1, 3);
            MaxRetryAttempts = Math.Clamp(document.MaxRetryAttempts, 1, 8);
            DefaultQuality = document.DefaultQuality ?? DefaultQuality;
            DefaultCodec = document.DefaultCodec ?? DefaultCodec;
            RetryPreset = document.RetryPreset ?? RetryPreset;
            BrowserStartup = document.BrowserStartup ?? BrowserStartup;
            ExternalLinkBehavior = document.ExternalLinkBehavior ?? ExternalLinkBehavior;
            DebugLogging = document.DebugLogging;
            FfmpegPath = IsUsableFfmpegPath(document.FfmpegPath) ? document.FfmpegPath : null;
            MergeAfterDownload = document.MergeAfterDownload;
            DeleteTemporaryFilesAfterMerge = document.DeleteTemporaryFilesAfterMerge;
            AutoCheckUpdates = document.AutoCheckUpdates;
            if (Enum.IsDefined(document.UpdateChannel)) UpdateChannel = document.UpdateChannel;
            if (!string.IsNullOrWhiteSpace(document.UpdateFeedRepository)) UpdateFeedRepository = document.UpdateFeedRepository;
            if (document.Theme is "System" or "Light" or "Dark") Theme = document.Theme;
            Validate();
            StartupDiagnostics.Configure(DebugLogging);
        }
        catch (Exception exception)
        {
            StartupDiagnostics.Warning("Settings could not be loaded. Defaults will be used.", exception);
        }
    }

    public WindowsSettingsSnapshot Capture() =>
        new(
            DownloadDirectory,
            MaxConcurrentTasks,
            MaxRetryAttempts,
            DefaultQuality,
            DefaultCodec,
            RetryPreset,
            BrowserStartup,
            ExternalLinkBehavior,
            DebugLogging,
            FfmpegPath,
            MergeAfterDownload,
            DeleteTemporaryFilesAfterMerge,
            AutoCheckUpdates,
            UpdateChannel,
            UpdateFeedRepository,
            Theme);

    public void Restore(WindowsSettingsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        DownloadDirectory = snapshot.DownloadDirectory;
        MaxConcurrentTasks = snapshot.MaxConcurrentTasks;
        MaxRetryAttempts = snapshot.MaxRetryAttempts;
        DefaultQuality = snapshot.DefaultQuality;
        DefaultCodec = snapshot.DefaultCodec;
        RetryPreset = snapshot.RetryPreset;
        BrowserStartup = snapshot.BrowserStartup;
        ExternalLinkBehavior = snapshot.ExternalLinkBehavior;
        DebugLogging = snapshot.DebugLogging;
        FfmpegPath = snapshot.FfmpegPath;
        MergeAfterDownload = snapshot.MergeAfterDownload;
        DeleteTemporaryFilesAfterMerge = snapshot.DeleteTemporaryFilesAfterMerge;
        AutoCheckUpdates = snapshot.AutoCheckUpdates;
        UpdateChannel = snapshot.UpdateChannel;
        UpdateFeedRepository = snapshot.UpdateFeedRepository;
        Theme = snapshot.Theme;
    }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(DownloadDirectory) || !Path.IsPathRooted(DownloadDirectory)) throw new InvalidDataException("The download directory must be absolute.");
        if (MaxConcurrentTasks is < 1 or > 3 || MaxRetryAttempts is < 1 or > 8) throw new InvalidDataException("The download limits are outside the supported range.");
        if (DefaultQuality is not ("Highest" or "Player" or "1080P" or "720P")) throw new InvalidDataException("The default quality is invalid.");
        if (DefaultCodec is not ("Auto" or "AVC" or "HEVC" or "AV1")) throw new InvalidDataException("The default codec is invalid.");
        if (RetryPreset is not ("Standard" or "Aggressive" or "Off")) throw new InvalidDataException("The retry preset is invalid.");
        if (BrowserStartup is not ("Home" or "LastPage")) throw new InvalidDataException("The browser startup option is invalid.");
        if (ExternalLinkBehavior is not ("System" or "Ask")) throw new InvalidDataException("The external-link option is invalid.");
        if (!Enum.IsDefined(UpdateChannel)) throw new InvalidDataException("The update channel is invalid.");
        if (string.IsNullOrWhiteSpace(UpdateFeedRepository) || UpdateFeedRepository.Length > 200 || UpdateFeedRepository.Count(character => character == '/') != 1) throw new InvalidDataException("The update repository is invalid.");
        if (Theme is not ("System" or "Light" or "Dark")) throw new InvalidDataException("The theme is invalid.");
        if (FfmpegPath is not null && !IsUsableFfmpegPath(FfmpegPath)) throw new InvalidDataException("The FFmpeg path is invalid.");
    }

    public void Save()
    {
        lock (_saveGate)
        {
            try
            {
                Validate();
                var directory = Path.GetDirectoryName(_settingsPath)!;
                Directory.CreateDirectory(directory);
                var document = new SettingsDocument(
                    CurrentSchemaVersion,
                    DownloadDirectory,
                    Math.Clamp(MaxConcurrentTasks, 1, 3),
                    Math.Clamp(MaxRetryAttempts, 1, 8),
                    FfmpegPath,
                    MergeAfterDownload,
                    DeleteTemporaryFilesAfterMerge,
                    AutoCheckUpdates,
                    UpdateChannel,
                    UpdateFeedRepository,
                    DefaultQuality,
                    DefaultCodec,
                    RetryPreset,
                    BrowserStartup,
                    ExternalLinkBehavior,
                    DebugLogging,
                    Theme);
                var json = JsonSerializer.Serialize(document, JsonOptions);
                var tempPath = _settingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, leaveOpen: true))
                {
                    writer.Write(json);
                    writer.Flush();
                    stream.Flush(flushToDisk: true);
                }
                File.Move(tempPath, _settingsPath, overwrite: true);
            }
            catch (Exception exception)
            {
                StartupDiagnostics.Warning("Settings could not be saved.", exception);
                throw;
            }
        }
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private static bool IsUsableFfmpegPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        Path.IsPathRooted(path) &&
        string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase) &&
        File.Exists(path);

    private sealed record SettingsDocument(
        int SchemaVersion,
        string? DownloadDirectory,
        int MaxConcurrentTasks,
        int MaxRetryAttempts,
        string? FfmpegPath,
        bool MergeAfterDownload,
        bool DeleteTemporaryFilesAfterMerge,
        bool AutoCheckUpdates,
        UpdateChannel UpdateChannel,
        string? UpdateFeedRepository,
        string? DefaultQuality = null,
        string? DefaultCodec = null,
        string? RetryPreset = null,
        string? BrowserStartup = null,
        string? ExternalLinkBehavior = null,
        bool DebugLogging = false,
        string? Theme = null);
}
