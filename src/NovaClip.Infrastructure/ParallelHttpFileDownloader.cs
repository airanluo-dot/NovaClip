using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using NovaClip.Core;

namespace NovaClip.Infrastructure;

/// <summary>Independent HTTP range transport for a private, per-task staging path.</summary>
public sealed class ParallelHttpFileDownloader(HttpClient client, TimeSpan? idleTimeout = null, DownloadConnectionBudget? connectionBudget = null, DownloadBandwidthLimiter? bandwidthLimiter = null)
{
    private const int BufferSize = 128 * 1024;
    private readonly RetryExecutor _retry = new();
    private readonly DownloadBandwidthLimiter _bandwidth = bandwidthLimiter ?? new();
    private readonly DownloadConnectionBudget _connections = connectionBudget ?? new();
    private readonly HttpTransferDeadline _deadline = new(idleTimeout);
    private sealed record Identity(string Url, string ETag, long Length, int Parts);
    private sealed class RangeRejectedException : Exception { }

    // Null means the server cannot safely support parallel ranges; caller uses sequential transport.
    public async Task<long?> TryDownloadAsync(Uri uri, string stagingPath, int connections,
        RetryPolicy retryPolicy, IProgress<TrackProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (uri.Scheme is not ("http" or "https")) throw new ArgumentException("HTTP(S) is required.", nameof(uri));
        if (!Path.IsPathRooted(stagingPath)) throw new ArgumentException("An absolute staging path is required.", nameof(stagingPath));
        if (connections <= 1) return null;
        using var probeLease = await _connections.AcquireAsync(cancellationToken).ConfigureAwait(false);
        using var probe = NewRequest(uri, 0, 0);
        using var head = await _deadline.RunAsync(token => client.SendAsync(probe, HttpCompletionOption.ResponseHeadersRead, token), cancellationToken).ConfigureAwait(false);
        var range = head.Content.Headers.ContentRange;
        var tag = head.Headers.ETag;
        if (head.StatusCode != HttpStatusCode.PartialContent || range?.Unit != "bytes" ||
            range.From != 0 || range.To != 0 || range.Length is not > 0 ||
            tag is null || tag.IsWeak || HasEncoding(head)) return null;
        var length = range.Length.Value;
        head.Dispose();
        probeLease.Dispose();
        var ranges = HttpByteRangePlanner.Create(length, connections);
        if (ranges.Count <= 1) return null;
        var cache = stagingPath + ".ranges";
        Directory.CreateDirectory(cache);
        var identity = new Identity(uri.AbsoluteUri, tag.ToString(), length, ranges.Count);
        var identityText = JsonSerializer.Serialize(identity);
        var metadataPath = Path.Combine(cache, "identity.json");
        var sameIdentity = File.Exists(metadataPath) && new FileInfo(metadataPath).Length < 16384 &&
            string.Equals(await File.ReadAllTextAsync(metadataPath, cancellationToken).ConfigureAwait(false), identityText, StringComparison.Ordinal);
        if (!sameIdentity)
        {
            // Only known part files in this task's private staging directory are touched.
            for (var index = 0; index < DownloadConnectionBudget.Maximum; index++) File.Delete(PartPath(cache, index));
            await File.WriteAllTextAsync(metadataPath + ".tmp", identityText, cancellationToken).ConfigureAwait(false);
            File.Move(metadataPath + ".tmp", metadataPath, true);
        }
        var completed = new long[ranges.Count];
        var gate = new object();
        for (var index = 0; index < ranges.Count; index++)
        {
            var file = PartPath(cache, index);
            if (!File.Exists(file)) continue;
            var size = new FileInfo(file).Length;
            if (size > ranges[index].Length) File.Delete(file);
            else completed[index] = size;
        }
        var resumedBytes = completed.Sum();
        var transferClock = System.Diagnostics.Stopwatch.StartNew();
        progress?.Report(new TrackProgress(TrackType.File, resumedBytes, length));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var rejected = 0;
        long CompletedBytes() { lock (gate) return completed.Sum(); }
        try
        {
            await AdaptiveDownloadScheduler.RunAsync(ranges.Count, connections,
                index => DownloadPartWithRetryAsync(ranges[index], index), CompletedBytes, stop.Token).ConfigureAwait(false);
        }
        catch when (Volatile.Read(ref rejected) != 0 && !cancellationToken.IsCancellationRequested)
        {
            File.Delete(metadataPath);
            return null;
        }
        cancellationToken.ThrowIfCancellationRequested();
        await using (var output = new FileStream(stagingPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous))
        {
            for (var index = 0; index < ranges.Count; index++)
            {
                await using var input = new FileStream(PartPath(cache, index), FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous);
                if (input.Length != ranges[index].Length) throw new InvalidDataException("Incomplete file part.");
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            }
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (output.Length != length) throw new InvalidDataException("File assembly length mismatch.");
        }
        return length;

        async Task DownloadPartWithRetryAsync(HttpByteRangePlanner.ByteRange part, int index)
        {
            try
            {
                await _retry.ExecuteAsync(async token =>
                {
                    var file = PartPath(cache, index);
                    var existing = File.Exists(file) ? new FileInfo(file).Length : 0;
                    if (existing == part.Length) return existing;
                    using var lease = await _connections.AcquireAsync(token).ConfigureAwait(false);
                    using var request = NewRequest(uri, part.From + existing, part.To);
                    request.Headers.IfRange = new RangeConditionHeaderValue(tag);
                    using var response = await _deadline.RunAsync(readToken => client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, readToken), token).ConfigureAwait(false);
                    if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.RequestedRangeNotSatisfiable)
                        throw new RangeRejectedException();
                    response.EnsureSuccessStatusCode();
                    var receivedRange = response.Content.Headers.ContentRange;
                    if (response.StatusCode != HttpStatusCode.PartialContent || HasEncoding(response) ||
                        receivedRange?.Unit != "bytes" || receivedRange.From != part.From + existing ||
                        receivedRange.To != part.To || receivedRange.Length != length ||
                        response.Headers.ETag?.ToString() != identity.ETag)
                        throw new RangeRejectedException();
                    await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                    await using var output = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.Read, BufferSize, FileOptions.Asynchronous);
                    var buffer = new byte[BufferSize];
                    int count;
                    while ((count = await _deadline.RunAsync(readToken => input.ReadAsync(buffer.AsMemory(), readToken).AsTask(), token).ConfigureAwait(false)) > 0)
                    {
                        if (count > part.Length - existing) throw new RangeRejectedException();
                        await _bandwidth.ConsumeAsync(count, token).ConfigureAwait(false);
                        await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                        existing += count;
                        lock (gate)
                        {
                            completed[index] = existing;
                            var total = completed.Sum();
                            var speed = transferClock.Elapsed.TotalSeconds > 0
                                ? (total - resumedBytes) / transferClock.Elapsed.TotalSeconds : 0;
                            progress?.Report(new TrackProgress(TrackType.File, total, length, speed));
                        }
                    }
                    await output.FlushAsync(token).ConfigureAwait(false);
                    if (existing != part.Length) throw new IOException("File part ended prematurely.");
                    return existing;
                }, retryPolicy, IsTransient, stop.Token).ConfigureAwait(false);
            }
            catch (RangeRejectedException)
            {
                Interlocked.Exchange(ref rejected, 1);
                await stop.CancelAsync().ConfigureAwait(false);
                throw;
            }
            catch
            {
                await stop.CancelAsync().ConfigureAwait(false);
                throw;
            }
        }
    }

    private static string PartPath(string directory, int index) => Path.Combine(directory, $"part-{index:D2}.bin");
    private static HttpRequestMessage NewRequest(Uri uri, long from, long to)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Range = new RangeHeaderValue(from, to);
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
        return request;
    }
    private static bool HasEncoding(HttpResponseMessage response) =>
        response.Content.Headers.ContentEncoding.Any(value => !value.Equals("identity", StringComparison.OrdinalIgnoreCase));
    private static bool IsTransient(Exception exception) => exception is IOException or TimeoutException ||
        exception is HttpRequestException { StatusCode: null or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests } ||
        exception is HttpRequestException { StatusCode: >= HttpStatusCode.InternalServerError };
}
