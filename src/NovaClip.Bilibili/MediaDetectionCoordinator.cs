using NovaClip.Contracts;

namespace NovaClip.Bilibili;

public sealed class MediaDetectionCoordinator : IMediaDetectionCoordinator
{
    private const int DiagnosticLimit = 200;
    private readonly IReadOnlyList<IMediaDetectionStrategy> _strategies;
    private readonly List<DetectionDiagnostic> _diagnostics = [];
    private readonly HashSet<MediaFingerprint> _seen = [];
    private PageIdentity? _page;
    private long _generation;

    public MediaDetectionCoordinator(IEnumerable<IMediaDetectionStrategy> strategies) => _strategies = strategies.ToArray();

    public event EventHandler<MediaDetectionSnapshot>? StateChanged;
    public MediaDetectionSnapshot Snapshot { get; private set; } = new(MediaDetectionState.Idle, null, null, null, null, []);

    public long BeginNavigation(Uri uri)
    {
        _generation++;
        _page = new PageIdentity(uri.ToString(), null, null, null, null, _generation);
        _seen.Clear();
        Transition(MediaDetectionState.WaitingForPageContext, "MediaDetection.NavigationStarted");
        return _generation;
    }

    public async Task ObserveAsync(PlayUrlObservation observation, CancellationToken cancellationToken = default)
    {
        if (_page is null || observation.NavigationGeneration != _generation)
        {
            AddDiagnostic("MediaDetection.StaleObservationIgnored", MediaDetectionState.Observing);
            return;
        }
        Transition(MediaDetectionState.CandidateFound, "MediaDetection.PlayUrlObserved");
        await DetectAsync(cancellationToken);
    }

    public async Task DetectAsync(CancellationToken cancellationToken = default)
    {
        if (_page is null) return;
        var expectedGeneration = _generation;
        Transition(MediaDetectionState.Resolving, "MediaDetection.ResolveStarted");
        foreach (var strategy in _strategies)
        {
            var result = await strategy.TryResolveAsync(_page, cancellationToken).ConfigureAwait(false);
            if (expectedGeneration != _generation)
            {
                AddDiagnostic("MediaDetection.StaleResultIgnored", MediaDetectionState.Observing);
                return;
            }
            if (!result.Success) continue;
            if (result.Fingerprint is not null && !_seen.Add(result.Fingerprint))
            {
                AddDiagnostic("MediaDetection.DuplicateIgnored", MediaDetectionState.Observing);
                return;
            }
            Transition(MediaDetectionState.Ready, "MediaDetection.Ready", result.Fingerprint, result.Media);
            return;
        }
        Transition(MediaDetectionState.Unsupported, "MediaDetection.NotFound", errorCode: "MEDIA_NOT_FOUND");
    }

    public void Reset()
    {
        _page = null;
        _seen.Clear();
        _diagnostics.Clear();
        Snapshot = new(MediaDetectionState.Idle, null, null, null, null, []);
        StateChanged?.Invoke(this, Snapshot);
    }

    private void Transition(MediaDetectionState state, string eventCode, MediaFingerprint? fingerprint = null, object? media = null, string? errorCode = null)
    {
        AddDiagnostic(eventCode, state, errorCode);
        Snapshot = new(state, _page, fingerprint, media, errorCode, _diagnostics.ToArray());
        StateChanged?.Invoke(this, Snapshot);
    }

    private void AddDiagnostic(string eventCode, MediaDetectionState state, string? detail = null)
    {
        _diagnostics.Add(new DetectionDiagnostic(eventCode, state, DateTimeOffset.UtcNow, detail));
        if (_diagnostics.Count > DiagnosticLimit) _diagnostics.RemoveAt(0);
    }
}
