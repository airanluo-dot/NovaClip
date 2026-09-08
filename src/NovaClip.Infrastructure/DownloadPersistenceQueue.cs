using System.Collections.Concurrent;
using NovaClip.Core;

namespace NovaClip.Infrastructure;

public sealed class DownloadPersistenceWorker : IAsyncDisposable
{
    private static readonly TimeSpan ProgressCheckpointInterval = TimeSpan.FromMilliseconds(500);
    private const long ProgressCheckpointByteDelta = 1_048_576;

    private readonly IDownloadTaskRepository _repository;
    private readonly ConcurrentQueue<CriticalWrite> _critical = new();
    private readonly ConcurrentDictionary<Guid, DownloadTaskSnapshot> _progress = new();
    private readonly Dictionary<Guid, ProgressCheckpoint> _checkpoints = [];
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _stopSource = new();
    private readonly Task _worker;
    private int _accepting = 1;

    public DownloadPersistenceWorker(IDownloadTaskRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _worker = Task.Run(WorkerAsync);
    }

    public bool IsAccepting => Volatile.Read(ref _accepting) == 1;

    public void EnqueueProgress(DownloadTaskSnapshot snapshot)
    {
        if (!IsAccepting) return;
        var wasEmpty = _progress.IsEmpty;
        _progress.AddOrUpdate(
            snapshot.Id,
            snapshot,
            (_, existing) => snapshot.UpdatedAt >= existing.UpdatedAt ? snapshot : existing);
        if (wasEmpty) ReleaseSignal();
    }

    public Task EnqueueCriticalAsync(DownloadTaskSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        if (!IsAccepting) return Task.FromException(new ObjectDisposedException(nameof(DownloadPersistenceWorker)));
        cancellationToken.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _critical.Enqueue(new CriticalWrite(snapshot, completion));
        ReleaseSignal();
        return completion.Task;
    }

    public async Task DrainAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        Interlocked.Exchange(ref _accepting, 0);
        ReleaseSignal();
        await _worker.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
    }

    private async Task WorkerAsync()
    {
        try
        {
            while (Volatile.Read(ref _accepting) == 1 || HasPending())
            {
                var wait = GetProgressWait();
                if (wait is null)
                {
                    await _signal.WaitAsync(_stopSource.Token).ConfigureAwait(false);
                }
                else
                {
                    _ = await _signal.WaitAsync(wait.Value, _stopSource.Token).ConfigureAwait(false);
                }

                while (_critical.TryDequeue(out var critical))
                {
                    try
                    {
                        await _repository.UpsertAsync(critical.Snapshot, CancellationToken.None).ConfigureAwait(false);
                        RecordCheckpoint(critical.Snapshot);
                        critical.Completion.TrySetResult();
                    }
                    catch (Exception exception)
                    {
                        critical.Completion.TrySetException(exception);
                    }
                }

                await FlushDueProgressAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_stopSource.IsCancellationRequested)
        {
            while (_critical.TryDequeue(out var critical)) critical.Completion.TrySetCanceled();
        }
    }

    private async Task FlushDueProgressAsync()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in _progress.ToArray())
        {
            if (!ShouldPersist(pair.Value, now)) continue;
            if (!_progress.TryRemove(pair.Key, out var snapshot)) continue;

            try
            {
                await _repository.UpsertAsync(snapshot, CancellationToken.None).ConfigureAwait(false);
                RecordCheckpoint(snapshot);
            }
            catch
            {
                _progress.AddOrUpdate(
                    snapshot.Id,
                    snapshot,
                    (_, existing) => existing.UpdatedAt >= snapshot.UpdatedAt ? existing : snapshot);
            }
        }
    }

    private TimeSpan? GetProgressWait()
    {
        if (_progress.IsEmpty) return null;

        var now = DateTimeOffset.UtcNow;
        TimeSpan? shortest = null;
        foreach (var pair in _progress)
        {
            if (ShouldPersist(pair.Value, now)) return TimeSpan.Zero;
            if (!_checkpoints.TryGetValue(pair.Key, out var checkpoint)) return TimeSpan.Zero;

            var remaining = checkpoint.PersistedAt + ProgressCheckpointInterval - now;
            if (shortest is null || remaining < shortest.Value) shortest = remaining;
        }

        return shortest is null || shortest.Value < TimeSpan.Zero ? TimeSpan.Zero : shortest;
    }

    private bool ShouldPersist(DownloadTaskSnapshot snapshot, DateTimeOffset now)
    {
        if (!_checkpoints.TryGetValue(snapshot.Id, out var checkpoint)) return true;
        if (snapshot.RunId != checkpoint.RunId ||
            snapshot.State != checkpoint.State ||
            snapshot.OperationState != checkpoint.OperationState)
        {
            return true;
        }

        return snapshot.DownloadedBytes >= checkpoint.DownloadedBytes + ProgressCheckpointByteDelta ||
            now - checkpoint.PersistedAt >= ProgressCheckpointInterval;
    }

    private void RecordCheckpoint(DownloadTaskSnapshot snapshot)
    {
        _checkpoints[snapshot.Id] = new ProgressCheckpoint(
            snapshot.RunId,
            snapshot.State,
            snapshot.OperationState,
            snapshot.DownloadedBytes,
            DateTimeOffset.UtcNow);
    }

    private bool HasPending() => !_critical.IsEmpty || !_progress.IsEmpty;

    private void ReleaseSignal()
    {
        try { _signal.Release(); } catch (ObjectDisposedException) { }
    }

    public async ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _accepting, 0);
        _stopSource.Cancel();
        try { await _worker.ConfigureAwait(false); } catch (OperationCanceledException) { }
        _stopSource.Dispose();
        _signal.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed record CriticalWrite(DownloadTaskSnapshot Snapshot, TaskCompletionSource Completion);

    private sealed record ProgressCheckpoint(
        long RunId,
        DownloadTaskState State,
        DurableOperationState OperationState,
        long DownloadedBytes,
        DateTimeOffset PersistedAt);
}
