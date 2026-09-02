using System.Collections.Concurrent;
using System.Text.Json;
using NovaClip.Core;

namespace NovaClip.Infrastructure;

public sealed class DownloadManager : IDownloadManager, IDisposable
{
    private const int MaxManifestCharacters = 2_000_000;
    private readonly IDownloadEngine _engine;
    private readonly IFfmpegService? _ffmpeg;
    private readonly IDownloadTaskRepository? _repository;
    private readonly IHistoryRepository? _history;
    private readonly SemaphoreSlim _slots;
    private readonly ConcurrentDictionary<Guid, DownloadWork> _work = new();

    public DownloadManager(
        IDownloadEngine engine,
        IFfmpegService? ffmpeg = null,
        IDownloadTaskRepository? repository = null,
        IHistoryRepository? history = null,
        int maxConcurrentTasks = 2)
    {
        _engine = engine;
        _ffmpeg = ffmpeg;
        _repository = repository;
        _history = history;
        _slots = new SemaphoreSlim(Math.Clamp(maxConcurrentTasks, 1, 3));
    }

    public event EventHandler<DownloadTaskSnapshot>? TaskChanged;

    public Task<Guid> EnqueueAsync(DownloadRequest request, CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        cancellationToken.ThrowIfCancellationRequested();
        var now = DateTimeOffset.UtcNow;
        var snapshot = new DownloadTaskSnapshot
        {
            Id = request.TaskId,
            PageUrl = request.Media.PageUrl,
            Title = request.Media.Title,
            State = DownloadTaskState.Queued,
            CreatedAt = now,
            UpdatedAt = now,
            OutputPath = Path.Combine(request.OutputDirectory, request.OutputFileName),
            SelectedQualityId = request.VideoTrack?.QualityId,
            SelectedCodec = request.VideoTrack?.Codec,
            TotalBytes = GetTotalBytes(request)
        };
        var work = new DownloadWork(request, snapshot);
        if (!_work.TryAdd(request.TaskId, work)) throw new InvalidOperationException("A task with this ID already exists.");
        Publish(snapshot);
        long runId;
        lock (work.Gate) runId = ++work.RunId;
        _ = RunAsync(work, runId, cancellationToken);
        return Task.FromResult(request.TaskId);
    }

