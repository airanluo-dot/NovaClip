using NovaClip.Core;

namespace NovaClip.Infrastructure;

public static class DirectFileRequestFactory
{
    public static bool TryParseUrl(string? value, out Uri? uri)
    {
        uri = null;
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var candidate) ||
            candidate.Scheme is not ("http" or "https") || string.IsNullOrEmpty(candidate.Host) ||
            !string.IsNullOrEmpty(candidate.UserInfo)) return false;
        uri = candidate;
        return true;
    }

    public static string SuggestFileName(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        var raw = uri.AbsolutePath.Split('/').LastOrDefault();
        var name = string.IsNullOrEmpty(raw) ? "download.bin" : Uri.UnescapeDataString(raw);
        return new FileNameSanitizer().Sanitize(name, "download.bin");
    }

    public static DownloadRequest Create(string url, string directory, string fileName, RetryPolicy retryPolicy)
    {
        if (!TryParseUrl(url, out var uri)) throw new ArgumentException("An HTTP(S) URL without embedded credentials is required.", nameof(url));
        if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("An absolute output folder is required.", nameof(directory));
        if (string.IsNullOrWhiteSpace(fileName) || fileName is "." or ".." ||
            !string.Equals(new FileNameSanitizer().Sanitize(fileName, "download.bin"), fileName, StringComparison.Ordinal))
            throw new ArgumentException("A valid single Windows file name is required.", nameof(fileName));
        var media = new MediaDescriptor
        {
            Title = fileName, PageUrl = uri!.AbsoluteUri, Source = ResolverStrategy.DirectFile,
            Tracks = [new MediaTrack { Type = TrackType.File, TrackId = "file", Urls = [new MediaUrlCandidate(uri.AbsoluteUri)] }]
        };
        return new DownloadRequest(Guid.NewGuid(), media, null, null, Path.GetFullPath(directory), fileName,
            retryPolicy, MergeAfterDownload: false, DeleteTemporaryFilesAfterMerge: true);
    }
}
