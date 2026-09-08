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
        if (uri is null || !uri.IsAbsoluteUri || uri.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("A valid HTTP(S) navigation URI is required.", nameof(uri));
        }

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

    public long UpdatePageContext(PageIdentity page)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (!Uri.TryCreate(page.PageUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("The page context must contain an absolute HTTP(S) URL.", nameof(page));
        }

        MediaDetectionSnapshot snapshot;
        long generation;
        lock (_gate)
        {
            var identityChanged = _page is null || !SameIdentity(_page, page);
            if (identityChanged)
            {
                generation = ++_generation;
                _seen.Clear();
            }
            else
            {
                generation = _generation == 0 ? ++_generation : _generation;
            }

            _page = page with { PageUrl = uri.ToString(), NavigationGeneration = generation };
            snapshot = identityChanged
                ? TransitionLocked(MediaDetectionState.Observing, "MediaDetection.PageContextChanged")
                : _snapshot with { Page = _page };
            _snapshot = snapshot;
        }

        if (snapshot.State != MediaDetectionState.Idle) Publish(snapshot);
        return generation;
    }

    public bool TryAcceptResult(long generation, MediaDetectionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        MediaDetectionSnapshot snapshot;
        var accepted = false;
        lock (_gate)
        {
            if (_page is null || generation != _generation)
            {
                snapshot = AddDiagnosticLocked("MediaDetection.StaleResultIgnored", MediaDetectionState.Observing);
            }
            else if (!result.Success)
            {
                snapshot = TransitionLocked(
                    result.State == MediaDetectionState.Idle ? MediaDetectionState.Error : result.State,
                    "MediaDetection.ResolveFailed",
                    result.Fingerprint,
                    result.Media,
                    result.ErrorCode ?? "MEDIA_RESOLVE_FAILED");
                accepted = true;
            }
            else if (result.Fingerprint is not null && !_seen.Add(result.Fingerprint))
            {
                snapshot = AddDiagnosticLocked("MediaDetection.DuplicateIgnored", MediaDetectionState.Observing);
            }
            else
            {
                snapshot = TransitionLocked(MediaDetectionState.Ready, "MediaDetection.Ready", result.Fingerprint, result.Media);
                accepted = true;
            }
        }

        Publish(snapshot);
        return accepted;
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
            page = _page;
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
                var staleFailure = false;
                lock (_gate)
                {
                    if (expectedGeneration != _generation)
                    {
                        snapshot = AddDiagnosticLocked("MediaDetection.StaleResultIgnored", MediaDetectionState.Observing);
                        staleFailure = true;
                    }
                    else
                    {
                        snapshot = AddDiagnosticLocked("MediaDetection.StrategyFailed", MediaDetectionState.Observing, exception.GetType().Name);
                    }
                }

                Publish(snapshot);
                if (staleFailure) return;
                continue;
            }

            if (expectedGeneration != _generation)
            {
                lock (_gate) snapshot = AddDiagnosticLocked("MediaDetection.StaleResultIgnored", MediaDetectionState.Observing);
                Publish(snapshot);
                return;
            }

            if (result.Success)
            {
                if (TryAcceptResult(expectedGeneration, result)) return;
                continue;
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

    private static bool SameIdentity(PageIdentity left, PageIdentity right) =>
        string.Equals(left.PageUrl, right.PageUrl, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.Bvid, right.Bvid, StringComparison.OrdinalIgnoreCase) &&
        left.Aid == right.Aid &&
        left.Cid == right.Cid &&
        left.EpisodeId == right.EpisodeId;

    private MediaDetectionSnapshot TransitionLocked(
        MediaDetectionState state,
        string eventCode,
        MediaFingerprint? fingerprint = null,
        object? media = null,
        string? errorCode = null)
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
        try { StateChanged?.Invoke(this, snapshot); }
        catch (Exception exception) { System.Diagnostics.Debug.WriteLine("NovaClip detection notification failed: " + exception); }
    }
}
