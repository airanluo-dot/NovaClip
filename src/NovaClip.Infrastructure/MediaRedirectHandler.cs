using System.Net;

namespace NovaClip.Infrastructure;

// The media client supplies an in-memory Cookie header, so automatic redirects
// cannot enforce the browser's cookie domain boundary. Own each hop explicitly.
public sealed class MediaRedirectHandler : DelegatingHandler
{
    private const int MaxUriCharacters = 16_384;
    private readonly int _maxRedirects;

    public MediaRedirectHandler(HttpMessageHandler innerHandler, int maxRedirects = 5) : base(innerHandler)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxRedirects);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxRedirects, 5);
        _maxRedirects = maxRedirects;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var current = request;
        HttpRequestMessage? ownedRequest = null;
        var credentialsStripped = false;
        var redirectCount = 0;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var response = await base.SendAsync(current, cancellationToken).ConfigureAwait(false);
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!IsRedirect(response.StatusCode) ||
                        current.Method != HttpMethod.Get && current.Method != HttpMethod.Head ||
                        current.Content is not null || !response.Headers.NonValidated.TryGetValues("Location", out var locations))
                        return response;
                    if (redirectCount >= _maxRedirects)
                        throw new HttpRequestException("The media redirect limit was exceeded.");

                    var source = current.RequestUri;
                    if (locations.Count != 1 || locations.First().Length > MaxUriCharacters ||
                        !Uri.TryCreate(locations.First(), UriKind.RelativeOrAbsolute, out var location) ||
                        source is null || !Uri.TryCreate(source, location, out var destination) ||
                        destination.Scheme is not ("http" or "https") ||
                        destination.AbsoluteUri.Length > MaxUriCharacters ||
                        string.IsNullOrWhiteSpace(destination.Host) || !string.IsNullOrEmpty(destination.UserInfo))
                        throw new HttpRequestException("The media redirect destination is invalid.");

                    credentialsStripped |= !SameAuthority(source, destination) ||
                        source.Scheme == "https" && destination.Scheme == "http";
                    var next = CloneRequest(current, destination, credentialsStripped);
                    var previous = ownedRequest;
                    ownedRequest = next;
                    current = next;
                    try { response.Dispose(); }
                    finally { previous?.Dispose(); }
                    redirectCount++;
                }
                catch
                {
                    response.Dispose();
                    throw;
                }
            }
        }
        finally
        {
            ownedRequest?.Dispose();
        }
    }

    private static HttpRequestMessage CloneRequest(HttpRequestMessage source, Uri destination, bool stripCredentials)
    {
        var clone = new HttpRequestMessage(source.Method, destination)
        {
            Version = source.Version,
            VersionPolicy = source.VersionPolicy
        };
        try
        {
            foreach (var header in source.Headers)
            {
                if (header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
                    stripCredentials && (header.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase) ||
                                         header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase))) continue;
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            foreach (var option in source.Options)
                clone.Options.Set(new HttpRequestOptionsKey<object?>(option.Key), option.Value);
            return clone;
        }
        catch
        {
            clone.Dispose();
            throw;
        }
    }

    private static bool SameAuthority(Uri source, Uri destination) =>
        source.IdnHost.Equals(destination.IdnHost, StringComparison.OrdinalIgnoreCase) && source.Port == destination.Port;

    private static bool IsRedirect(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther or
            HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;
}
