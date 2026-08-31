using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using NovaClip.Core;

namespace NovaClip.Infrastructure;

public sealed class HttpRangeDownloader : IDownloadEngine
{
    private const int BufferSize = 128 * 1024;
    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };
    private readonly HttpClient _httpClient;
    private readonly RetryExecutor _retryExecutor;

    public HttpRangeDownloader(HttpClient? httpClient = null, RetryExecutor? retryExecutor = null)
    {
        _httpClient = httpClient ?? new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.None });
        _retryExecutor = retryExecutor ?? new RetryExecutor();
    }

    public async Task DownloadAsync(DownloadRequest request, IProgress<DownloadProgress> progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(request.OutputDirectory);
        var taskRoot = Path.Combine(request.OutputDirectory, ".bilinative", request.TaskId.ToString("N"));
        Directory.CreateDirectory(taskRoot);

        var tracks = new List<(MediaTrack Track, string Path)>();
        if (request.VideoTrack is not null) tracks.Add((request.VideoTrack, Path.Combine(taskRoot, "video.m4s.part")));
        if (request.AudioTrack is not null) tracks.Add((request.AudioTrack, Path.Combine(taskRoot, "audio.m4s.part")));
        await WriteTaskManifestAsync(request, taskRoot, cancellationToken).ConfigureAwait(false);
        if (tracks.Count == 0 && request.Media.LegacySegments.Count > 0)
        {
            await DownloadLegacySegmentsAsync(request, taskRoot, progress, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (tracks.Count == 0) throw new InvalidOperationException("The download request has no media tracks.");

        var totals = tracks.Sum(track => track.Track.Size ?? 0);
        var gate = new object();
        var downloadedByTrack = new Dictionary<string, long>(StringComparer.Ordinal);
        var trackTasks = tracks.Select(async item =>
        {
            var type = item.Track.Type == TrackType.Video ? DownloadTaskState.DownloadingVideo : DownloadTaskState.DownloadingAudio;
            var trackProgress = new Progress<TrackProgress>(value =>
            {
                lock (gate)
                {
                    downloadedByTrack[item.Track.TrackId] = value.DownloadedBytes;
                    var current = downloadedByTrack.Values.Sum();
                    progress.Report(new DownloadProgress(request.TaskId, type, current, totals > 0 ? totals : null, value));
                }
            });
            var bytes = await DownloadTrackAsync(item.Track, item.Path, request.RetryPolicy, trackProgress, request.RequestHeaders, cancellationToken).ConfigureAwait(false);
            lock (gate) downloadedByTrack[item.Track.TrackId] = bytes;
        });

        await Task.WhenAll(trackTasks).ConfigureAwait(false);
    }

    private async Task DownloadLegacySegmentsAsync(
        DownloadRequest request,
        string taskRoot,
        IProgress<DownloadProgress> progress,
        CancellationToken cancellationToken)
    {
        var completed = 0L;
        var total = request.Media.LegacySegments.Sum(segment => segment.Size ?? 0);
        for (var index = 0; index < request.Media.LegacySegments.Count; index++)
        {
            var segment = request.Media.LegacySegments[index];
            var track = new MediaTrack
            {
                Type = TrackType.Segment,
                TrackId = $"segment-{segment.Index:D4}",
                Size = segment.Size,
                DurationSeconds = segment.DurationSeconds,
                Urls = segment.Urls
            };
            var segmentPath = Path.Combine(taskRoot, $"segment-{segment.Index:D4}.part");
            var completedBefore = completed;
            var segmentProgress = new Progress<TrackProgress>(value =>
            {
                progress.Report(new DownloadProgress(
                    request.TaskId,
                    DownloadTaskState.DownloadingSegments,
                    completedBefore + value.DownloadedBytes,
                    total > 0 ? total : null,
                    value));
            });
            var bytes = await DownloadTrackAsync(track, segmentPath, request.RetryPolicy, segmentProgress, request.RequestHeaders, cancellationToken).ConfigureAwait(false);
            completed += bytes;
        }

        var combinedPart = Path.Combine(taskRoot, "legacy.mp4.part");
        await using (var output = new FileStream(combinedPart, FileMode.Create, FileAccess.Write, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            var buffer = new byte[BufferSize];
            foreach (var segment in request.Media.LegacySegments.OrderBy(item => item.Index))
            {
                var segmentPath = Path.Combine(taskRoot, $"segment-{segment.Index:D4}.part");
                await using var input = new FileStream(segmentPath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
                int read;
                while ((read = await input.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
            }
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        var outputPath = Path.Combine(request.OutputDirectory, request.OutputFileName);
        Directory.CreateDirectory(request.OutputDirectory);
        File.Move(combinedPart, outputPath, true);
        progress.Report(new DownloadProgress(request.TaskId, DownloadTaskState.Finalizing, total, total > 0 ? total : null));
    }

    private static async Task WriteTaskManifestAsync(DownloadRequest request, string taskRoot, CancellationToken cancellationToken)
    {
        var manifest = new
        {
            schemaVersion = 1,
            taskId = request.TaskId,
            pageUrl = request.Media.PageUrl,
            title = request.Media.Title,
            retryAttempts = request.RetryPolicy.MaxAttempts,
            mergeAfterDownload = request.MergeAfterDownload,
            deleteTemporaryFiles = request.DeleteTemporaryFilesAfterMerge,
            requestHeaders = request.RequestHeaders is null ? null : new
            {
                referer = request.RequestHeaders.Referer,
                origin = request.RequestHeaders.Origin,
                userAgent = request.RequestHeaders.UserAgent,
                refreshUrl = request.RequestHeaders.RefreshUrl
            },
            tracks = request.VideoTrack is not null || request.AudioTrack is not null
                ? new[] { request.VideoTrack, request.AudioTrack }.Where(track => track is not null).Select(track => new
                {
                    type = track!.Type.ToString(),
                    trackId = track.TrackId,
                    size = track.Size,
                    segmentIndex = (int?)null,
                    urls = track.Urls.Select(candidate => candidate.Url).ToArray()
                }).ToArray()
                : request.Media.LegacySegments.Select(segment => new
                {
                    type = TrackType.Segment.ToString(),
                    trackId = $"segment-{segment.Index:D4}",
                    size = segment.Size,
                    segmentIndex = (int?)segment.Index,
                    urls = segment.Urls.Select(candidate => candidate.Url).ToArray()
                }).ToArray()
        };
        var json = JsonSerializer.Serialize(manifest, ManifestJsonOptions);
        await File.WriteAllTextAsync(Path.Combine(taskRoot, "task.json"), json, cancellationToken).ConfigureAwait(false);
    }

    public async Task<long> DownloadTrackAsync(
        MediaTrack track,
        string destinationPath,
        RetryPolicy retryPolicy,
        IProgress<TrackProgress>? progress,
        MediaRequestHeaders? requestHeaders,
        CancellationToken cancellationToken)
    {
        if (track.Urls.Count == 0) throw new InvalidOperationException("The media track has no URL candidates.");
        if (track.Size is long expectedSize && File.Exists(destinationPath))
        {
            var existingLength = new FileInfo(destinationPath).Length;
            if (existingLength == expectedSize)
            {
                progress?.Report(new TrackProgress(track.Type, existingLength, expectedSize));
                return existingLength;
            }
            if (existingLength > expectedSize) File.Delete(destinationPath);
        }
        Exception? last = null;
        for (var candidateIndex = 0; candidateIndex < track.Urls.Count; candidateIndex++)
        {
            var candidate = track.Urls[candidateIndex];
            try
            {
                return await _retryExecutor.ExecuteAsync(
                    token => DownloadCandidateAsync(track, candidate.Url, destinationPath, progress, requestHeaders, token),
                    retryPolicy,
                    IsTransient,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (candidateIndex < track.Urls.Count - 1 && IsFallbackEligible(exception))
            {
                last = exception;
            }
        }

        throw last ?? new InvalidOperationException("All media URL candidates failed.");
    }

    private async Task<long> DownloadCandidateAsync(
        MediaTrack track,
        string url,
        string destinationPath,
        IProgress<TrackProgress>? progress,
        MediaRequestHeaders? requestHeaders,
        CancellationToken cancellationToken)
    {
        var existingLength = File.Exists(destinationPath) ? new FileInfo(destinationPath).Length : 0L;
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyRequestHeaders(request, requestHeaders);
        if (existingLength > 0) request.Headers.Range = new RangeHeaderValue(existingLength, null);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.PartialContent)
        {
            throw new HttpRequestException($"HTTP {(int)response.StatusCode} while downloading media.", null, response.StatusCode);
        }

        var append = existingLength > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (append && response.Content.Headers.ContentRange?.From != existingLength)
        {
            append = false;
        }

        var startingLength = append ? existingLength : 0L;
        var totalLength = GetTotalLength(response, startingLength);
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = new FileStream(destinationPath, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[BufferSize];
        var downloaded = startingLength;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        int read;
        while ((read = await input.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            downloaded += read;
            var speed = stopwatch.Elapsed.TotalSeconds > 0 ? (downloaded - startingLength) / stopwatch.Elapsed.TotalSeconds : 0;
            progress?.Report(new TrackProgress(track.Type, downloaded, totalLength, speed));
        }

        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (totalLength is > 0 && downloaded != totalLength)
        {
            throw new IOException($"Expected {totalLength} bytes but received {downloaded}.");
        }

        return downloaded;
    }

    private static void ApplyRequestHeaders(HttpRequestMessage request, MediaRequestHeaders? headers)
    {
        request.Headers.Accept.TryParseAdd("*/*");
        if (headers is null) return;
        if (!string.IsNullOrWhiteSpace(headers.Referer) && Uri.TryCreate(headers.Referer, UriKind.Absolute, out var referer))
        {
            request.Headers.Referrer = referer;
        }
        if (!string.IsNullOrWhiteSpace(headers.Origin)) request.Headers.TryAddWithoutValidation("Origin", headers.Origin);
        if (!string.IsNullOrWhiteSpace(headers.UserAgent)) request.Headers.UserAgent.TryParseAdd(headers.UserAgent);
        if (!string.IsNullOrWhiteSpace(headers.Cookie)) request.Headers.TryAddWithoutValidation("Cookie", headers.Cookie);
    }

    private static long? GetTotalLength(HttpResponseMessage response, long startingLength)
    {
        if (response.Content.Headers.ContentRange?.Length is long rangeLength) return rangeLength;
        if (response.Content.Headers.ContentLength is long contentLength) return startingLength + contentLength;
        return null;
    }

    private static bool IsTransient(Exception exception) => exception switch
    {
        OperationCanceledException => false,
        HttpRequestException http when http.StatusCode is null => true,
        HttpRequestException http when (int?)http.StatusCode >= 500 => true,
        IOException => true,
        _ => false
    };

    private static bool IsFallbackEligible(Exception exception) => exception switch
    {
        HttpRequestException http => http.StatusCode is null || (int?)http.StatusCode >= 500 || http.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.PreconditionFailed,
        IOException => true,
        _ => false
    };
}
