using System.Collections.Concurrent;
using System.Text.Json;
using NovaClip.Core;

namespace NovaClip.Infrastructure;

public sealed class DownloadManager : IDownloadManager, IDisposable, IAsyncDisposable
{
    private const int MaxManifestCharacters = 2_000_000;
    private readonly IDownloadEngine _engine;
    private readonly IFfmpegService? _ffmpeg;
    private readonly IDownloadTaskRepository? _repository;
    private readonly IHistoryRepository? _history;
    private readonly IDurableObligationStore? _obligations;
    private readonly IOutputReservationService _reservations;
    private readonly bool _ownsReservations;
    private readonly DownloadPersistenceWorker? _persistence;
    private readonly SemaphoreSlim _slots;
    private readonly object _slotsGate = new();
    private readonly ConcurrentDictionary<Guid, DownloadWork> _work = new();
    private int _maxConcurrentTasks;
    private int _slotDeficit;
    private int _accepting = 1;
    private int _disposed;

    public DownloadManager(
        IDownloadEngine engine,
        IFfmpegService? ffmpeg = null,
        IDownloadTaskRepository? repository = null,
        IHistoryRepository? history = null,
        int maxConcurrentTasks = 2,
        IOutputReservationService? reservations = null,
        IDurableObligationStore? obligations = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _ffmpeg = ffmpeg;
        _repository = repository;
        _history = history;
        _obligations = obligations;
        _reservations = reservations ?? new OutputReservationService();
        _ownsReservations = reservations is null;
        _persistence = repository is null ? null : new DownloadPersistenceWorker(repository);
        _maxConcurrentTasks = Math.Clamp(maxConcurrentTasks, 1, 3);
        _slots = new SemaphoreSlim(_maxConcurrentTasks, 3);
    }

    public event EventHandler<DownloadTaskSnapshot>? TaskChanged;

    public bool IsAcceptingWork => Volatile.Read(ref _accepting) == 1;

    public async Task<Guid> EnqueueAsync(DownloadRequest request, CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        if (request.VideoTrack is null &&
            request.AudioTrack is not null &&
            string.Equals(Path.GetExtension(request.OutputFileName), ".mp4", StringComparison.OrdinalIgnoreCase))
        {
            request = request with { OutputFileName = Path.ChangeExtension(request.OutputFileName, ".m4a") };
        }

        if (!IsAcceptingWork) throw new InvalidOperationException("The application is shutting down and no new downloads are accepted.");
        cancellationToken.ThrowIfCancellationRequested();

        var reservation = await _reservations.ReserveAsync(request.TaskId, request.OutputDirectory, request.OutputFileName, cancellationToken).ConfigureAwait(false);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var snapshot = new DownloadTaskSnapshot
            {
                Id = request.TaskId,
                PageUrl = request.Media.PageUrl,
                Title = request.Media.Title,
                State = DownloadTaskState.Queued,
                OperationState = DurableOperationState.Preparing,
                CreatedAt = now,
                UpdatedAt = now,
                OutputPath = reservation.OutputPath,
                SelectedQualityId = request.VideoTrack?.QualityId,
                SelectedCodec = request.VideoTrack?.Codec,
                TotalBytes = GetTotalBytes(request)
            };
            var work = new DownloadWork(request, snapshot, reservation);
            if (!_work.TryAdd(request.TaskId, work)) throw new InvalidOperationException("A task with this ID already exists.");
            Publish(snapshot);
            await PersistCriticalAsync(snapshot).ConfigureAwait(false);
            StartRun(work);
            return request.TaskId;
        }
        catch
        {
            await _reservations.ReleaseAsync(reservation, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task PauseAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_work.TryGetValue(taskId, out var work)) return;

        CancellationTokenSource? stopSource;
        Task? runTask;
        lock (work.Gate)
        {
            if (work.Snapshot.State is DownloadTaskState.Completed or DownloadTaskState.Cancelled or DownloadTaskState.Paused or DownloadTaskState.Failed) return;
            work.PauseRequested = true;
            stopSource = work.CurrentRun?.StopSource;
            runTask = work.RunTask;
        }

        if (stopSource is not null) await stopSource.CancelAsync().ConfigureAwait(false);
        if (runTask is not null) await ObserveTaskAsync(runTask).ConfigureAwait(false);
    }

