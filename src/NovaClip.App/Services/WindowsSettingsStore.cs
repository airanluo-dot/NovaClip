using System.Text.Json;
using System.Text.Json.Serialization;
using NovaClip.Core;

namespace NovaClip.App;

public sealed class WindowsSettingsStore
{
    public const int CurrentSchemaVersion = 2;
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
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
    public static string? GitHubToken => Environment.GetEnvironmentVariable("NOVACLIP_GITHUB_TOKEN");

    public async Task LoadAsync()
    {
        if (!File.Exists(_settingsPath)) return;

        try
        {
            var json = await File.ReadAllTextAsync(_settingsPath).ConfigureAwait(true);
            var document = JsonSerializer.Deserialize<SettingsDocument>(json, JsonOptions);
            if (document is null) return;

            if (!string.IsNullOrWhiteSpace(document.DownloadDirectory)) DownloadDirectory = document.DownloadDirectory;
            MaxConcurrentTasks = Math.Clamp(document.MaxConcurrentTasks, 1, 3);
            MaxRetryAttempts = Math.Clamp(document.MaxRetryAttempts, 1, 8);
            DefaultQuality = document.DefaultQuality ?? DefaultQuality;
            DefaultCodec = document.DefaultCodec ?? DefaultCodec;
            RetryPreset = document.RetryPreset ?? RetryPreset;
            BrowserStartup = document.BrowserStartup ?? BrowserStartup;
            ExternalLinkBehavior = document.ExternalLinkBehavior ?? ExternalLinkBehavior;
            DebugLogging = document.DebugLogging;
            FfmpegPath = string.IsNullOrWhiteSpace(document.FfmpegPath) ? null : document.FfmpegPath;
            MergeAfterDownload = document.MergeAfterDownload;
            DeleteTemporaryFilesAfterMerge = document.DeleteTemporaryFilesAfterMerge;
            AutoCheckUpdates = document.AutoCheckUpdates;
            UpdateChannel = document.UpdateChannel;
            if (!string.IsNullOrWhiteSpace(document.UpdateFeedRepository)) UpdateFeedRepository = document.UpdateFeedRepository;
        }
        catch (Exception exception)
        {
            StartupDiagnostics.Warning("Settings could not be loaded. Defaults will be used.", exception);
        }
    }

    public void Save()
    {
        try
        {
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
                DebugLogging);
            var json = JsonSerializer.Serialize(document, JsonOptions);
            var tempPath = _settingsPath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _settingsPath, true);
        }
        catch (Exception exception)
        {
            StartupDiagnostics.Warning("Settings could not be saved.", exception);
            throw;
        }
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private sealed record SettingsDocument(
        int SchemaVersion,
        string DownloadDirectory,
        int MaxConcurrentTasks,
        int MaxRetryAttempts,
        string? FfmpegPath,
        bool MergeAfterDownload,
        bool DeleteTemporaryFilesAfterMerge,
        bool AutoCheckUpdates,
        UpdateChannel UpdateChannel,
        string UpdateFeedRepository,
        string? DefaultQuality = null,
        string? DefaultCodec = null,
        string? RetryPreset = null,
        string? BrowserStartup = null,
        string? ExternalLinkBehavior = null,
        bool DebugLogging = false);
}
