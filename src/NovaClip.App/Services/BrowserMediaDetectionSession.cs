using NovaClip.Bilibili;
using NovaClip.Contracts;

namespace NovaClip.App;

// The browser adapter owns WebView COM calls; this session owns navigation work.
// Retired scopes stay alive until their readers finish, then release cancellation.
internal sealed class BrowserMediaDetectionSession : IDisposable
{
    // A slow COM response must not occupy every slot needed by independent page
    // evidence. These separate quotas retain the same total bound of four.
    private const int MaximumNetworkReaders = 3;
    private const int MaximumPageDataReaders = 1;
    private readonly object _gate = new();
    private NavigationScope? _scope;
    private int _networkReaders;
    private int _pageDataReaders;
    private bool _disposed;

    public MediaDetectionCoordinator Detector { get; } = new([]);

    public long BeginNavigation(Uri uri)
    {
        var generation = Detector.BeginNavigation(uri);
        ReplaceScope(generation);
        return generation;
    }

    public long UpdatePageContext(PageIdentity page)
    {
        var generation = Detector.UpdatePageContext(page);
        lock (_gate)
        {
            if (_scope?.Generation == generation) return generation;
        }

        ReplaceScope(generation);
        return generation;
    }

    public ObservationLease? TryBeginObservation(long generation, bool isPageData = false)
    {
        lock (_gate)
        {
            if (_disposed || _scope is null || _scope.Generation != generation ||
                (isPageData ? _pageDataReaders >= MaximumPageDataReaders : _networkReaders >= MaximumNetworkReaders))
                return null;
            if (isPageData) _pageDataReaders++;
            else _networkReaders++;
            _scope.Readers++;
            return new ObservationLease(this, _scope, isPageData);
        }
    }

    public void Reset()
    {
        RetireCurrentScope();
        Detector.Reset();
    }

    public void Dispose()
    {
        lock (_gate) _disposed = true;
        RetireCurrentScope();
    }

    private void ReplaceScope(long generation)
    {
        RetireCurrentScope();
        lock (_gate)
        {
            if (!_disposed) _scope = new NavigationScope(generation);
        }
    }

    private void RetireCurrentScope()
    {
        NavigationScope? old;
        lock (_gate)
        {
            old = _scope;
            _scope = null;
            if (old is null) return;
            old.Retired = true;
        }

        old.Cancellation.Cancel();
        lock (_gate)
        {
            old.CancellationCompleted = true;
            if (old.Readers == 0) old.Cancellation.Dispose();
        }
    }

    private void Release(NavigationScope scope, bool isPageData)
    {
        lock (_gate)
        {
            if (isPageData) _pageDataReaders--;
            else _networkReaders--;
            scope.Readers--;
            if (scope.Retired && scope.CancellationCompleted && scope.Readers == 0)
                scope.Cancellation.Dispose();
        }
    }

    internal sealed class NavigationScope(long generation)
    {
        public long Generation { get; } = generation;
        public CancellationTokenSource Cancellation { get; } = new();
        public int Readers { get; set; }
        public bool Retired { get; set; }
        public bool CancellationCompleted { get; set; }
    }

    internal sealed class ObservationLease(BrowserMediaDetectionSession owner, NavigationScope scope, bool isPageData) : IDisposable
    {
        private BrowserMediaDetectionSession? _owner = owner;
        public long Generation => scope.Generation;
        public CancellationToken CancellationToken => scope.Cancellation.Token;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(scope, isPageData);
    }
}
