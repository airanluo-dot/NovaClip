using Windows.Storage;
using BiliNative.Core;

namespace BiliNative.App;

public sealed class WindowsSettingsStore
{
    private ApplicationDataContainer? _container;

    public string DownloadDirectory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    public int MaxConcurrentTasks { get; set; } = 2;
    public int MaxRetryAttempts { get; set; } = 3;
    public string? FfmpegPath { get; set; }
    public bool MergeAfterDownload { get; set; } = true;
    public bool DeleteTemporaryFilesAfterMerge { get; set; } = true;
    public bool AutoCheckUpdates { get; set; } = true;
    public UpdateChannel UpdateChannel { get; set; } = UpdateChannel.Preview;
    public string UpdateFeedRepository { get; set; } = "airanluo-dot/NovaClip";

    public async Task LoadAsync()
    {
        try
        {
            _container = ApplicationData.Current.LocalSettings;
            if (_container.Values["downloadDirectory"] is string directory && !string.IsNullOrWhiteSpace(directory)) DownloadDirectory = directory;
            if (_container.Values["maxConcurrentTasks"] is int concurrency) MaxConcurrentTasks = Math.Clamp(concurrency, 1, 3);
            if (_container.Values["maxRetryAttempts"] is int retries) MaxRetryAttempts = Math.Clamp(retries, 1, 8);
            if (_container.Values["ffmpegPath"] is string ffmpegPath && !string.IsNullOrWhiteSpace(ffmpegPath)) FfmpegPath = ffmpegPath;
            if (_container.Values["mergeAfterDownload"] is bool merge) MergeAfterDownload = merge;
            if (_container.Values["deleteTemporaryFiles"] is bool deleteTemp) DeleteTemporaryFilesAfterMerge = deleteTemp;
            if (_container.Values["autoCheckUpdates"] is bool autoCheck) AutoCheckUpdates = autoCheck;
            if (_container.Values["updateChannel"] is string channel && Enum.TryParse<UpdateChannel>(channel, true, out var parsed)) UpdateChannel = parsed;
        }
        catch (Exception)
        {
            _container = null;
        }
        await Task.CompletedTask;
    }

    public void Save()
    {
        if (_container is null) return;
        _container.Values["downloadDirectory"] = DownloadDirectory;
        _container.Values["maxConcurrentTasks"] = MaxConcurrentTasks;
        _container.Values["maxRetryAttempts"] = MaxRetryAttempts;
        _container.Values["ffmpegPath"] = FfmpegPath ?? string.Empty;
        _container.Values["mergeAfterDownload"] = MergeAfterDownload;
        _container.Values["deleteTemporaryFiles"] = DeleteTemporaryFilesAfterMerge;
        _container.Values["autoCheckUpdates"] = AutoCheckUpdates;
        _container.Values["updateChannel"] = UpdateChannel.ToString();
    }
}
