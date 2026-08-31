using System.Text.Json;
using System.Text.Json.Serialization;
using BiliNative.Core;

namespace BiliNative.App;

public sealed class WindowsSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly string _settingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NovaClip",
        "settings.json");

    public string DownloadDirectory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    public int MaxConcurrentTasks { get; set; } = 2;
    public int MaxRetryAttempts { get; set; } = 3;
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
                DownloadDirectory,
                Math.Clamp(MaxConcurrentTasks, 1, 3),
                Math.Clamp(MaxRetryAttempts, 1, 8),
                FfmpegPath,
                MergeAfterDownload,
                DeleteTemporaryFilesAfterMerge,
                AutoCheckUpdates,
                UpdateChannel,
                UpdateFeedRepository);
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
        string DownloadDirectory,
        int MaxConcurrentTasks,
        int MaxRetryAttempts,
        string? FfmpegPath,
        bool MergeAfterDownload,
        bool DeleteTemporaryFilesAfterMerge,
        bool AutoCheckUpdates,
        UpdateChannel UpdateChannel,
        string UpdateFeedRepository);
}
