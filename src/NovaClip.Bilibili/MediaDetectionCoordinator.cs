using NovaClip.Contracts;

namespace NovaClip.Bilibili;

public sealed class MediaDetectionCoordinator : IMediaDetectionCoordinator
{
    private const int DiagnosticLimit = 200;
    private readonly IReadOnlyList<IMediaDetectionStrategy> _strategies;
    private readonly List<DetectionDiagnostic> _diagnostics = [];
    private readonly HashSet<MediaFingerprint> _seen = [];
    private readonly object _gate = new();
    private PageIdentity? _page;
    private long _generation;
    private MediaDetectionSnapshot _snapshot = new(MediaDetectionState.Idle, null, null, null, null, []);

    public MediaDetectionCoordinator(IEnumerable<IMediaDetectionStrategy> strategies)
    {
        ArgumentNullException.ThrowIfNull(strategies);
        _strategies = strategies.Where(strategy => strategy is not null).ToArray();
    }

    public event EventHandler<MediaDetectionSnapshot>? StateChanged;

    public MediaDetectionSnapshot Snapshot
    {
        get
        {
            lock (_gate) return _snapshot;
        }
    }

    public long BeginNavigation(Uri uri)
    {
        if (uri is null || !uri.IsAbsoluteUri || uri.Scheme is not ("http" or "https")) throw new ArgumentException("A valid HTTP(S) navigation URI is required.", nameof(uri));
        MediaDetectionSnapshot snapshot;
        long generation;
        lock (_gate)
        {
            generation = ++_generation;
            _page = new PageIdentity(uri.ToString(), null, null, null, null, generation);
            _seen.Clear();
            snapshot = TransitionLocked(MediaDetectionState.WaitingForPageContext, "MediaDetection.NavigationStarted");
        }
        Publish(snapshot);
        return generation;
    }

    public async Task ObserveAsync(PlayUrlObservation observation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observation);
        MediaDetectionSnapshot snapshot;
        bool accepted;
        lock (_gate)
        {
            accepted = _page is not null && observation.NavigationGeneration == _generation;
            snapshot = accepted
                ? TransitionLocked(MediaDetectionState.CandidateFound, "MediaDetection.PlayUrlObserved")
                : AddDiagnosticLocked("MediaDetection.StaleObservationIgnored", MediaDetectionState.Observing);
        }
        Publish(snapshot);
        if (accepted) await DetectAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DetectAsync(CancellationToken cancellationToken = default)
    {
        PageIdentity page;
        long expectedGeneration;
        MediaDetectionSnapshot snapshot;
        lock (_gate)
        {
            if (_page is null) return;
            page = _page!;
            expectedGeneration = _generation;
            snapshot = TransitionLocked(MediaDetectionState.Resolving, "MediaDetection.ResolveStarted");
        }
        Publish(snapshot);

        var hadStrategyError = false;
        foreach (var strategy in _strategies)
        {
            MediaDetectionResult result;
            try
            {
                result = await strategy.TryResolveAsync(page, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                hadStrategyError = true;
                var stale = false;
                lock (_gate)
                {
                    if (expectedGeneration != _generation)
                    {
                        snapshot = AddDiagnosticLocked("MediaDetection.StaleResultIgnored", MediaDetectionState.Observing);
                        stale = true;
                    }
                    else
                    {
                        snapshot = AddDiagnosticLocked("MediaDetection.StrategyFailed", MediaDetectionState.Observing, exception.GetType().Name);
                    }
                }
                Publish(snapshot);
                if (stale) return;
                continue;
            }

            var stale = false;
            var shouldReturn = false;
            lock (_gate)
            {
                if (expectedGeneration != _generation)
                {
                    snapshot = AddDiagnosticLocked("MediaDetection.StaleResultIgnored", MediaDetectionState.Observing);
                    stale = true;
                }
                else if (result.Success)
                {
                    if (result.Fingerprint is not null && !_seen.Add(result.Fingerprint))
                    {
                        snapshot = AddDiagnosticLocked("MediaDetection.DuplicateIgnored", MediaDetectionState.Observing);
                    }
                    else
                    {
                        snapshot = TransitionLocked(MediaDetectionState.Ready, "MediaDetection.Ready", result.Fingerprint, result.Media);
                    }
                    shouldReturn = true;
                }
            }
            if (stale)
            {
                Publish(snapshot);
                return;
            }
            if (shouldReturn)
            {
                Publish(snapshot);
                return;
            }
        }

        lock (_gate)
        {
            if (expectedGeneration != _generation) return;
            snapshot = hadStrategyError
                ? TransitionLocked(MediaDetectionState.Error, "MediaDetection.StrategiesFailed", errorCode: "MEDIA_STRATEGY_FAILED")
                : TransitionLocked(MediaDetectionState.Unsupported, "MediaDetection.NotFound", errorCode: "MEDIA_NOT_FOUND");
        }
        Publish(snapshot);
    }

    public void Reset()
    {
        MediaDetectionSnapshot snapshot;
        lock (_gate)
        {
            _generation++;
            _page = null;
            _seen.Clear();
            _diagnostics.Clear();
            _snapshot = new(MediaDetectionState.Idle, null, null, null, null, []);
            snapshot = _snapshot;
        }
        Publish(snapshot);
    }

    private MediaDetectionSnapshot TransitionLocked(MediaDetectionState state, string eventCode, MediaFingerprint? fingerprint = null, object? media = null, string? errorCode = null)
    {
        AddDiagnosticCore(eventCode, state, errorCode);
        _snapshot = new(state, _page, fingerprint, media, errorCode, _diagnostics.ToArray());
        return _snapshot;
    }

    private MediaDetectionSnapshot AddDiagnosticLocked(string eventCode, MediaDetectionState state, string? detail = null)
    {
        AddDiagnosticCore(eventCode, state, detail);
        _snapshot = _snapshot with { Diagnostics = _diagnostics.ToArray() };
        return _snapshot;
    }

    private void AddDiagnosticCore(string eventCode, MediaDetectionState state, string? detail)
    {
        _diagnostics.Add(new DetectionDiagnostic(eventCode, state, DateTimeOffset.UtcNow, detail));
        if (_diagnostics.Count > DiagnosticLimit) _diagnostics.RemoveAt(0);
    }

    private void Publish(MediaDetectionSnapshot snapshot)
    {
        try
        {
            StateChanged?.Invoke(this, snapshot);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"NovaClip detection notification failed: {exception}");
        }
    }
}
