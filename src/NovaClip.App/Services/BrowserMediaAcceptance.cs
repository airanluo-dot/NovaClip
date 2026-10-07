using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using NovaClip.App.Pages;
using NovaClip.Contracts;
using NovaClip.Core;

namespace NovaClip.App;

// Opt-in acceptance drives the real browser and the same queue command as the UI.
// It records identities and state codes, never media URLs, cookies or page HTML.
internal static class BrowserMediaAcceptance
{
    private static readonly TimeSpan AcceptanceBudget = TimeSpan.FromSeconds(330);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, WriteIndented = true };
    private const string PlaybackScript = """
        (() => {
          const video = document.querySelector('video');
          if (video && !window.__novaClipAcceptancePlayAttempted) {
            window.__novaClipAcceptancePlayAttempted = true;
            video.muted = true;
            video.play().catch(error => {
              const names = ['NotAllowedError', 'NotSupportedError', 'AbortError'];
              window.__novaClipAcceptancePlayError = names.includes(error && error.name) ? error.name : 'OtherError';
            });
          }
          return {
            path: location.pathname,
            pageNumber: Number(new URL(location.href).searchParams.get('p') || 1),
            documentState: document.readyState,
            hasVideo: !!video,
            paused: video ? video.paused : true,
            readyState: video ? video.readyState : 0,
            currentTime: video && Number.isFinite(video.currentTime) ? video.currentTime : 0,
            errorCode: video && video.error ? video.error.code : null,
            playAttemptError: window.__novaClipAcceptancePlayError || null
          };
        })()
        """;

    public static async Task RunAsync(BrowserPage browser, string input)
    {
        var report = new AcceptanceReport
        {
            Version = AppServices.CurrentVersion,
            BuildCommit = Environment.GetEnvironmentVariable("NOVACLIP_BUILD_COMMIT"),
            BaselineHarnessOverlay = Environment.GetEnvironmentVariable("NOVACLIP_ACCEPTANCE_BASELINE") == "1",
            StartedAt = DateTimeOffset.UtcNow
        };
        var clock = Stopwatch.StartNew();
        using var process = Process.GetCurrentProcess();
        var startingCpu = process.TotalProcessorTime;
        var originalSettings = AppServices.Settings.Capture();
        Guid? downloadId = null;
        try
        {
            var uri = ValidateVideoUri(input);
            report.PagePath = uri.AbsolutePath;
            report.PageNumber = GetPageNumber(uri);
            var outputRoot = Path.Combine(AppContext.BaseDirectory, "media-acceptance");
            Directory.CreateDirectory(outputRoot);
            report.DownloadDirectoryRelative = Path.Combine("downloads", Guid.NewGuid().ToString("N"));
            AppServices.Settings.DownloadDirectory = Path.Combine(outputRoot, report.DownloadDirectoryRelative);
            AppServices.Settings.DefaultQuality = "720P";
            AppServices.Settings.DefaultCodec = "AVC";
            AppServices.Settings.MergeAfterDownload = true;
            AppServices.Settings.DeleteTemporaryFilesAfterMerge = true;
            AppServices.Settings.MaxRetryAttempts = 2;
            var ffmpegPath = Environment.GetEnvironmentVariable("NOVACLIP_MEDIA_ACCEPTANCE_FFMPEG");
            if (!string.IsNullOrWhiteSpace(ffmpegPath))
            {
                if (!Path.IsPathRooted(ffmpegPath) || !File.Exists(ffmpegPath))
                    throw new AcceptanceFailure("ACCEPTANCE_FFMPEG_CONFIG_INVALID", blocked: true);
                AppServices.Settings.FfmpegPath = ffmpegPath;
                report.FfmpegExplicitlyConfigured = true;
            }

            var initializationDeadline = clock.Elapsed + TimeSpan.FromSeconds(25);
            while (!browser.IsPageBridgeReady && clock.Elapsed < initializationDeadline)
                await Task.Delay(250);
            if (!browser.IsPageBridgeReady) throw new AcceptanceFailure("WEBVIEW_BRIDGE_NOT_READY", blocked: true);

            browser.NavigateAddress(uri.ToString());
            Record(report, clock, "AddressBarNavigation", browser.AcceptanceSnapshot);
            var readyMedia = await WaitForReadyPlaybackAsync(browser, uri, report, clock, "Initial", TimeSpan.FromSeconds(75));
            report.DetectionElapsedMs = clock.Elapsed.TotalMilliseconds;
            report.Media = new MediaEvidence(
                readyMedia.Bvid, readyMedia.Aid, readyMedia.Cid, readyMedia.Source.ToString(),
                readyMedia.Tracks.Count(track => track.Type == TrackType.Video),
                readyMedia.Tracks.Count(track => track.Type == TrackType.Audio),
                readyMedia.LegacySegments.Count,
                readyMedia.Tracks.Where(track => track.Type == TrackType.Video).Select(track => track.QualityId).Distinct().ToArray());
            if (!AppServices.Ffmpeg.IsAvailable) throw new AcceptanceFailure("ACCEPTANCE_FFMPEG_UNAVAILABLE", blocked: true);

            var initialCid = readyMedia.Cid;
            var generation = browser.AcceptanceSnapshot.Page?.NavigationGeneration ?? 0;
            browser.Reload();
            readyMedia = await WaitForReadyPlaybackAsync(browser, uri, report, clock, "Refresh", TimeSpan.FromSeconds(35), generation);
            if (readyMedia.Cid != initialCid) throw new AcceptanceFailure("REFRESH_MEDIA_IDENTITY_CHANGED");

            // Establish a known history entry rather than depending on the user's
            // startup preference. Each home visit must clear the old media card.
            browser.NavigateAddress("https://www.bilibili.com/");
            await WaitForHomeAsync(browser, report, clock, "Home");
            generation = browser.AcceptanceSnapshot.Page?.NavigationGeneration ?? 0;
            browser.GoBack();
            readyMedia = await WaitForReadyPlaybackAsync(browser, uri, report, clock, "Back", TimeSpan.FromSeconds(35), generation);
            browser.GoForward();
            await WaitForHomeAsync(browser, report, clock, "Forward");
            generation = browser.AcceptanceSnapshot.Page?.NavigationGeneration ?? 0;
            browser.GoBack();
            readyMedia = await WaitForReadyPlaybackAsync(browser, uri, report, clock, "ReturnToDownload", TimeSpan.FromSeconds(35), generation);
            if (readyMedia.Cid != initialCid) throw new AcceptanceFailure("HISTORY_MEDIA_IDENTITY_CHANGED");

            browser.SelectLowestAcceptanceQuality();
            downloadId = await browser.EnqueueCurrentMediaAsync();
            if (downloadId is null) throw new AcceptanceFailure("MEDIA_UI_ENQUEUE_REJECTED");
            var downloadDeadline = Deadline(clock, TimeSpan.FromMinutes(4));
            DownloadTaskSnapshot? task = null;
            DownloadTaskState? previousDownloadState = null;
            while (clock.Elapsed < downloadDeadline)
            {
                task = AppServices.Downloads.GetTasks().FirstOrDefault(candidate => candidate.Id == downloadId);
                if (task is not null && task.State != previousDownloadState)
                {
                    report.Events.Add(new AcceptanceEvent(clock.Elapsed.TotalMilliseconds, "DownloadState", task.State.ToString(), ErrorCode: task.ErrorCode));
                    previousDownloadState = task.State;
                }
                if (task?.State is DownloadTaskState.Completed or DownloadTaskState.Failed or DownloadTaskState.Cancelled) break;
                await Task.Delay(500);
            }
            if (task?.State != DownloadTaskState.Completed)
                throw new AcceptanceFailure(task?.ErrorCode ?? "REAL_DOWNLOAD_NOT_COMPLETED");
            if (!File.Exists(task.OutputPath) || new FileInfo(task.OutputPath).Length == 0)
                throw new AcceptanceFailure("COMPLETED_OUTPUT_MISSING");
            report.OutputBytes = new FileInfo(task.OutputPath).Length;
            using (var stream = File.OpenRead(task.OutputPath))
                report.OutputSha256 = Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
            report.Status = "passed";
            report.ResultCode = "REAL_PLAYBACK_DETECTION_DOWNLOAD_COMPLETED";
        }
        catch (AcceptanceFailure failure)
        {
            report.Status = failure.Blocked ? "blocked" : "failed";
            report.ResultCode = failure.Message;
        }
        catch (Exception exception)
        {
            report.Status = "failed";
            report.ResultCode = "ACCEPTANCE_EXCEPTION_" + exception.GetType().Name;
        }
        finally
        {
            report.FinalDetection = SafeSnapshot(browser.AcceptanceSnapshot, browser.IsMediaCardReady);
            if (downloadId is Guid id)
            {
                var task = AppServices.Downloads.GetTasks().FirstOrDefault(candidate => candidate.Id == id);
                if (task is not null && task.State is not (DownloadTaskState.Completed or DownloadTaskState.Failed or DownloadTaskState.Cancelled))
                {
                    try { await AppServices.Downloads.CancelAsync(id); }
                    catch (Exception exception) { report.CleanupCode = exception.GetType().Name; }
                }
            }
            AppServices.Settings.Restore(originalSettings);
            process.Refresh();
            report.ElapsedMs = clock.Elapsed.TotalMilliseconds;
            report.AppCpuMs = (process.TotalProcessorTime - startingCpu).TotalMilliseconds;
            report.AppPeakWorkingSetBytes = process.PeakWorkingSet64;
            report.CompletedAt = DateTimeOffset.UtcNow;
            var directory = Path.Combine(AppContext.BaseDirectory, "media-acceptance");
            Directory.CreateDirectory(directory);
            var resultPath = Path.Combine(directory, "result.json");
            var temporaryPath = resultPath + ".tmp";
            await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(report, JsonOptions));
            File.Move(temporaryPath, resultPath, overwrite: true);
            StartupDiagnostics.Info($"MediaAcceptance.Completed:{report.Status}:{report.ResultCode}");
        }
    }

    private static async Task<MediaDescriptor> WaitForReadyPlaybackAsync(BrowserPage browser, Uri uri, AcceptanceReport report,
        Stopwatch clock, string phase, TimeSpan budget, long? afterGeneration = null)
    {
        var deadline = Deadline(clock, budget);
        double? previousTime = null;
        var playbackObserved = false;
        MediaDescriptor? readyMedia = null;
        string? previousStateKey = null;
        while (clock.Elapsed < deadline)
        {
            var snapshot = browser.AcceptanceSnapshot;
            var stateKey = $"{snapshot.State}:{snapshot.Page?.NavigationGeneration}:{snapshot.Page?.Bvid}:{snapshot.Page?.Cid}:{snapshot.ErrorCode}:{browser.IsMediaCardReady}";
            if (stateKey != previousStateKey)
            {
                Record(report, clock, phase + "DetectionState", snapshot, browser.IsMediaCardReady);
                previousStateKey = stateKey;
            }
            if (browser.IsPageBridgeReady && (afterGeneration is null || snapshot.Page?.NavigationGeneration > afterGeneration))
            {
                var playback = JsonSerializer.Deserialize<PlaybackEvidence>(await browser.ExecuteAcceptanceScriptAsync(PlaybackScript), JsonOptions);
                snapshot = browser.AcceptanceSnapshot;
                if (playback is not null && IsRequestedPlayback(playback, uri, snapshot.Page))
                {
                    report.Playback = playback;
                    if (!playback.Paused && previousTime is double time && playback.CurrentTime > time + 0.2)
                        playbackObserved = true;
                    previousTime = playback.CurrentTime;
                }
                if (snapshot.State == MediaDetectionState.Ready && snapshot.Media is MediaDescriptor media &&
                    browser.IsMediaCardReady && MatchesRequestedMedia(snapshot.Page, media, uri))
                    readyMedia = media;
                else readyMedia = null;
                if (playbackObserved && readyMedia is not null)
                {
                    report.PlaybackObserved = true;
                    report.NavigationChecks.Add(new NavigationEvidence(phase, true, true, snapshot.Page?.NavigationGeneration, readyMedia.Cid));
                    return readyMedia;
                }
            }
            var beforeDelay = clock.Elapsed;
            await Task.Delay(500);
            report.UiProbeDelayOverrunsMs.Add(Math.Max(0, (clock.Elapsed - beforeDelay).TotalMilliseconds - 500));
        }
        report.PlaybackObserved |= playbackObserved;
        report.NavigationChecks.Add(new NavigationEvidence(phase, playbackObserved, false, browser.AcceptanceSnapshot.Page?.NavigationGeneration, browser.AcceptanceSnapshot.Page?.Cid));
        if (!playbackObserved) throw new AcceptanceFailure(phase + "_REAL_PLAYBACK_NOT_OBSERVED", blocked: true);
        throw new AcceptanceFailure(phase + "_PLAYBACK_OBSERVED_MEDIA_NOT_READY");
    }

    private static async Task WaitForHomeAsync(BrowserPage browser, AcceptanceReport report, Stopwatch clock, string phase)
    {
        var deadline = Deadline(clock, TimeSpan.FromSeconds(25));
        while (clock.Elapsed < deadline)
        {
            var snapshot = browser.AcceptanceSnapshot;
            if (browser.IsPageBridgeReady && snapshot.Page is not null &&
                Uri.TryCreate(snapshot.Page.PageUrl, UriKind.Absolute, out var page) && page.AbsolutePath == "/" &&
                snapshot.State != MediaDetectionState.Ready && !browser.IsMediaCardReady)
            {
                var playback = JsonSerializer.Deserialize<PlaybackEvidence>(await browser.ExecuteAcceptanceScriptAsync(PlaybackScript), JsonOptions);
                if (playback?.Path == "/")
                {
                    Record(report, clock, phase + "ClearedMedia", snapshot);
                    report.NavigationChecks.Add(new NavigationEvidence(phase, false, true, snapshot.Page.NavigationGeneration, snapshot.Page.Cid));
                    return;
                }
            }
            await Task.Delay(250);
        }
        throw new AcceptanceFailure(phase + "_HOME_MEDIA_DID_NOT_CLEAR");
    }

    private static TimeSpan Deadline(Stopwatch clock, TimeSpan duration) =>
        TimeSpan.FromTicks(Math.Min((clock.Elapsed + duration).Ticks, AcceptanceBudget.Ticks));

    private static Uri ValidateVideoUri(string input)
    {
        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            !uri.Host.Equals("www.bilibili.com", StringComparison.OrdinalIgnoreCase) ||
            !System.Text.RegularExpressions.Regex.IsMatch(uri.AbsolutePath, @"^/video/(BV[0-9A-Za-z]+|av[0-9]+)/?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase) ||
            !string.IsNullOrEmpty(uri.UserInfo))
            throw new AcceptanceFailure("ACCEPTANCE_REQUIRES_NORMAL_BILIBILI_VIDEO_URL", blocked: true);
        // Keep only the public multipart selector. Tracking/authentication queries never reach the report.
        return new UriBuilder(uri) { Query = "p=" + GetPageNumber(uri), Fragment = string.Empty }.Uri;
    }

    private static int GetPageNumber(Uri uri)
    {
        foreach (var component in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = component.Split('=', 2);
            if (pair[0] == "p" && pair.Length == 2 && int.TryParse(pair[1], out var value) && value > 0) return value;
        }
        return 1;
    }

    private static bool IsRequestedPlayback(PlaybackEvidence evidence, Uri requested, PageIdentity? page)
    {
        if (evidence.PageNumber != GetPageNumber(requested) || !evidence.HasVideo) return false;
        if (string.Equals(evidence.Path.TrimEnd('/'), requested.AbsolutePath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)) return true;
        var id = requested.AbsolutePath.TrimEnd('/').Split('/')[^1];
        // Bilibili can canonicalize an AV address to BV. Match its trusted current
        // page AID as well as the actual playback path, rather than rejecting the redirect.
        return id.StartsWith("av", StringComparison.OrdinalIgnoreCase) && long.TryParse(id[2..], out var aid) &&
            page?.Aid == aid && Uri.TryCreate(page.PageUrl, UriKind.Absolute, out var canonical) &&
            string.Equals(canonical.AbsolutePath.TrimEnd('/'), evidence.Path.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesRequestedMedia(PageIdentity? page, MediaDescriptor media, Uri requested)
    {
        if (page is null || !Uri.TryCreate(page.PageUrl, UriKind.Absolute, out var pageUri) ||
            !pageUri.Host.Equals("www.bilibili.com", StringComparison.OrdinalIgnoreCase) ||
            GetPageNumber(pageUri) != GetPageNumber(requested) ||
            page.Cid is null || page.Cid != media.Cid) return false;
        var id = requested.AbsolutePath.TrimEnd('/').Split('/')[^1];
        return id.StartsWith("BV", StringComparison.OrdinalIgnoreCase)
            ? string.Equals(pageUri.AbsolutePath.TrimEnd('/'), requested.AbsolutePath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) &&
                string.Equals(id, media.Bvid, StringComparison.OrdinalIgnoreCase) && string.Equals(page.Bvid, media.Bvid, StringComparison.OrdinalIgnoreCase)
            : long.TryParse(id[2..], out var aid) && media.Aid == aid && page.Aid == aid;
    }

    private static DetectionEvidence SafeSnapshot(MediaDetectionSnapshot snapshot, bool cardReady) =>
        new(snapshot.State.ToString(), snapshot.Page?.NavigationGeneration, snapshot.Page?.Bvid, snapshot.Page?.Aid,
            snapshot.Page?.Cid, snapshot.Page is not null && Uri.TryCreate(snapshot.Page.PageUrl, UriKind.Absolute, out var pageUri) ? GetPageNumber(pageUri) : null, cardReady, snapshot.ErrorCode,
            snapshot.Diagnostics.TakeLast(30).Select(item => new DiagnosticEvidence(item.EventCode, item.State.ToString(), item.Timestamp)).ToArray());

    private static void Record(AcceptanceReport report, Stopwatch clock, string phase, MediaDetectionSnapshot snapshot, bool cardReady = false) =>
        report.Events.Add(new AcceptanceEvent(clock.Elapsed.TotalMilliseconds, phase, snapshot.State.ToString(),
            snapshot.Page?.NavigationGeneration, snapshot.Page?.Bvid, snapshot.Page?.Cid, cardReady, snapshot.ErrorCode));

    private sealed class AcceptanceFailure(string code, bool blocked = false) : Exception(code)
    {
        public bool Blocked { get; } = blocked;
    }

    private sealed class AcceptanceReport
    {
        public int SchemaVersion { get; } = 1;
        public string Version { get; set; } = string.Empty;
        public string? BuildCommit { get; set; }
        public string BuildCommitSource { get; } = "WorkflowDeclared";
        public bool BaselineHarnessOverlay { get; set; }
        public bool FfmpegExplicitlyConfigured { get; set; }
        public DateTimeOffset StartedAt { get; set; }
        public DateTimeOffset CompletedAt { get; set; }
        public string? PagePath { get; set; }
        public int PageNumber { get; set; }
        public string Status { get; set; } = "failed";
        public string? ResultCode { get; set; }
        public bool PlaybackObserved { get; set; }
        public PlaybackEvidence? Playback { get; set; }
        public DetectionEvidence? FinalDetection { get; set; }
        public MediaEvidence? Media { get; set; }
        public List<AcceptanceEvent> Events { get; } = [];
        public List<NavigationEvidence> NavigationChecks { get; } = [];
        public List<double> UiProbeDelayOverrunsMs { get; } = [];
        public double? DetectionElapsedMs { get; set; }
        public double ElapsedMs { get; set; }
        public double AppCpuMs { get; set; }
        public long AppPeakWorkingSetBytes { get; set; }
        public long? OutputBytes { get; set; }
        public string? OutputSha256 { get; set; }
        public string? DownloadDirectoryRelative { get; set; }
        public string? CleanupCode { get; set; }
    }

    private sealed record PlaybackEvidence(string Path, int PageNumber, string DocumentState, bool HasVideo, bool Paused, int ReadyState, double CurrentTime, int? ErrorCode, string? PlayAttemptError);
    private sealed record DiagnosticEvidence(string EventCode, string State, DateTimeOffset Timestamp);
    private sealed record DetectionEvidence(string State, long? Generation, string? Bvid, long? Aid, long? Cid, int? PageNumber, bool CardReady, string? ErrorCode, DiagnosticEvidence[] Diagnostics);
    private sealed record MediaEvidence(string? Bvid, long? Aid, long? Cid, string Source, int VideoTracks, int AudioTracks, int LegacySegments, int?[] Qualities);
    private sealed record NavigationEvidence(string Phase, bool PlaybackObserved, bool Converged, long? Generation, long? Cid);
    private sealed record AcceptanceEvent(double ElapsedMs, string Phase, string State, long? Generation = null, string? Bvid = null, long? Cid = null, bool CardReady = false, string? ErrorCode = null);
}
