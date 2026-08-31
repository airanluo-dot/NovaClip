namespace BiliNative.Core;

public enum TrackType
{
    Video,
    Audio,
    Segment
}

public enum DownloadTaskState
{
    Queued,
    Resolving,
    DownloadingVideo,
    DownloadingAudio,
    DownloadingSegments,
    Paused,
    Merging,
    Finalizing,
    Completed,
    Failed,
    Cancelled
}

public enum ResolverStrategy
{
    PageData,
    PlayUrlResponse,
    HydrateData
}

public enum UpdateChannel
{
    Stable,
    Preview
}

public sealed record MediaUrlCandidate(string Url, string? Fingerprint = null);

public sealed record MediaRequestHeaders(
    string? Referer = null,
    string? Origin = null,
    string? UserAgent = null,
    string? Cookie = null,
    string? RefreshUrl = null);

public sealed record QualityOption(
    int Id,
    string Description,
    bool IsAvailable = true);

public sealed record CodecOption(
    int Id,
    string Name,
    bool IsAvailable = true);

public sealed record LegacyMediaSegment(
    int Index,
    IReadOnlyList<MediaUrlCandidate> Urls,
    long? Size,
    double? DurationSeconds);

public sealed class MediaTrack
{
    public required TrackType Type { get; init; }
    public required string TrackId { get; init; }
    public int? QualityId { get; init; }
    public int? CodecId { get; init; }
    public string? Codec { get; init; }
    public long? Size { get; init; }
    public double? DurationSeconds { get; init; }
    public required IReadOnlyList<MediaUrlCandidate> Urls { get; init; }
}

public sealed class MediaDescriptor
{
    public required string Title { get; init; }
    public required string PageUrl { get; init; }
    public string? Bvid { get; init; }
    public long? Aid { get; init; }
    public long? Cid { get; init; }
    public long? EpisodeId { get; init; }
    public string? EpisodeTitle { get; init; }
    public bool IsBangumi { get; init; }
    public ResolverStrategy Source { get; init; }
    public IReadOnlyList<QualityOption> QualityOptions { get; init; } = [];
    public IReadOnlyList<CodecOption> CodecOptions { get; init; } = [];
    public IReadOnlyList<MediaTrack> Tracks { get; init; } = [];
    public IReadOnlyList<LegacyMediaSegment> LegacySegments { get; init; } = [];

    public MediaTrack? VideoTrack => Tracks.FirstOrDefault(track => track.Type == TrackType.Video);
    public MediaTrack? AudioTrack => Tracks.FirstOrDefault(track => track.Type == TrackType.Audio);
}

public sealed record PlayUrlContext(
    string PageUrl,
    string Title,
    string? Bvid = null,
    long? Aid = null,
    long? Cid = null,
    long? EpisodeId = null,
    string? EpisodeTitle = null,
    bool IsBangumi = false,
    ResolverStrategy Source = ResolverStrategy.PlayUrlResponse);

public sealed record DownloadRequest(
    Guid TaskId,
    MediaDescriptor Media,
    MediaTrack? VideoTrack,
    MediaTrack? AudioTrack,
    string OutputDirectory,
    string OutputFileName,
    RetryPolicy RetryPolicy,
    bool MergeAfterDownload = true,
    bool DeleteTemporaryFilesAfterMerge = true,
    MediaRequestHeaders? RequestHeaders = null);

public sealed record TrackProgress(
    TrackType TrackType,
    long DownloadedBytes,
    long? TotalBytes,
    double BytesPerSecond = 0);

public sealed record DownloadProgress(
    Guid TaskId,
    DownloadTaskState State,
    long DownloadedBytes,
    long? TotalBytes,
    TrackProgress? CurrentTrack = null,
    double BytesPerSecond = 0,
    TimeSpan? Eta = null,
    string? Message = null)
{
    public double Fraction => TotalBytes is > 0 ? Math.Clamp((double)DownloadedBytes / TotalBytes.Value, 0, 1) : 0;
}

public sealed record DownloadTaskSnapshot
{
    public required Guid Id { get; init; }
    public required string PageUrl { get; init; }
    public required string Title { get; init; }
    public required DownloadTaskState State { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public required string OutputPath { get; init; }
    public int? SelectedQualityId { get; init; }
    public string? SelectedCodec { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public long DownloadedBytes { get; init; }
    public long? TotalBytes { get; init; }
}

public sealed record RetryPolicy(
    int MaxAttempts = 3,
    TimeSpan? InitialDelay = null,
    TimeSpan? MaxDelay = null)
{
    public TimeSpan GetDelay(int attempt)
    {
        var initial = InitialDelay ?? TimeSpan.FromSeconds(1);
        var maximum = MaxDelay ?? TimeSpan.FromSeconds(16);
        var multiplier = Math.Pow(2, Math.Max(0, attempt - 1));
        return TimeSpan.FromMilliseconds(Math.Min(maximum.TotalMilliseconds, initial.TotalMilliseconds * multiplier));
    }
}

public sealed record AppError(
    string Code,
    string UserMessage,
    string TechnicalMessage,
    bool Recoverable,
    string? SuggestedAction = null,
    Exception? Exception = null);

public sealed record ResolveResult(MediaDescriptor? Media, AppError? Error)
{
    public bool IsSuccess => Media is not null && Error is null;

    public static ResolveResult Success(MediaDescriptor media) => new(media, null);
    public static ResolveResult Failure(AppError error) => new(null, error);
}

public sealed record FfmpegResult(
    bool Success,
    int ExitCode,
    string? OutputPath,
    string? ErrorMessage = null);

public sealed record AppUpdateAsset(
    string Name,
    string DownloadUrl,
    long? Size,
    string? ContentType,
    string? Digest = null);

public sealed record AppUpdateInfo(
    string Version,
    bool IsPrerelease,
    DateTimeOffset? PublishedAt,
    string? ReleaseNotes,
    IReadOnlyList<AppUpdateAsset> Assets)
{
    public AppUpdateAsset? SetupAsset => Assets.FirstOrDefault(a => a.Name.EndsWith("-setup.exe", StringComparison.OrdinalIgnoreCase));
    public AppUpdateAsset? PortableAsset => Assets.FirstOrDefault(a => a.Name.EndsWith("-portable.zip", StringComparison.OrdinalIgnoreCase));
}
