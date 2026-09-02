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
        ArgumentNullException.ThrowIfNull(progress);
        ValidateRequest(request);
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

        long totals = 0;
        var totalsOverflowed = false;
        foreach (var track in tracks)
        {
            var size = track.Track.Size ?? 0;
            if (size > 0 && long.MaxValue - totals >= size) totals += size;
            else if (size > 0)
            {
                totalsOverflowed = true;
                break;
            }
        }
        if (totalsOverflowed) totals = 0;
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
        long total = 0;
        foreach (var segment in request.Media.LegacySegments)
        {
            var size = segment.Size ?? 0;
            if (size > 0 && long.MaxValue - total >= size) total += size;
            else if (size > 0) { total = 0; break; }
        }
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
        var manifestPath = Path.Combine(taskRoot, "task.json");
        var temporaryPath = manifestPath + ".tmp";
        await File.WriteAllTextAsync(temporaryPath, json, cancellationToken).ConfigureAwait(false);
        File.Move(temporaryPath, manifestPath, true);
    }

    public async Task<long> DownloadTrackAsync(
        MediaTrack track,
        string destinationPath,
        RetryPolicy retryPolicy,
        IProgress<TrackProgress>? progress,
        MediaRequestHeaders? requestHeaders,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(track.Urls);
        if (track.Urls.Count == 0) throw new InvalidOperationException("The media track has no URL candidates.");
        if (string.IsNullOrWhiteSpace(destinationPath) || !Path.IsPathRooted(destinationPath)) throw new ArgumentException("The destination path must be absolute.", nameof(destinationPath));
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? throw new ArgumentException("The destination path must include a directory.", nameof(destinationPath)));
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
            if (candidate is null || !Uri.TryCreate(candidate.Url, UriKind.Absolute, out var candidateUri) || candidateUri.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(candidateUri.Host) || !string.IsNullOrEmpty(candidateUri.UserInfo))
            {
                last = new UriFormatException("The media URL candidate is not an HTTP(S) URI.");
                continue;
            }
            try
            {
                return await _retryExecutor.ExecuteAsync(
                    token => DownloadCandidateAsync(track, candidateUri, destinationPath, progress, requestHeaders, token),
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
        Uri url,
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

        if (existingLength > 0 && response.StatusCode == HttpStatusCode.PartialContent && response.Content.Headers.ContentRange?.From != existingLength)
        {
            throw new HttpRequestException("The server returned a partial response for the wrong byte range.", null, HttpStatusCode.PreconditionFailed);
        }
        var append = existingLength > 0 && response.StatusCode == HttpStatusCode.PartialContent;

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
            if (track.Size is > 0 && downloaded > track.Size.Value)
            {
                throw new InvalidDataException("The server returned more bytes than the media track declares.");
            }
            var speed = stopwatch.Elapsed.TotalSeconds > 0 ? (downloaded - startingLength) / stopwatch.Elapsed.TotalSeconds : 0;
            progress?.Report(new TrackProgress(track.Type, downloaded, totalLength, speed));
        }

        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (totalLength is > 0 && downloaded != totalLength)
        {
            throw new IOException($"Expected {totalLength} bytes but received {downloaded}.");
        }
        if (track.Size is > 0 && downloaded != track.Size.Value)
        {
            throw new IOException($"Expected {track.Size.Value} media bytes but received {downloaded}.");
        }

        return downloaded;
    }

    private static void ApplyRequestHeaders(HttpRequestMessage request, MediaRequestHeaders? headers)
    {
        request.Headers.Accept.TryParseAdd("*/*");
        if (headers is null) return;
        if (!string.IsNullOrWhiteSpace(headers.Referer) && Uri.TryCreate(headers.Referer, UriKind.Absolute, out var referer) && referer.Scheme is ("http" or "https") && !string.IsNullOrEmpty(referer.Host) && string.IsNullOrEmpty(referer.UserInfo))
        {
            request.Headers.Referrer = referer;
        }
        if (!string.IsNullOrWhiteSpace(headers.Origin) && headers.Origin.Length <= 4_096 && !ContainsHeaderInjection(headers.Origin) && Uri.TryCreate(headers.Origin, UriKind.Absolute, out var origin) && origin.Scheme is ("http" or "https") && !string.IsNullOrEmpty(origin.Host) && string.IsNullOrEmpty(origin.UserInfo)) request.Headers.TryAddWithoutValidation("Origin", headers.Origin);
        if (!string.IsNullOrWhiteSpace(headers.UserAgent) && headers.UserAgent.Length <= 4_096 && !ContainsHeaderInjection(headers.UserAgent)) request.Headers.UserAgent.TryParseAdd(headers.UserAgent);
        if (!string.IsNullOrWhiteSpace(headers.Cookie) && headers.Cookie.Length <= 64_000 && !ContainsHeaderInjection(headers.Cookie) && request.RequestUri is { } uri && IsTrustedMediaHost(uri.Host)) request.Headers.TryAddWithoutValidation("Cookie", headers.Cookie);
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
        HttpRequestException http when http.StatusCode is HttpStatusCode.RequestTimeout or (HttpStatusCode)429 => true,
        HttpRequestException http when http.StatusCode is null => true,
        HttpRequestException http when (int?)http.StatusCode >= 500 => true,
        IOException => true,
        _ => false
    };

    private static void ValidateRequest(DownloadRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.TaskId == Guid.Empty) throw new ArgumentException("The download task ID cannot be empty.", nameof(request));
        ArgumentNullException.ThrowIfNull(request.Media);
        ArgumentNullException.ThrowIfNull(request.Media.LegacySegments);
        if (string.IsNullOrWhiteSpace(request.OutputDirectory) || !Path.IsPathRooted(request.OutputDirectory)) throw new ArgumentException("The output directory must be an absolute path.", nameof(request));
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

    private static bool IsHttpUrl(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 16_384 && Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is ("http" or "https") && !string.IsNullOrWhiteSpace(uri.Host) && string.IsNullOrEmpty(uri.UserInfo);

    private static bool IsFallbackEligible(Exception exception) => exception switch
    {
        HttpRequestException http => http.StatusCode is null || (int?)http.StatusCode >= 500 || http.StatusCode is HttpStatusCode.RequestTimeout or (HttpStatusCode)429 or HttpStatusCode.Forbidden or HttpStatusCode.PreconditionFailed,
        IOException => true,
        _ => false
    };

    private static bool ContainsHeaderInjection(string value) => value.IndexOfAny(['\r', '\n']) >= 0;

    private static bool IsTrustedMediaHost(string host)
    {
        var normalized = host.TrimEnd('.');
        return normalized.Equals("bilibili.com", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith(".bilibili.com", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("b23.tv", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith(".bilivideo.com", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith(".bilivideo.cn", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith(".biliapi.com", StringComparison.OrdinalIgnoreCase);
    }
}
