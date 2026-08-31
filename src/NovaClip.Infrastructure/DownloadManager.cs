using System.Collections.Concurrent;
using System.Text.Json;
using NovaClip.Core;

namespace NovaClip.Infrastructure;

public sealed class DownloadManager : IDownloadManager, IDisposable
{
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
        _ = RunAsync(work, cancellationToken);
        return Task.FromResult(request.TaskId);
    }

    public async Task PauseAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        if (_work.TryGetValue(taskId, out var work))
        {
            work.PauseRequested = true;
            await work.StopSource.CancelAsync().ConfigureAwait(false);
        }
    }

    public Task ResumeAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        if (_work.TryGetValue(taskId, out var work) && work.Snapshot.State is DownloadTaskState.Paused or DownloadTaskState.Failed)
        {
            work.PauseRequested = false;
            work.CancelRequested = false;
            work.StopSource.Dispose();
            work.StopSource = new CancellationTokenSource();
            work.Snapshot = work.Snapshot with { ErrorCode = null, ErrorMessage = null, UpdatedAt = DateTimeOffset.UtcNow };
            Publish(work.Snapshot);
            _ = RunAsync(work, cancellationToken);
        }
        return Task.CompletedTask;
    }

    public async Task CancelAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        if (_work.TryGetValue(taskId, out var work))
        {
            work.CancelRequested = true;
            await work.StopSource.CancelAsync().ConfigureAwait(false);
        }
    }

    public IReadOnlyList<DownloadTaskSnapshot> GetTasks() => _work.Values.Select(work => work.Snapshot).OrderByDescending(snapshot => snapshot.UpdatedAt).ToArray();

    public async Task RestoreAsync(CancellationToken cancellationToken = default)
    {
        if (_repository is null) return;
        var snapshots = await _repository.GetAllAsync(cancellationToken).ConfigureAwait(false);
        foreach (var snapshot in snapshots.Where(item => item.State is not (DownloadTaskState.Completed or DownloadTaskState.Cancelled)))
        {
            if (_work.ContainsKey(snapshot.Id)) continue;
            var request = await TryRestoreRequestAsync(snapshot, cancellationToken).ConfigureAwait(false);
            if (request is null) continue;
            var queued = snapshot with
            {
                State = DownloadTaskState.Queued,
                UpdatedAt = DateTimeOffset.UtcNow,
                ErrorCode = null,
                ErrorMessage = null
            };
            var work = new DownloadWork(request, queued);
            if (_work.TryAdd(snapshot.Id, work))
            {
                Publish(queued);
                _ = RunAsync(work, CancellationToken.None);
            }
        }
    }

    public void Dispose()
    {
        foreach (var work in _work.Values) work.StopSource.Dispose();
        _slots.Dispose();
    }

    private async Task RunAsync(DownloadWork work, CancellationToken externalCancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(work.StopSource.Token, externalCancellationToken);
        var token = linked.Token;
        try
        {
            await _slots.WaitAsync(token).ConfigureAwait(false);
            try
            {
                SetState(work, DownloadTaskState.Resolving);
                var progress = new Progress<DownloadProgress>(value =>
                {
                    work.Snapshot = work.Snapshot with
                    {
                        DownloadedBytes = value.DownloadedBytes,
                        TotalBytes = value.TotalBytes ?? work.Snapshot.TotalBytes,
                        UpdatedAt = DateTimeOffset.UtcNow
                    };
                    Publish(work.Snapshot);
                });
                SetState(work, work.Request.Media.LegacySegments.Count > 0 && work.Request.VideoTrack is null && work.Request.AudioTrack is null
                    ? DownloadTaskState.DownloadingSegments
                    : work.Request.VideoTrack is not null ? DownloadTaskState.DownloadingVideo : DownloadTaskState.DownloadingAudio);
                await _engine.DownloadAsync(work.Request, progress, token).ConfigureAwait(false);
                if (work.Request.MergeAfterDownload && work.Request.VideoTrack is not null && work.Request.AudioTrack is not null)
                {
                    if (_ffmpeg is null) throw new InvalidOperationException("FFmpeg service is not configured.");
                    SetState(work, DownloadTaskState.Merging);
                    var root = Path.Combine(work.Request.OutputDirectory, ".bilinative", work.Request.TaskId.ToString("N"));
                    var result = await _ffmpeg.MergeAsync(Path.Combine(root, "video.m4s.part"), Path.Combine(root, "audio.m4s.part"), work.Snapshot.OutputPath, null, token).ConfigureAwait(false);
                    if (!result.Success) throw new InvalidOperationException(result.ErrorMessage ?? "FFmpeg merge failed.");
                }

                if (!work.Request.Media.LegacySegments.Any() && !(work.Request.VideoTrack is not null && work.Request.AudioTrack is not null && work.Request.MergeAfterDownload))
                {
                    await FinalizeSingleTrackAsync(work, token).ConfigureAwait(false);
                }

                SetState(work, DownloadTaskState.Finalizing);
                if (work.Request.DeleteTemporaryFilesAfterMerge && Directory.Exists(Path.Combine(work.Request.OutputDirectory, ".bilinative", work.Request.TaskId.ToString("N"))))
                {
                    Directory.Delete(Path.Combine(work.Request.OutputDirectory, ".bilinative", work.Request.TaskId.ToString("N")), true);
                }
                SetState(work, DownloadTaskState.Completed);
                if (_history is not null) await _history.AddAsync(work.Snapshot, token).ConfigureAwait(false);
            }
            finally
            {
                _slots.Release();
            }
        }
        catch (OperationCanceledException) when (work.PauseRequested)
        {
            SetState(work, DownloadTaskState.Paused);
        }
        catch (OperationCanceledException)
        {
            SetState(work, DownloadTaskState.Cancelled);
        }
        catch (Exception exception)
        {
            work.Snapshot = work.Snapshot with { ErrorCode = "DOWNLOAD_FAILED", ErrorMessage = exception.Message };
            SetState(work, DownloadTaskState.Failed);
        }
    }

    private void SetState(DownloadWork work, DownloadTaskState state)
    {
        var current = work.Snapshot.State;
        if (!DownloadTaskStateMachine.CanTransition(current, state))
        {
            if (current == state) return;
            throw new InvalidOperationException($"Cannot transition task {work.Snapshot.Id} from {current} to {state}.");
        }
        work.Snapshot = work.Snapshot with { State = state, UpdatedAt = DateTimeOffset.UtcNow };
        Publish(work.Snapshot);
    }

    private static long? GetTotalBytes(DownloadRequest request)
    {
        var total = (request.VideoTrack?.Size ?? 0) + (request.AudioTrack?.Size ?? 0);
        if (total == 0 && request.Media.LegacySegments.Count > 0) total = request.Media.LegacySegments.Sum(segment => segment.Size ?? 0);
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
        await Task.Run(() => File.Move(sourcePath, work.Snapshot.OutputPath, true), cancellationToken).ConfigureAwait(false);
    }

    private static async Task<DownloadRequest?> TryRestoreRequestAsync(DownloadTaskSnapshot snapshot, CancellationToken cancellationToken)
    {
        var outputDirectory = Path.GetDirectoryName(snapshot.OutputPath);
        if (string.IsNullOrWhiteSpace(outputDirectory)) return null;
        var manifestPath = Path.Combine(outputDirectory, ".bilinative", snapshot.Id.ToString("N"), "task.json");
        if (!File.Exists(manifestPath)) return null;
        try
        {
            var json = await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false);
            var manifest = JsonSerializer.Deserialize<DownloadManifest>(json, ManifestJsonOptions);
            if (manifest is null || manifest.Tracks.Count == 0) return null;

            var tracks = manifest.Tracks
                .Where(item => Enum.TryParse<TrackType>(item.Type, true, out _) && item.Urls.Count > 0)
                .Select(item => new MediaTrack
                {
                    Type = Enum.Parse<TrackType>(item.Type, true),
                    TrackId = item.TrackId,
                    Size = item.Size,
                    Urls = item.Urls.Where(url => !string.IsNullOrWhiteSpace(url)).Select(url => new MediaUrlCandidate(url)).ToArray()
                })
                .ToArray();
            if (tracks.Length == 0) return null;

            var legacySegments = tracks
                .Where(track => track.Type == TrackType.Segment)
                .Select((track, index) => new LegacyMediaSegment(
                    manifest.Tracks.First(item => item.TrackId == track.TrackId).SegmentIndex ?? index,
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
                Title = manifest.Title ?? snapshot.Title,
                PageUrl = manifest.PageUrl ?? snapshot.PageUrl,
                Source = ResolverStrategy.PlayUrlResponse,
                Tracks = mediaTracks,
                LegacySegments = legacySegments
            };
            var outputFileName = Path.GetFileName(snapshot.OutputPath);
            if (string.IsNullOrWhiteSpace(outputFileName)) return null;
            return new DownloadRequest(
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
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static readonly JsonSerializerOptions ManifestJsonOptions = new() { PropertyNameCaseInsensitive = true };

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
        public string Type { get; set; } = string.Empty;
        public string TrackId { get; set; } = string.Empty;
        public long? Size { get; set; }
        public int? SegmentIndex { get; set; }
        public List<string> Urls { get; set; } = [];
    }

    private void Publish(DownloadTaskSnapshot snapshot)
    {
        _ = _repository?.UpsertAsync(snapshot);
        TaskChanged?.Invoke(this, snapshot);
    }

    private sealed class DownloadWork
    {
        public DownloadWork(DownloadRequest request, DownloadTaskSnapshot snapshot)
        {
            Request = request;
            Snapshot = snapshot;
        }

        public DownloadRequest Request { get; }
        public DownloadTaskSnapshot Snapshot { get; set; }
        public CancellationTokenSource StopSource { get; set; } = new();
        public bool PauseRequested { get; set; }
        public bool CancelRequested { get; set; }
    }
}
