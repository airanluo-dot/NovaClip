namespace NovaClip.Core;

public interface IPlayUrlNormalizer
{
    ResolveResult Normalize(string json, PlayUrlContext context);
}

public interface IMediaResolver
{
    Task<ResolveResult> ResolveAsync(Uri pageUri, CancellationToken cancellationToken = default);
}

public interface IDownloadEngine
{
    Task DownloadAsync(
        DownloadRequest request,
        IProgress<DownloadProgress> progress,
        CancellationToken cancellationToken);
}

public interface IDownloadManager
{
    event EventHandler<DownloadTaskSnapshot>? TaskChanged;
    Task<Guid> EnqueueAsync(DownloadRequest request, CancellationToken cancellationToken = default);
    Task PauseAsync(Guid taskId, CancellationToken cancellationToken = default);
    Task ResumeAsync(Guid taskId, CancellationToken cancellationToken = default);
    Task CancelAsync(Guid taskId, CancellationToken cancellationToken = default);
    Task RestoreAsync(CancellationToken cancellationToken = default);
    IReadOnlyList<DownloadTaskSnapshot> GetTasks();
}

public interface IDownloadTaskRepository
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task UpsertAsync(DownloadTaskSnapshot snapshot, CancellationToken cancellationToken = default);
    Task<DownloadTaskSnapshot?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DownloadTaskSnapshot>> GetAllAsync(CancellationToken cancellationToken = default);
}

public interface IHistoryRepository
{
    Task AddAsync(DownloadTaskSnapshot snapshot, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DownloadTaskSnapshot>> GetAllAsync(CancellationToken cancellationToken = default);
    Task RemoveAsync(Guid taskId, CancellationToken cancellationToken = default);
}

public interface IFfmpegService
{
    Task<FfmpegResult> MergeAsync(
        string videoPath,
        string audioPath,
        string outputPath,
        IProgress<double>? progress,
        CancellationToken cancellationToken);
}

public interface IFileNameSanitizer
{
    string Sanitize(string value, string fallback = "video");
    string GetAvailablePath(string directory, string fileName);
}

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public interface IAppLogger
{
    void Debug(string message, params object?[] args);
    void Info(string message, params object?[] args);
    void Warning(string message, params object?[] args);
    void LogError(string message, Exception? exception = null, params object?[] args);
}

public interface IUpdateService
{
    Task<AppUpdateInfo?> CheckForUpdateAsync(
        string currentVersion,
        UpdateChannel channel,
        CancellationToken cancellationToken = default);

    Task<string> DownloadAssetAsync(
        AppUpdateAsset asset,
        string destinationPath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);
}