    public async Task ResumeAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_work.TryGetValue(taskId, out var work)) return;

        Task? previousRun;
        lock (work.Gate)
        {
            if (work.Snapshot.State is not (DownloadTaskState.Paused or DownloadTaskState.Failed)) return;
            previousRun = work.RunTask;
        }

        if (previousRun is not null) await ObserveTaskAsync(previousRun).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        lock (work.Gate)
        {
            if (work.Snapshot.State is not (DownloadTaskState.Paused or DownloadTaskState.Failed)) return;
            work.PauseRequested = false;
            work.CancelRequested = false;
        }

        StartRun(work);
    }

    public async Task CancelAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_work.TryGetValue(taskId, out var work)) return;

        CancellationTokenSource? stopSource;
        Task? runTask;
        DownloadTaskSnapshot? cancelled = null;
        lock (work.Gate)
        {
            if (work.Snapshot.State is DownloadTaskState.Completed or DownloadTaskState.Cancelled) return;
            work.CancelRequested = true;
            work.PauseRequested = false;
            if (work.Snapshot.State is DownloadTaskState.Paused or DownloadTaskState.Failed)
            {
                work.Snapshot = work.Snapshot with { State = DownloadTaskState.Cancelled, UpdatedAt = DateTimeOffset.UtcNow };
                cancelled = work.Snapshot;
                stopSource = null;
                runTask = null;
            }
            else
            {
                stopSource = work.CurrentRun?.StopSource;
                runTask = work.RunTask;
            }
        }

        if (cancelled is not null)
        {
            Publish(cancelled);
            await PersistCriticalAsync(cancelled).ConfigureAwait(false);
            await CleanupCancelledAsync(work).ConfigureAwait(false);
            return;
        }

        if (stopSource is not null) await stopSource.CancelAsync().ConfigureAwait(false);
        if (runTask is not null) await ObserveTaskAsync(runTask).ConfigureAwait(false);
    }

    public IReadOnlyList<DownloadTaskSnapshot> GetTasks() =>
        _work.Values.Select(work => work.GetSnapshot()).OrderByDescending(snapshot => snapshot.UpdatedAt).ToArray();

    public void SetMaxConcurrentTasks(int maxConcurrentTasks)
    {
        var requested = Math.Clamp(maxConcurrentTasks, 1, 3);
        lock (_slotsGate)
        {
            var current = _maxConcurrentTasks;
            if (requested == current) return;
            _maxConcurrentTasks = requested;
            if (requested > current)
            {
                var increase = requested - current;
                var absorbed = Math.Min(increase, _slotDeficit);
                _slotDeficit -= absorbed;
                if (increase > absorbed) _slots.Release(increase - absorbed);
            }
            else
            {
                var decrease = current - requested;
                for (var index = 0; index < decrease; index++)
                {
                    if (!_slots.Wait(0)) _slotDeficit++;
                }
            }
        }
    }

    public async Task RestoreAsync(CancellationToken cancellationToken = default)
    {
        if (_repository is null || !IsAcceptingWork) return;
        await ReplayObligationsAsync(cancellationToken).ConfigureAwait(false);
        var snapshots = await _repository.GetAllAsync(cancellationToken).ConfigureAwait(false);
        foreach (var snapshot in snapshots.Where(item => item.State is not (DownloadTaskState.Completed or DownloadTaskState.Cancelled)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_work.ContainsKey(snapshot.Id)) continue;
            var request = await TryRestoreRequestAsync(snapshot, cancellationToken).ConfigureAwait(false);
            if (request is null) continue;

            var fileName = Path.GetFileName(snapshot.OutputPath);
            var directory = Path.GetDirectoryName(snapshot.OutputPath);
            if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(fileName)) continue;
            OutputReservation reservation;
            try
            {
                reservation = await _reservations.ReserveAsync(snapshot.Id, directory, fileName, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                continue;
            }

            var shouldResume = snapshot.State is not (DownloadTaskState.Paused or DownloadTaskState.Failed);
            var restored = snapshot with
            {
                State = shouldResume ? DownloadTaskState.Queued : snapshot.State,
                OutputPath = reservation.OutputPath,
                UpdatedAt = DateTimeOffset.UtcNow,
                ErrorCode = shouldResume ? null : snapshot.ErrorCode,
                ErrorMessage = shouldResume ? null : snapshot.ErrorMessage
            };
            var work = new DownloadWork(request, restored, reservation);
            if (!_work.TryAdd(snapshot.Id, work))
            {
                await _reservations.ReleaseAsync(reservation, CancellationToken.None).ConfigureAwait(false);
                continue;
            }

            Publish(restored);
            await PersistCriticalAsync(restored).ConfigureAwait(false);
            if (shouldResume) StartRun(work);
        }
    }

    public async Task ShutdownAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        Interlocked.Exchange(ref _accepting, 0);

        var runs = new List<(CancellationTokenSource Source, Task Task)>();
        foreach (var work in _work.Values)
        {
            lock (work.Gate)
            {
                if (work.Snapshot.State is DownloadTaskState.Completed or DownloadTaskState.Cancelled or DownloadTaskState.Paused or DownloadTaskState.Failed) continue;
                if (work.CurrentRun is { } run && work.RunTask is { } task)
                {
                    work.PauseRequested = true;
                    runs.Add((run.StopSource, task));
                }
            }
        }

        foreach (var run in runs) await run.Source.CancelAsync().ConfigureAwait(false);
        var allRuns = Task.WhenAll(runs.Select(item => ObserveTaskAsync(item.Task)));
        await allRuns.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        if (_persistence is not null) await _persistence.DrainAsync(timeout, cancellationToken).ConfigureAwait(false);
    }

    private void StartRun(DownloadWork work)
    {
        lock (work.Gate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (work.CurrentRun is not null && !work.CurrentRun.Task.IsCompleted) return;
            var runId = ++work.RunId;
            var run = new DownloadRun(runId);
            work.Snapshot = work.Snapshot with { RunId = runId, UpdatedAt = DateTimeOffset.UtcNow };
            work.CurrentRun = run;
            run.Task = RunAsync(work, run);
            work.RunTask = run.Task;
        }
    }

    private async Task RunAsync(DownloadWork work, DownloadRun run)
    {
        var slotAcquired = false;
        try
        {
            await _slots.WaitAsync(run.StopSource.Token).ConfigureAwait(false);
            slotAcquired = true;
            if (!await SetStateAsync(work, DownloadTaskState.Resolving, run.RunId).ConfigureAwait(false)) return;

            var progress = new Progress<DownloadProgress>(value =>
            {
                DownloadTaskSnapshot? snapshot = null;
                lock (work.Gate)
                {
                    if (work.CurrentRun?.RunId != run.RunId) return;
                    work.Snapshot = work.Snapshot with
                    {
                        DownloadedBytes = Math.Max(0, value.DownloadedBytes),
                        TotalBytes = value.TotalBytes ?? work.Snapshot.TotalBytes,
                        UpdatedAt = DateTimeOffset.UtcNow
                    };
                    snapshot = work.Snapshot;
                }
                Publish(snapshot);
            });

            var downloadState = work.Request.Media.LegacySegments.Count > 0 && work.Request.VideoTrack is null && work.Request.AudioTrack is null
                ? DownloadTaskState.DownloadingSegments
                : work.Request.VideoTrack is not null
                    ? DownloadTaskState.DownloadingVideo
                    : DownloadTaskState.DownloadingAudio;
            if (!await SetStateAsync(work, downloadState, run.RunId).ConfigureAwait(false)) return;

            await _engine.DownloadAsync(work.Request, progress, run.StopSource.Token).ConfigureAwait(false);
            var taskRoot = HttpRangeDownloader.GetTaskRoot(work.Request.OutputDirectory, work.Request.TaskId);

            if (!await SetStateAsync(work, DownloadTaskState.Finalizing, run.RunId).ConfigureAwait(false)) return;

            if (work.Request.MergeAfterDownload && work.Request.VideoTrack is not null && work.Request.AudioTrack is not null)
            {
                if (_ffmpeg is null) throw new InvalidOperationException("FFmpeg service is not configured.");
                if (!await SetStateAsync(work, DownloadTaskState.Merging, run.RunId).ConfigureAwait(false)) return;
                var staging = Path.Combine(taskRoot, "final-output.tmp");
                TryDeleteFile(staging);
                var result = await _ffmpeg.MergeAsync(
                    Path.Combine(taskRoot, "video.m4s.part"),
                    Path.Combine(taskRoot, "audio.m4s.part"),
                    staging,
                    null,
                    run.StopSource.Token).ConfigureAwait(false);
                if (!result.Success) throw new InvalidOperationException(result.ErrorMessage ?? "FFmpeg merge failed.");
                ValidateStagingFile(staging);
                await CommitPrimaryAsync(work, staging, run.StopSource.Token).ConfigureAwait(false);
            }
            else if (work.Request.Media.LegacySegments.Count > 0)
            {
                var staging = Path.Combine(taskRoot, "legacy.mp4.part");
                ValidateStagingFile(staging);
                await CommitPrimaryAsync(work, staging, run.StopSource.Token).ConfigureAwait(false);
            }
            else if (work.Request.VideoTrack is not null && work.Request.AudioTrack is not null && !work.Request.MergeAfterDownload)
            {
                var videoStaging = Path.Combine(taskRoot, "video.m4s.part");
                var audioStaging = Path.Combine(taskRoot, "audio.m4s.part");
                ValidateStagingFile(videoStaging);
                ValidateStagingFile(audioStaging);
                await CommitPrimaryAsync(work, videoStaging, run.StopSource.Token).ConfigureAwait(false);
                var audioName = Path.GetFileNameWithoutExtension(work.GetSnapshot().OutputPath) + "-audio.m4a";
                var audioReservation = await _reservations.ReserveAsync(work.Request.TaskId, work.Request.OutputDirectory, audioName, run.StopSource.Token).ConfigureAwait(false);
                var committedAudio = await _reservations.CommitAsync(audioReservation, audioStaging, run.StopSource.Token).ConfigureAwait(false);
                StartupDiagnosticsAdapter.Info($"Task {work.Request.TaskId:D} committed secondary audio {committedAudio.OutputPath}.");
            }
            else
            {
                var track = work.Request.VideoTrack ?? work.Request.AudioTrack;
                if (track is null) throw new InvalidOperationException("The download request has no finalizable track.");
                var partName = track.Type == TrackType.Video ? "video.m4s.part" : "audio.m4s.part";
                var staging = Path.Combine(taskRoot, partName);
                ValidateStagingFile(staging);
                await CommitPrimaryAsync(work, staging, run.StopSource.Token).ConfigureAwait(false);
            }

            if (work.Request.DeleteTemporaryFilesAfterMerge)
            {
                var cleanupCompleted = await CleanupTaskRootAsync(work, DurableObligationKind.TemporaryCleanup).ConfigureAwait(false);
                if (!cleanupCompleted)
                {
                    await SetStateIfCurrentAsync(work, DownloadTaskState.Completed, run.RunId).ConfigureAwait(false);
                    await SetOperationStateAsync(work, DurableOperationState.CleanupPending, run.RunId).ConfigureAwait(false);
                }
            }

            if (work.GetSnapshot().State != DownloadTaskState.Completed &&
                !await SetStateAsync(work, DownloadTaskState.Completed, run.RunId).ConfigureAwait(false)) return;
            if (_history is not null)
            {
                var completed = work.GetSnapshot();
                try
                {
                    await _history.AddAsync(completed, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    await EnqueueObligationAsync(DurableObligationKind.HistoryWrite, JsonSerializer.Serialize(completed), exception.Message).ConfigureAwait(false);
                    StartupDiagnosticsAdapter.Warning("History write deferred until the next startup.", exception);
                }
            }
        }
        catch (OperationCanceledException) when (work.IsCancelRequested)
        {
            await CompleteCancellationAsync(work, run.RunId).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (work.IsPauseRequested)
        {
            await SetStateIfCurrentAsync(work, DownloadTaskState.Paused, run.RunId).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await CompleteCancellationAsync(work, run.RunId).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            DownloadTaskSnapshot? failed = null;
            lock (work.Gate)
            {
                if (work.CurrentRun?.RunId == run.RunId)
                {
                    work.Snapshot = work.Snapshot with
                    {
                        ErrorCode = "DOWNLOAD_FAILED",
                        ErrorMessage = exception.Message,
                        UpdatedAt = DateTimeOffset.UtcNow
                    };
                    failed = work.Snapshot;
                }
            }
            if (failed is not null)
            {
                await SetStateIfCurrentAsync(work, DownloadTaskState.Failed, run.RunId).ConfigureAwait(false);
                StartupDiagnosticsAdapter.Error($"Download {work.Request.TaskId:D} failed.", exception);
            }
        }
        finally
        {
            if (slotAcquired) ReleaseSlot();
            lock (work.Gate)
            {
                if (work.CurrentRun?.RunId == run.RunId) work.CurrentRun = null;
            }
            run.StopSource.Dispose();
        }
    }

    private async Task CommitPrimaryAsync(DownloadWork work, string stagingPath, CancellationToken cancellationToken)
    {
        ValidateStagingFile(stagingPath);
        OutputReservation reservation;
        lock (work.Gate) reservation = work.Reservation;
        var committed = await _reservations.CommitAsync(reservation, stagingPath, cancellationToken).ConfigureAwait(false);
        DownloadTaskSnapshot snapshot;
        lock (work.Gate)
        {
            work.Reservation = committed;
            work.Snapshot = work.Snapshot with { OutputPath = committed.OutputPath, OperationState = DurableOperationState.Committed, UpdatedAt = DateTimeOffset.UtcNow };
            snapshot = work.Snapshot;
        }
        Publish(snapshot);
        await PersistCriticalAsync(snapshot).ConfigureAwait(false);
    }

    private async Task CompleteCancellationAsync(DownloadWork work, long runId)
    {
        if (!await SetStateIfCurrentAsync(work, DownloadTaskState.Cancelled, runId).ConfigureAwait(false)) return;
        await CleanupCancelledAsync(work).ConfigureAwait(false);
    }

    private async Task<bool> SetStateIfCurrentAsync(DownloadWork work, DownloadTaskState state, long runId)
    {
        try { return await SetStateAsync(work, state, runId).ConfigureAwait(false); }
        catch (Exception exception)
        {
            StartupDiagnosticsAdapter.Error($"Task {work.Request.TaskId:D} state transition failed.", exception);
            return false;
        }
    }

    private async Task<bool> SetStateAsync(DownloadWork work, DownloadTaskState state, long? expectedRunId = null)
    {
        DownloadTaskSnapshot snapshot;
        lock (work.Gate)
        {
            if (expectedRunId is long runId && work.CurrentRun?.RunId != runId) return false;
            var current = work.Snapshot.State;
            if (!DownloadTaskStateMachine.CanTransition(current, state))
            {
                if (current == state) return true;
                throw new InvalidOperationException($"Cannot transition task {work.Snapshot.Id} from {current} to {state}.");
            }

            var operationState = state switch
            {
                DownloadTaskState.Queued or DownloadTaskState.Resolving => DurableOperationState.Preparing,
                DownloadTaskState.DownloadingVideo or DownloadTaskState.DownloadingAudio or DownloadTaskState.DownloadingSegments => DurableOperationState.Downloading,
                DownloadTaskState.Merging or DownloadTaskState.Finalizing => DurableOperationState.Finalizing,
                DownloadTaskState.Completed when work.Snapshot.OperationState != DurableOperationState.CleanupPending => DurableOperationState.Committed,
                DownloadTaskState.Failed => DurableOperationState.Failed,
                _ => work.Snapshot.OperationState
            };
            work.Snapshot = work.Snapshot with
            {
                State = state,
                OperationState = operationState,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            snapshot = work.Snapshot;
        }
        Publish(snapshot);
        await PersistCriticalAsync(snapshot).ConfigureAwait(false);
        return true;
    }

    private async Task SetOperationStateAsync(DownloadWork work, DurableOperationState operationState, long runId)
    {
        DownloadTaskSnapshot snapshot;
        lock (work.Gate)
        {
            if (work.CurrentRun?.RunId != runId) return;
            work.Snapshot = work.Snapshot with
            {
                OperationState = operationState,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            snapshot = work.Snapshot;
        }

        Publish(snapshot);
        await PersistCriticalAsync(snapshot).ConfigureAwait(false);
    }

    private void Publish(DownloadTaskSnapshot snapshot)
    {
        _persistence?.EnqueueProgress(snapshot);
        try { TaskChanged?.Invoke(this, snapshot); }
        catch (Exception exception) { StartupDiagnosticsAdapter.Warning("Download task notification failed.", exception); }
    }

    private async Task PersistCriticalAsync(DownloadTaskSnapshot snapshot)
    {
        if (_persistence is not null) await _persistence.EnqueueCriticalAsync(snapshot, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task CleanupCancelledAsync(DownloadWork work)
    {
        await _reservations.ReleaseAsync(work.Reservation).ConfigureAwait(false);
        await CleanupTaskRootAsync(work, DurableObligationKind.TemporaryCleanup).ConfigureAwait(false);
    }

    private async Task<bool> CleanupTaskRootAsync(DownloadWork work, DurableObligationKind kind)
    {
        var root = HttpRangeDownloader.GetTaskRoot(work.Request.OutputDirectory, work.Request.TaskId);
        if (!IsSafeTaskRoot(root)) return false;
        try
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            await EnqueueObligationAsync(kind, root, exception.Message).ConfigureAwait(false);
            return false;
        }
    }

    private async Task ReplayObligationsAsync(CancellationToken cancellationToken)
    {
        if (_obligations is null || _history is null) return;
        var pending = await _obligations.GetPendingAsync(100, cancellationToken).ConfigureAwait(false);
        foreach (var obligation in pending)
        {
            try
            {
                switch (obligation.Kind)
                {
                    case DurableObligationKind.HistoryWrite:
                        var snapshot = JsonSerializer.Deserialize<DownloadTaskSnapshot>(obligation.Payload);
                        if (snapshot is null) throw new InvalidDataException("Deferred history payload was invalid.");
                        await _history.AddAsync(snapshot, cancellationToken).ConfigureAwait(false);
                        break;
                    case DurableObligationKind.TemporaryCleanup:
                        if (!IsSafeTaskRoot(obligation.Payload)) throw new InvalidDataException("Deferred cleanup path was outside a task root.");
                        if (Directory.Exists(obligation.Payload)) Directory.Delete(obligation.Payload, true);
                        break;
                    default:
                        throw new InvalidDataException("Unsupported deferred obligation kind.");
                }

                await _obligations.CompleteAsync(obligation.Id, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await _obligations.RecordFailureAsync(obligation.Id, exception.Message, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task EnqueueObligationAsync(DurableObligationKind kind, string payload, string? error)
    {
        if (_obligations is null) return;
        try { await _obligations.EnqueueAsync(kind, payload, error, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception exception) { StartupDiagnosticsAdapter.Error("Could not record durable obligation.", exception); }
    }

    private void ReleaseSlot()
    {
        lock (_slotsGate)
        {
            if (_slotDeficit > 0) _slotDeficit--;
            else _slots.Release();
        }
    }

    private static async Task ObserveTaskAsync(Task task)
    {
        try { await task.ConfigureAwait(false); } catch { }
    }

    private static void ValidateStagingFile(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("The staging output does not exist.", path);
        if (new FileInfo(path).Length <= 0) throw new InvalidDataException("The staging output is empty.");
    }

    private static bool IsSafeTaskRoot(string path)
    {
        if (!Path.IsPathRooted(path)) return false;
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return full.Contains($"{Path.DirectorySeparatorChar}.novaclip{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
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

    private static async Task<DownloadRequest?> TryRestoreRequestAsync(DownloadTaskSnapshot snapshot, CancellationToken cancellationToken)
    {
        var outputDirectory = Path.GetDirectoryName(snapshot.OutputPath);
        if (string.IsNullOrWhiteSpace(outputDirectory) || !Path.IsPathRooted(snapshot.OutputPath)) return null;
        var taskRoot = HttpRangeDownloader.GetTaskRoot(outputDirectory, snapshot.Id);
        var manifestPath = Path.Combine(taskRoot, "task.json");
        if (!File.Exists(manifestPath))
        {
            var legacyRoot = Path.Combine(outputDirectory, ".bilinative", snapshot.Id.ToString("N"));
            manifestPath = Path.Combine(legacyRoot, "task.json");
        }

        try
        {
            if (!File.Exists(manifestPath) || new FileInfo(manifestPath).Length > MaxManifestCharacters * sizeof(char)) return null;
            var json = await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false);
            if (json.Length > MaxManifestCharacters) return null;
            var manifest = JsonSerializer.Deserialize<DownloadManifest>(json, ManifestJsonOptions);
            if (manifest?.Tracks is null || manifest.Tracks.Count == 0) return null;

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
            if (video is null &&
                audio is not null &&
                string.Equals(Path.GetExtension(request.OutputFileName), ".mp4", StringComparison.OrdinalIgnoreCase))
            {
                request = request with { OutputFileName = Path.ChangeExtension(request.OutputFileName, ".m4a") };
            }
            ValidateRequest(request);
            return request;
        }
        catch (Exception exception) when (exception is JsonException or IOException or ArgumentException or NullReferenceException)
        {
            StartupDiagnosticsAdapter.Warning("A persisted download task could not be restored.", exception);
            return null;
        }
    }

    private static readonly JsonSerializerOptions ManifestJsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static bool IsHttpUrl(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 16_384 &&
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme is "http" or "https" &&
        !string.IsNullOrWhiteSpace(uri.Host) &&
        string.IsNullOrEmpty(uri.UserInfo);

    private static void ValidateRequest(DownloadRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.TaskId == Guid.Empty) throw new ArgumentException("The download task ID cannot be empty.", nameof(request));
        ArgumentNullException.ThrowIfNull(request.Media);
        ArgumentNullException.ThrowIfNull(request.Media.LegacySegments);
        if (string.IsNullOrWhiteSpace(request.OutputDirectory) || !Path.IsPathRooted(request.OutputDirectory)) throw new ArgumentException("The output directory must be absolute.", nameof(request));
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

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var work in _work.Values)
        {
            lock (work.Gate) work.CurrentRun?.StopSource.Cancel();
        }

        _slots.Dispose();
        if (_ownsReservations) _reservations.Dispose();
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            try { await ShutdownAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false); } catch { }
            Dispose();
        }

        if (_persistence is not null) await _persistence.DisposeAsync().ConfigureAwait(false);
    }

    private sealed class DownloadWork
    {
        public DownloadWork(DownloadRequest request, DownloadTaskSnapshot snapshot, OutputReservation reservation)
        {
            Request = request;
            Snapshot = snapshot;
            Reservation = reservation;
        }

        public DownloadRequest Request { get; }
        public object Gate { get; } = new();
        public DownloadTaskSnapshot Snapshot { get; set; }
        public OutputReservation Reservation { get; set; }
        public DownloadRun? CurrentRun { get; set; }
        public Task? RunTask { get; set; }
        public long RunId { get; set; }
        public bool PauseRequested { get; set; }
        public bool CancelRequested { get; set; }

        public bool IsPauseRequested
        {
            get { lock (Gate) return PauseRequested && !CancelRequested; }
        }

        public bool IsCancelRequested
        {
            get { lock (Gate) return CancelRequested; }
        }

        public DownloadTaskSnapshot GetSnapshot()
        {
            lock (Gate) return Snapshot;
        }
    }

    private sealed class DownloadRun
    {
        public DownloadRun(long runId)
        {
            RunId = runId;
        }

        public long RunId { get; }
        public CancellationTokenSource StopSource { get; } = new();
        public Task Task { get; set; } = Task.CompletedTask;
    }

    private static class StartupDiagnosticsAdapter
    {
        public static void Info(string message) => System.Diagnostics.Debug.WriteLine(message);
        public static void Warning(string message, Exception exception) => System.Diagnostics.Debug.WriteLine($"{message} {exception}");
        public static void Error(string message, Exception exception) => System.Diagnostics.Debug.WriteLine($"{message} {exception}");
    }

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
}
