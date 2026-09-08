using System.Collections.Concurrent;
using NovaClip.Core;

namespace NovaClip.Infrastructure;

public sealed class DownloadPersistenceWorker : IAsyncDisposable
{
    private readonly IDownloadTaskRepository _repository;
    private readonly ConcurrentQueue<CriticalWrite> _critical = new();
    private readonly ConcurrentDictionary<Guid, DownloadTaskSnapshot> _progress = new();
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
        _progress.AddOrUpdate(
            snapshot.Id,
            snapshot,
            (_, existing) => snapshot.UpdatedAt >= existing.UpdatedAt ? snapshot : existing);
        ReleaseSignal();
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
                await _signal.WaitAsync(_stopSource.Token).ConfigureAwait(false);
                while (_critical.TryDequeue(out var critical))
                {
                    try
                    {
                        await _repository.UpsertAsync(critical.Snapshot, CancellationToken.None).ConfigureAwait(false);
                        critical.Completion.TrySetResult();
                    }
                    catch (Exception exception)
                    {
                        critical.Completion.TrySetException(exception);
                    }
                }

                foreach (var pair in _progress.ToArray())
                {
                    if (!_progress.TryRemove(pair.Key, out var snapshot)) continue;
                    try { await _repository.UpsertAsync(snapshot, CancellationToken.None).ConfigureAwait(false); }
                    catch { }
                }
            }
        }
        catch (OperationCanceledException) when (_stopSource.IsCancellationRequested)
        {
            while (_critical.TryDequeue(out var critical)) critical.Completion.TrySetCanceled();
        }
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
}
