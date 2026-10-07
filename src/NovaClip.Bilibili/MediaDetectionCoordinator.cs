using NovaClip.Contracts;
using NovaClip.Core;

namespace NovaClip.Bilibili;

public sealed class MediaDetectionCoordinator : IMediaDetectionCoordinator
{
    private const int DiagnosticLimit = 200;
    private const int PendingObservationLimit = 4;
    private readonly IReadOnlyList<IMediaDetectionStrategy> _strategies;
    private readonly PlayUrlNormalizer _normalizer = new();
    private readonly List<DetectionDiagnostic> _diagnostics = [];
    private readonly List<PendingObservation> _pending = [];
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
        get { lock (_gate) return _snapshot; }
    }

    public long BeginNavigation(Uri uri)
    {
        if (uri is null || !uri.IsAbsoluteUri || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("A valid HTTP(S) navigation URI is required.", nameof(uri));

        MediaDetectionSnapshot snapshot;
        long generation;
        lock (_gate)
        {
            generation = ++_generation;
            _page = BilibiliMediaIdentity.FromUri(uri, generation);
            _pending.Clear();
            snapshot = TransitionLocked(MediaDetectionState.WaitingForPageContext, "MediaDetection.NavigationStarted");
        }
        Publish(snapshot);
        return generation;
    }

    public long UpdatePageContext(PageIdentity page)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (!Uri.TryCreate(page.PageUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("The page context must contain an absolute HTTP(S) URL.", nameof(page));

        MediaDetectionSnapshot snapshot;
        long generation;
        lock (_gate)
        {
            var fromUrl = BilibiliMediaIdentity.FromUri(uri, 0);
            page = BilibiliMediaIdentity.Merge(fromUrl, page, 0);
            var identityChanged = _page is null || !BilibiliMediaIdentity.CanEnrich(_page, page);
            if (identityChanged)
            {
                generation = ++_generation;
                _pending.Clear();
                _page = page with { NavigationGeneration = generation };
                snapshot = TransitionLocked(MediaDetectionState.Observing, "MediaDetection.PageContextChanged");
            }
            else
            {
                generation = _generation == 0 ? ++_generation : _generation;
                _page = BilibiliMediaIdentity.Merge(_page!, page, generation);
                // Missing BV/AV/CID values becoming known are enrichment of this
                // navigation, not a reason to discard a response already in flight.
                snapshot = _snapshot with
                {
                    Page = _page,
                    Media = _snapshot.Media is MediaDescriptor media ? EnrichMedia(media, _page) : _snapshot.Media,
                    Fingerprint = _snapshot.Fingerprint is null ? null : EnrichFingerprint(_snapshot.Fingerprint, _page)
                };
                _snapshot = snapshot;
                if (snapshot.State == MediaDetectionState.WaitingForPageContext)
                    snapshot = TransitionLocked(MediaDetectionState.Observing, "MediaDetection.PageContextAccepted");
            }
            snapshot = ReconcilePendingLocked(snapshot);
        }
        Publish(snapshot);
        return generation;
    }

    public bool TryAcceptResult(long generation, MediaDetectionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        MediaDetectionSnapshot snapshot;
        bool accepted;
        lock (_gate) accepted = AcceptResultLocked(generation, result, out snapshot);
        Publish(snapshot);
        return accepted;
    }

    public Task ObserveAsync(PlayUrlObservation observation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observation);
        cancellationToken.ThrowIfCancellationRequested();
        PageIdentity? page;
        lock (_gate) page = observation.NavigationGeneration == _generation ? _page : null;
        if (page is null)
        {
            RecordIgnored("MediaDetection.StaleObservationIgnored");
            return Task.CompletedTask;
        }
        if (observation.Endpoint.Scheme is not ("http" or "https") ||
            !IsBilibiliHost(observation.Endpoint.Host) ||
            !observation.Endpoint.AbsolutePath.Contains("/playurl", StringComparison.OrdinalIgnoreCase))
        {
            RecordIgnored("MediaDetection.UnrelatedObservationIgnored");
            return Task.CompletedTask;
        }
        var evidence = BilibiliMediaIdentity.FromEndpoint(observation.Endpoint, page.PageUrl, observation.NavigationGeneration);
        NormalizeObservation(observation.Json, evidence, page, ResolverStrategy.PlayUrlResponse, cancellationToken);
        return Task.CompletedTask;
    }

    public Task ObservePageDataAsync(string json, PageIdentity evidence, long generation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        cancellationToken.ThrowIfCancellationRequested();
        PageIdentity? page;
        lock (_gate) page = generation == _generation ? _page : null;
        if (page is null)
        {
            RecordIgnored("MediaDetection.StaleObservationIgnored");
            return Task.CompletedTask;
        }
        if (!BilibiliMediaIdentity.CanEnrich(page, evidence))
        {
            RecordIgnored("MediaDetection.UnrelatedObservationIgnored");
            return Task.CompletedTask;
        }
        NormalizeObservation(json, evidence with { NavigationGeneration = generation }, page, ResolverStrategy.PageData, cancellationToken);
        return Task.CompletedTask;
    }

    /// <summary>Ends a finite browser evidence/fallback pass without an indefinite spinner.</summary>
    public void CompleteObservation(long generation)
    {
        MediaDetectionSnapshot? snapshot = null;
        lock (_gate)
        {
            if (_page is null || generation != _generation || _snapshot.State is MediaDetectionState.Ready or MediaDetectionState.PermissionDenied or MediaDetectionState.Error or MediaDetectionState.Unsupported) return;
            snapshot = TransitionLocked(MediaDetectionState.Unsupported, "MediaDetection.NotFound",
                errorCode: _pending.Count > 0 ? "MEDIA_CONTEXT_INCOMPLETE" : "MEDIA_NOT_FOUND");
        }
        Publish(snapshot);
    }

    public void FailObservation(long generation, string errorCode, MediaDetectionState state = MediaDetectionState.Error) =>
        TryAcceptResult(generation, new MediaDetectionResult(false, state, null, null, errorCode));

    private void NormalizeObservation(string json, PageIdentity evidence, PageIdentity page, ResolverStrategy source, CancellationToken cancellationToken)
    {
        var context = new PlayUrlContext(page.PageUrl, page.Title ?? "Bilibili media", evidence.Bvid, evidence.Aid,
            evidence.Cid, evidence.EpisodeId, page.EpisodeTitle, page.IsBangumi, source);
        var resolved = _normalizer.Normalize(json, context);
        cancellationToken.ThrowIfCancellationRequested();
        if (resolved.Error?.Code == "RESOLVE_IDENTITY_MISMATCH")
        {
            RecordIgnored("MediaDetection.UnrelatedObservationIgnored");
            return;
        }
        var media = resolved.Media;
        if (media is not null)
            evidence = evidence with { Bvid = media.Bvid, Aid = media.Aid, Cid = media.Cid, EpisodeId = media.EpisodeId };
        var track = media?.VideoTrack ?? media?.AudioTrack;
        var result = new MediaDetectionResult(resolved.IsSuccess,
            resolved.IsSuccess ? MediaDetectionState.Ready : resolved.Error?.Code is "RESOLVE_VIP_REQUIRED" or "RESOLVE_LOGIN_REQUIRED"
                ? MediaDetectionState.PermissionDenied : resolved.Error?.Code is "RESOLVE_PLAYURL_NOT_FOUND" or "RESOLVE_DRM_UNSUPPORTED" ? MediaDetectionState.Unsupported : MediaDetectionState.Error,
            new MediaFingerprint(page.PageUrl, evidence.Bvid, evidence.Aid, evidence.Cid, evidence.EpisodeId,
                track?.QualityId, track?.Codec, evidence.NavigationGeneration), media, resolved.Error?.Code);

        MediaDetectionSnapshot snapshot;
        lock (_gate)
        {
            if (_page is null || evidence.NavigationGeneration != _generation)
                snapshot = AddDiagnosticLocked("MediaDetection.StaleObservationIgnored", MediaDetectionState.Observing);
            else if (BilibiliMediaIdentity.Conflicts(_page, evidence))
                snapshot = AddDiagnosticLocked("MediaDetection.UnrelatedObservationIgnored", MediaDetectionState.Observing);
            else if (!BilibiliMediaIdentity.HasMatchingEvidence(_page, evidence))
            {
                // Retain only normalized tracks, not raw response bodies or credentials.
                // Context arrival reconciles the bounded queue synchronously.
                if (_pending.Count == PendingObservationLimit) _pending.RemoveAt(0);
                _pending.Add(new PendingObservation(evidence, result));
                snapshot = _snapshot.State == MediaDetectionState.Ready
                    ? AddDiagnosticLocked("MediaDetection.AwaitingIdentity", MediaDetectionState.WaitingForPageContext)
                    : TransitionLocked(MediaDetectionState.WaitingForPageContext, "MediaDetection.AwaitingIdentity");
            }
            else
                AcceptResultLocked(evidence.NavigationGeneration, result, out snapshot);
        }
        Publish(snapshot);
    }

    private MediaDetectionSnapshot ReconcilePendingLocked(MediaDetectionSnapshot snapshot)
    {
        for (var index = 0; index < _pending.Count;)
        {
            var pending = _pending[index];
            if (BilibiliMediaIdentity.Conflicts(_page!, pending.Evidence))
            {
                _pending.RemoveAt(index);
                snapshot = AddDiagnosticLocked("MediaDetection.UnrelatedObservationIgnored", MediaDetectionState.Observing);
            }
            else if (BilibiliMediaIdentity.HasMatchingEvidence(_page!, pending.Evidence))
            {
                _pending.RemoveAt(index);
                AcceptResultLocked(_generation, pending.Result, out snapshot);
            }
            else index++;
        }
        return snapshot;
    }

    private bool AcceptResultLocked(long generation, MediaDetectionResult result, out MediaDetectionSnapshot snapshot)
    {
        if (_page is null || generation != _generation)
        {
            snapshot = AddDiagnosticLocked("MediaDetection.StaleResultIgnored", MediaDetectionState.Observing);
            return false;
        }
        var evidence = result.Fingerprint is { } fingerprint
            ? new PageIdentity(fingerprint.PageUrl, fingerprint.Bvid, fingerprint.Aid, fingerprint.Cid, fingerprint.EpisodeId, generation)
            : null;
        var unrelatedFingerprint = evidence is not null && !BilibiliMediaIdentity.CanEnrich(_page, evidence);
        var missingIdentity = result.Success && (result.Media is null || evidence is null || !BilibiliMediaIdentity.HasMatchingEvidence(_page, evidence));
        var unrelatedMedia = result.Media is MediaDescriptor observedMedia && !BilibiliMediaIdentity.CanEnrich(_page,
            new PageIdentity(observedMedia.PageUrl, observedMedia.Bvid, observedMedia.Aid, observedMedia.Cid, observedMedia.EpisodeId, generation));
        if (unrelatedFingerprint || missingIdentity || unrelatedMedia)
        {
            snapshot = AddDiagnosticLocked("MediaDetection.UnrelatedResultIgnored", MediaDetectionState.Observing);
            return false;
        }
        if (!result.Success)
        {
            // A refresh or secondary API error cannot hide known usable tracks.
            snapshot = _snapshot.State == MediaDetectionState.Ready
                ? AddDiagnosticLocked("MediaDetection.ResolveFailed", result.State, result.ErrorCode)
                : TransitionLocked(result.State == MediaDetectionState.Idle ? MediaDetectionState.Error : result.State,
                    "MediaDetection.ResolveFailed", errorCode: result.ErrorCode ?? "MEDIA_RESOLVE_FAILED");
            return true;
        }
        var enrichedFingerprint = result.Fingerprint is null ? null : EnrichFingerprint(result.Fingerprint, _page);
        var enrichedMedia = result.Media is MediaDescriptor descriptor ? EnrichMedia(descriptor, _page) : result.Media;
        if (_snapshot.State == MediaDetectionState.Ready && _snapshot.Fingerprint == enrichedFingerprint &&
            EquivalentMedia(_snapshot.Media, enrichedMedia))
        {
            snapshot = AddDiagnosticLocked("MediaDetection.DuplicateIgnored", MediaDetectionState.Observing);
            return false;
        }
        snapshot = TransitionLocked(MediaDetectionState.Ready, "MediaDetection.Ready", enrichedFingerprint, enrichedMedia);
        return true;
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
            snapshot = _snapshot.State == MediaDetectionState.Ready
                ? AddDiagnosticLocked("MediaDetection.ResolveStarted", MediaDetectionState.Resolving)
                : TransitionLocked(MediaDetectionState.Resolving, "MediaDetection.ResolveStarted");
        }
        Publish(snapshot);
        var hadStrategyError = false;
        MediaDetectionResult? explicitFailure = null;
        foreach (var strategy in _strategies)
        {
            MediaDetectionResult result;
            try { result = await strategy.TryResolveAsync(page, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                hadStrategyError = true;
                lock (_gate)
                {
                    if (expectedGeneration != _generation) return;
                    snapshot = AddDiagnosticLocked("MediaDetection.StrategyFailed", MediaDetectionState.Observing, exception.GetType().Name);
                }
                Publish(snapshot);
                continue;
            }
            lock (_gate) { if (expectedGeneration != _generation) return; }
            if (result.Success)
            {
                if (TryAcceptResult(expectedGeneration, result)) return;
            }
            else if (result.State is MediaDetectionState.PermissionDenied or MediaDetectionState.Error)
                explicitFailure = result;
        }
        lock (_gate)
        {
            if (expectedGeneration != _generation || _snapshot.State == MediaDetectionState.Ready) return;
            if (explicitFailure is not null) AcceptResultLocked(expectedGeneration, explicitFailure, out snapshot);
            else snapshot = hadStrategyError
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
            _pending.Clear();
            _diagnostics.Clear();
            _snapshot = new(MediaDetectionState.Idle, null, null, null, null, []);
            snapshot = _snapshot;
        }
        Publish(snapshot);
    }

    private static MediaFingerprint EnrichFingerprint(MediaFingerprint fingerprint, PageIdentity page) => fingerprint with
    {
        PageUrl = page.PageUrl, Bvid = page.Bvid ?? fingerprint.Bvid, Aid = page.Aid ?? fingerprint.Aid,
        Cid = page.Cid ?? fingerprint.Cid, EpisodeId = page.EpisodeId ?? fingerprint.EpisodeId,
        NavigationGeneration = page.NavigationGeneration
    };

    private static MediaDescriptor EnrichMedia(MediaDescriptor media, PageIdentity page)
    {
        if ((page.Title is null || page.Title == media.Title) && page.PageUrl == media.PageUrl &&
            (page.Bvid is null || page.Bvid == media.Bvid) && (page.Aid is null || page.Aid == media.Aid) &&
            (page.Cid is null || page.Cid == media.Cid) && (page.EpisodeId is null || page.EpisodeId == media.EpisodeId) &&
            (page.EpisodeTitle is null || page.EpisodeTitle == media.EpisodeTitle) && (!page.IsBangumi || media.IsBangumi)) return media;
        return new MediaDescriptor
        {
            Title = page.Title ?? media.Title, PageUrl = page.PageUrl,
            Bvid = page.Bvid ?? media.Bvid, Aid = page.Aid ?? media.Aid, Cid = page.Cid ?? media.Cid,
            EpisodeId = page.EpisodeId ?? media.EpisodeId, EpisodeTitle = page.EpisodeTitle ?? media.EpisodeTitle,
            IsBangumi = page.IsBangumi || media.IsBangumi, Source = media.Source,
            QualityOptions = media.QualityOptions, CodecOptions = media.CodecOptions,
            Tracks = media.Tracks, LegacySegments = media.LegacySegments
        };
    }

    private static bool EquivalentMedia(object? left, object? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is not MediaDescriptor a || right is not MediaDescriptor b) return false;
        return a.Title == b.Title && a.PageUrl == b.PageUrl && a.Bvid == b.Bvid && a.Aid == b.Aid && a.Cid == b.Cid &&
            a.EpisodeId == b.EpisodeId && a.EpisodeTitle == b.EpisodeTitle && a.IsBangumi == b.IsBangumi &&
            a.Tracks.Count == b.Tracks.Count && a.LegacySegments.Count == b.LegacySegments.Count &&
            a.QualityOptions.SequenceEqual(b.QualityOptions) && a.CodecOptions.SequenceEqual(b.CodecOptions) &&
            a.Tracks.Zip(b.Tracks).All(pair => EquivalentTrack(pair.First, pair.Second)) &&
            a.LegacySegments.Zip(b.LegacySegments).All(pair => pair.First.Index == pair.Second.Index &&
                pair.First.Size == pair.Second.Size && pair.First.DurationSeconds == pair.Second.DurationSeconds &&
                pair.First.Urls.SequenceEqual(pair.Second.Urls));
    }

    private static bool EquivalentTrack(MediaTrack left, MediaTrack right) => left.Type == right.Type &&
        left.TrackId == right.TrackId && left.QualityId == right.QualityId && left.CodecId == right.CodecId &&
        left.Codec == right.Codec && left.Size == right.Size && left.DurationSeconds == right.DurationSeconds &&
        left.Urls.SequenceEqual(right.Urls);

    private void RecordIgnored(string eventCode)
    {
        MediaDetectionSnapshot snapshot;
        lock (_gate) snapshot = AddDiagnosticLocked(eventCode, MediaDetectionState.Observing);
        Publish(snapshot);
    }

    private static bool IsBilibiliHost(string host) => host.TrimEnd('.').Equals("bilibili.com", StringComparison.OrdinalIgnoreCase) ||
        host.TrimEnd('.').EndsWith(".bilibili.com", StringComparison.OrdinalIgnoreCase);

    private MediaDetectionSnapshot TransitionLocked(MediaDetectionState state, string eventCode,
        MediaFingerprint? fingerprint = null, object? media = null, string? errorCode = null)
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
        catch (Exception exception) { System.Diagnostics.Debug.WriteLine("NovaClip detection notification failed: " + exception.GetType().Name); }
    }

    private sealed record PendingObservation(PageIdentity Evidence, MediaDetectionResult Result);
}