    public async Task PauseAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_work.TryGetValue(taskId, out var work))
        {
            CancellationTokenSource stopSource;
            lock (work.Gate)
            {
                if (work.Snapshot.State is DownloadTaskState.Completed or DownloadTaskState.Cancelled or DownloadTaskState.Paused or DownloadTaskState.Failed) return;
                work.PauseRequested = true;
                stopSource = work.StopSource;
            }
            await stopSource.CancelAsync().ConfigureAwait(false);
        }
    }

    public Task ResumeAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_work.TryGetValue(taskId, out var work))
        {
            long runId;
            lock (work.Gate)
            {
                if (work.Snapshot.State is not (DownloadTaskState.Paused or DownloadTaskState.Failed)) return Task.CompletedTask;
                work.PauseRequested = false;
                work.CancelRequested = false;
                // The previous source may still be observed by a finishing RunAsync. Keep it alive until Dispose.
                work.StopSource = new CancellationTokenSource();
                work.Snapshot = work.Snapshot with { State = DownloadTaskState.Resolving, ErrorCode = null, ErrorMessage = null, UpdatedAt = DateTimeOffset.UtcNow };
                Publish(work.Snapshot);
                runId = ++work.RunId;
            }
            _ = RunAsync(work, runId, cancellationToken);
        }
        return Task.CompletedTask;
    }

    public async Task CancelAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_work.TryGetValue(taskId, out var work))
        {
            CancellationTokenSource stopSource;
            DownloadTaskSnapshot? cancelledSnapshot = null;
            lock (work.Gate)
            {
                if (work.Snapshot.State is DownloadTaskState.Completed or DownloadTaskState.Cancelled) return;
                work.CancelRequested = true;
                if (work.Snapshot.State is DownloadTaskState.Paused or DownloadTaskState.Failed)
                {
                    work.Snapshot = work.Snapshot with { State = DownloadTaskState.Cancelled, UpdatedAt = DateTimeOffset.UtcNow };
                    cancelledSnapshot = work.Snapshot;
                    stopSource = null!;
                }
                else
                {
                    stopSource = work.StopSource;
                }
            }
            if (cancelledSnapshot is not null) Publish(cancelledSnapshot);
            else await stopSource.CancelAsync().ConfigureAwait(false);
        }
    }

    public IReadOnlyList<DownloadTaskSnapshot> GetTasks() => _work.Values.Select(work => work.GetSnapshot()).OrderByDescending(snapshot => snapshot.UpdatedAt).ToArray();

    public async Task RestoreAsync(CancellationToken cancellationToken = default)
    {
        if (_repository is null) return;
        var snapshots = await _repository.GetAllAsync(cancellationToken).ConfigureAwait(false);
        foreach (var snapshot in snapshots.Where(item => item.State is not (DownloadTaskState.Completed or DownloadTaskState.Cancelled)))
        {
            if (_work.ContainsKey(snapshot.Id)) continue;
            var request = await TryRestoreRequestAsync(snapshot, cancellationToken).ConfigureAwait(false);
            if (request is null) continue;
            var shouldResume = snapshot.State is not (DownloadTaskState.Paused or DownloadTaskState.Failed);
            var queued = shouldResume
                ? snapshot with { State = DownloadTaskState.Queued, UpdatedAt = DateTimeOffset.UtcNow, ErrorCode = null, ErrorMessage = null }
                : snapshot;
            var work = new DownloadWork(request, queued);
            if (_work.TryAdd(snapshot.Id, work))
            {
                Publish(queued);
                if (shouldResume)
                {
                    long runId;
                    lock (work.Gate) runId = ++work.RunId;
                    _ = RunAsync(work, runId, CancellationToken.None);
                }
            }
        }
    }

    public void Dispose()
    {
        foreach (var work in _work.Values)
        {
            lock (work.Gate) work.StopSource.Dispose();
        }
        _slots.Dispose();
    }

    private async Task RunAsync(DownloadWork work, long runId, CancellationToken externalCancellationToken)
    {
        CancellationToken stopToken;
        lock (work.Gate)
        {
            if (work.RunId != runId) return;
            stopToken = work.StopSource.Token;
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stopToken, externalCancellationToken);
        var token = linked.Token;
        try
        {
            await _slots.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (!SetState(work, DownloadTaskState.Resolving, runId)) return;
                var progress = new Progress<DownloadProgress>(value =>
                {
                    DownloadTaskSnapshot snapshot;
                    lock (work.Gate)
                    {
                        if (work.RunId != runId) return;
                        work.Snapshot = work.Snapshot with
                        {
                            DownloadedBytes = value.DownloadedBytes,
                            TotalBytes = value.TotalBytes ?? work.Snapshot.TotalBytes,
                            UpdatedAt = DateTimeOffset.UtcNow
                        };
                        snapshot = work.Snapshot;
                    }
                    Publish(snapshot);
                });
                if (!SetState(work, work.Request.Media.LegacySegments.Count > 0 && work.Request.VideoTrack is null && work.Request.AudioTrack is null
                    ? DownloadTaskState.DownloadingSegments
                    : work.Request.VideoTrack is not null ? DownloadTaskState.DownloadingVideo : DownloadTaskState.DownloadingAudio, runId)) return;
                await _engine.DownloadAsync(work.Request, progress, token).ConfigureAwait(false);
                if (work.Request.MergeAfterDownload && work.Request.VideoTrack is not null && work.Request.AudioTrack is not null)
                {
                    if (_ffmpeg is null) throw new InvalidOperationException("FFmpeg service is not configured.");
                    if (!SetState(work, DownloadTaskState.Merging, runId)) return;
                    var root = Path.Combine(work.Request.OutputDirectory, ".bilinative", work.Request.TaskId.ToString("N"));
                    var result = await _ffmpeg.MergeAsync(Path.Combine(root, "video.m4s.part"), Path.Combine(root, "audio.m4s.part"), work.GetSnapshot().OutputPath, null, token).ConfigureAwait(false);
                    if (!result.Success) throw new InvalidOperationException(result.ErrorMessage ?? "FFmpeg merge failed.");
                }

                if (!work.Request.Media.LegacySegments.Any() && work.Request.VideoTrack is not null && work.Request.AudioTrack is not null && !work.Request.MergeAfterDownload)
                {
                    await FinalizeUnmergedTracksAsync(work, token).ConfigureAwait(false);
                }
                else if (!work.Request.Media.LegacySegments.Any() && !(work.Request.VideoTrack is not null && work.Request.AudioTrack is not null && work.Request.MergeAfterDownload))
                {
                    await FinalizeSingleTrackAsync(work, token).ConfigureAwait(false);
                }

                if (!SetState(work, DownloadTaskState.Finalizing, runId)) return;
                if (work.Request.DeleteTemporaryFilesAfterMerge && Directory.Exists(Path.Combine(work.Request.OutputDirectory, ".bilinative", work.Request.TaskId.ToString("N"))))
                {
                    Directory.Delete(Path.Combine(work.Request.OutputDirectory, ".bilinative", work.Request.TaskId.ToString("N")), true);
                }
                if (!SetState(work, DownloadTaskState.Completed, runId)) return;
                if (_history is not null)
                {
                    try
                    {
                        await _history.AddAsync(work.GetSnapshot(), CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        System.Diagnostics.Debug.WriteLine($"NovaClip history persistence failed: {exception}");
                    }
                }
            }
            finally
            {
                _slots.Release();
            }
        }
        catch (OperationCanceledException) when (work.IsPauseRequested)
        {
            TrySetState(work, DownloadTaskState.Paused, runId);
        }
        catch (OperationCanceledException)
        {
            TrySetState(work, DownloadTaskState.Cancelled, runId);
        }
        catch (Exception exception)
        {
            lock (work.Gate)
            {
                if (work.RunId != runId) return;
                work.Snapshot = work.Snapshot with { ErrorCode = "DOWNLOAD_FAILED", ErrorMessage = exception.Message };
            }
            TrySetState(work, DownloadTaskState.Failed, runId);
        }
    }

    private bool SetState(DownloadWork work, DownloadTaskState state, long? expectedRunId = null)
    {
        DownloadTaskSnapshot snapshot;
        lock (work.Gate)
        {
            if (expectedRunId is long runId && work.RunId != runId) return false;
            var current = work.Snapshot.State;
            if (!DownloadTaskStateMachine.CanTransition(current, state))
            {
                if (current == state) return true;
                throw new InvalidOperationException($"Cannot transition task {work.Snapshot.Id} from {current} to {state}.");
            }
            work.Snapshot = work.Snapshot with { State = state, UpdatedAt = DateTimeOffset.UtcNow };
            snapshot = work.Snapshot;
        }
        Publish(snapshot);
        return true;
    }

    private void TrySetState(DownloadWork work, DownloadTaskState state, long runId)
    {
        try
        {
            SetState(work, state, runId);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"NovaClip task state update failed: {exception}");
        }
    }

    private static long? GetTotalBytes(DownloadRequest request)
    {
        IEnumerable<long> sizes = request.VideoTrack is not null || request.AudioTrack is not null
            ? new[] { request.VideoTrack?.Size ?? 0, request.AudioTrack?.Size ?? 0 }
            : request.Media.LegacySegments.Select(segment => segment.Size ?? 0);
        long total = 0;
        foreach (var size in sizes)
        {
            if (size <= 0 || long.MaxValue - total < size) return null;
            total += size;
        }
        return total > 0 ? total : null;
    }

    private static async Task FinalizeSingleTrackAsync(DownloadWork work, CancellationToken cancellationToken)
    {
        var track = work.Request.VideoTrack ?? work.Request.AudioTrack;
        if (track is null) return;
        var root = Path.Combine(work.Request.OutputDirectory, ".bilinative", work.Request.TaskId.ToString("N"));
        var partName = track.Type == TrackType.Video ? "video.m4s.part" : "audio.m4s.part";
        var sourcePath = Path.Combine(root, partName);
        if (!File.Exists(sourcePath)) return;
        Directory.CreateDirectory(work.Request.OutputDirectory);
        var outputPath = work.GetSnapshot().OutputPath;
        if (track.Type == TrackType.Audio && string.Equals(Path.GetExtension(outputPath), ".mp4", StringComparison.OrdinalIgnoreCase))
        {
            outputPath = Path.ChangeExtension(outputPath, ".m4a");
            lock (work.Gate) work.Snapshot = work.Snapshot with { OutputPath = outputPath, UpdatedAt = DateTimeOffset.UtcNow };
        }
        await Task.Run(() => File.Move(sourcePath, outputPath, true), cancellationToken).ConfigureAwait(false);
    }

    private static async Task FinalizeUnmergedTracksAsync(DownloadWork work, CancellationToken cancellationToken)
    {
        var root = Path.Combine(work.Request.OutputDirectory, ".bilinative", work.Request.TaskId.ToString("N"));
        var snapshot = work.GetSnapshot();
        if (work.Request.VideoTrack is not null)
        {
            var videoPath = Path.Combine(root, "video.m4s.part");
            if (File.Exists(videoPath)) await Task.Run(() => File.Move(videoPath, snapshot.OutputPath, true), cancellationToken).ConfigureAwait(false);
        }
        if (work.Request.AudioTrack is not null)
        {
            var audioPath = Path.Combine(root, "audio.m4s.part");
            var outputName = Path.GetFileNameWithoutExtension(snapshot.OutputPath) + "-audio.m4a";
            var outputPath = Path.Combine(work.Request.OutputDirectory, outputName);
            if (File.Exists(audioPath)) await Task.Run(() => File.Move(audioPath, outputPath, true), cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<DownloadRequest?> TryRestoreRequestAsync(DownloadTaskSnapshot snapshot, CancellationToken cancellationToken)
    {
        if (!Path.IsPathRooted(snapshot.OutputPath)) return null;
        var outputDirectory = Path.GetDirectoryName(snapshot.OutputPath);
        if (string.IsNullOrWhiteSpace(outputDirectory)) return null;
        var manifestPath = Path.Combine(outputDirectory, ".bilinative", snapshot.Id.ToString("N"), "task.json");
        if (!File.Exists(manifestPath)) return null;
        try
        {
            if (new FileInfo(manifestPath).Length > MaxManifestCharacters * sizeof(char)) return null;
            var json = await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false);
            if (json.Length > MaxManifestCharacters) return null;
            var manifest = JsonSerializer.Deserialize<DownloadManifest>(json, ManifestJsonOptions);
            if (manifest is null || manifest.Tracks is null || manifest.Tracks.Count == 0) return null;

            var tracks = manifest.Tracks
                .Where(item => item is not null && !string.IsNullOrWhiteSpace(item.Type) && !string.IsNullOrWhiteSpace(item.TrackId) && Enum.TryParse<TrackType>(item.Type, true, out _) && item.Urls is not null)
                .Select(item => new MediaTrack
                {
                    Type = Enum.Parse<TrackType>(item.Type!, true),
                    TrackId = item.TrackId!,
                    Size = item.Size,
                    Urls = item.Urls!.Where(IsHttpUrl).Select(url => new MediaUrlCandidate(url)).ToArray()
                })
                .Where(track => track.Urls.Count > 0)
                .ToArray();
            if (tracks.Length == 0) return null;

            var segmentIndexes = manifest.Tracks
                .Where(item => item is not null && !string.IsNullOrWhiteSpace(item.TrackId))
                .GroupBy(item => item.TrackId!, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First().SegmentIndex, StringComparer.Ordinal);
            var legacySegments = tracks
                .Where(track => track.Type == TrackType.Segment)
                .Select((track, index) => new LegacyMediaSegment(
                    segmentIndexes.TryGetValue(track.TrackId, out var segmentIndex) && segmentIndex is int value ? value : index,
                    track.Urls,
                    track.Size,
                    null))
                .ToArray();
            var mediaTracks = tracks.Where(track => track.Type is TrackType.Video or TrackType.Audio).ToArray();
            var video = mediaTracks.FirstOrDefault(track => track.Type == TrackType.Video);
            var audio = mediaTracks.FirstOrDefault(track => track.Type == TrackType.Audio);
            if (legacySegments.Length == 0 && video is null && audio is null) return null;

            var media = new MediaDescriptor
            {
                Title = string.IsNullOrWhiteSpace(manifest.Title) ? snapshot.Title : manifest.Title!,
                PageUrl = IsHttpUrl(manifest.PageUrl) ? manifest.PageUrl! : snapshot.PageUrl,
                Source = ResolverStrategy.PlayUrlResponse,
                Tracks = mediaTracks,
                LegacySegments = legacySegments
            };
            var outputFileName = Path.GetFileName(snapshot.OutputPath);
            if (string.IsNullOrWhiteSpace(outputFileName)) return null;
            var request = new DownloadRequest(
                snapshot.Id,
                media,
                video,
                audio,
                outputDirectory,
                outputFileName,
                new RetryPolicy(Math.Clamp(manifest.RetryAttempts, 1, 8)),
                manifest.MergeAfterDownload,
                manifest.DeleteTemporaryFiles,
                manifest.RequestHeaders is null ? null : new MediaRequestHeaders(
                    manifest.RequestHeaders.Referer,
                    manifest.RequestHeaders.Origin,
                    manifest.RequestHeaders.UserAgent,
                    null,
                    manifest.RequestHeaders.RefreshUrl));
            ValidateRequest(request);
            return request;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (NullReferenceException)
        {
            return null;
        }
    }

    private static readonly JsonSerializerOptions ManifestJsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static bool IsHttpUrl(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 16_384 && Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is ("http" or "https") && !string.IsNullOrWhiteSpace(uri.Host) && string.IsNullOrEmpty(uri.UserInfo);

    private sealed class DownloadManifest
    {
        public string? PageUrl { get; set; }
        public string? Title { get; set; }
        public int RetryAttempts { get; set; } = 3;
        public bool MergeAfterDownload { get; set; } = true;
        public bool DeleteTemporaryFiles { get; set; } = true;
        public ManifestRequestHeaders? RequestHeaders { get; set; }
        public List<ManifestTrack> Tracks { get; set; } = [];
    }

    private sealed class ManifestRequestHeaders
    {
        public string? Referer { get; set; }
        public string? Origin { get; set; }
        public string? UserAgent { get; set; }
        public string? RefreshUrl { get; set; }
    }

    private sealed class ManifestTrack
    {
        public string? Type { get; set; }
        public string? TrackId { get; set; }
        public long? Size { get; set; }
        public int? SegmentIndex { get; set; }
        public List<string>? Urls { get; set; }
    }

    private void Publish(DownloadTaskSnapshot snapshot)
    {
        if (_repository is not null) _ = PersistAsync(snapshot);
        try
        {
            TaskChanged?.Invoke(this, snapshot);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"NovaClip task notification failed: {exception}");
        }
    }

    private async Task PersistAsync(DownloadTaskSnapshot snapshot)
    {
        try
        {
            await _repository!.UpsertAsync(snapshot).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"NovaClip persistence failed: {exception}");
        }
    }

    private static void ValidateRequest(DownloadRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.TaskId == Guid.Empty) throw new ArgumentException("The download task ID cannot be empty.", nameof(request));
        ArgumentNullException.ThrowIfNull(request.Media);
        ArgumentNullException.ThrowIfNull(request.Media.LegacySegments);
        if (string.IsNullOrWhiteSpace(request.OutputDirectory) || !Path.IsPathRooted(request.OutputDirectory)) throw new ArgumentException("The output directory must be an absolute path.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.OutputFileName) || request.OutputFileName is "." or ".." || request.OutputFileName.IndexOfAny(['/', '\\', '\0']) >= 0 || Path.GetFileName(request.OutputFileName) != request.OutputFileName) throw new ArgumentException("The output file name must be a single safe file name.", nameof(request));
        if (request.VideoTrack is null && request.AudioTrack is null && request.Media.LegacySegments.Count == 0) throw new ArgumentException("The download request has no media tracks.", nameof(request));
        ValidateTrack(request.VideoTrack, TrackType.Video);
        ValidateTrack(request.AudioTrack, TrackType.Audio);
        if (request.VideoTrack is not null && request.AudioTrack is not null && string.Equals(request.VideoTrack.TrackId, request.AudioTrack.TrackId, StringComparison.Ordinal)) throw new ArgumentException("The media track IDs must be unique.", nameof(request));
        var segmentIds = new HashSet<int>();
        foreach (var segment in request.Media.LegacySegments)
        {
            if (segment is null || segment.Index < 0 || !segmentIds.Add(segment.Index) || segment.Size is < 0 || segment.Urls is null || segment.Urls.Count == 0 || segment.Urls.Any(candidate => candidate is null || !IsHttpUrl(candidate.Url))) throw new ArgumentException("The download request contains an invalid media segment.", nameof(request));
        }
    }

    private static void ValidateTrack(MediaTrack? track, TrackType expectedType)
    {
        if (track is null) return;
        if (track.Type != expectedType || string.IsNullOrWhiteSpace(track.TrackId) || track.Size is < 0 || track.DurationSeconds is < 0 || track.Urls is null || track.Urls.Count == 0 || track.Urls.Any(candidate => candidate is null || !IsHttpUrl(candidate.Url))) throw new ArgumentException("The download request contains an invalid media track.", nameof(track));
    }

    private sealed class DownloadWork
    {
        public DownloadWork(DownloadRequest request, DownloadTaskSnapshot snapshot)
        {
            Request = request;
            Snapshot = snapshot;
        }

        public DownloadRequest Request { get; }
        public object Gate { get; } = new();
        public DownloadTaskSnapshot Snapshot { get; set; }
        public CancellationTokenSource StopSource { get; set; } = new();
        public long RunId { get; set; }
        public bool PauseRequested { get; set; }
        public bool CancelRequested { get; set; }
        public bool IsPauseRequested
        {
            get { lock (Gate) return PauseRequested; }
        }
        public DownloadTaskSnapshot GetSnapshot()
        {
            lock (Gate) return Snapshot;
        }
    }
}
