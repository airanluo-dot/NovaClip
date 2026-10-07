using System.Net;
using System.Net.Http.Headers;
using NovaClip.Infrastructure;
using Xunit;

namespace NovaClip.Infrastructure.Tests;

public sealed class MediaRedirectHandlerTests
{
    [Theory]
    [InlineData(HttpStatusCode.MovedPermanently, "GET")]
    [InlineData(HttpStatusCode.Found, "GET")]
    [InlineData(HttpStatusCode.SeeOther, "GET")]
    [InlineData(HttpStatusCode.TemporaryRedirect, "GET")]
    [InlineData(HttpStatusCode.PermanentRedirect, "GET")]
    [InlineData(HttpStatusCode.SeeOther, "HEAD")]
    public async Task SameOriginPreservesMethodRangeAndPlaybackHeaders(HttpStatusCode status, string method)
    {
        var intermediateContent = new DisposalContent();
        var finalContent = new DisposalContent();
        var terminal = new HttpResponseMessage(HttpStatusCode.OK) { Content = finalContent };
        var transport = new RecordingHandler((request, count) => count == 1
            ? Redirect(status, "/next", intermediateContent)
            : terminal);
        using var client = new HttpMessageInvoker(new MediaRedirectHandler(transport));
        using var request = Request(method);

        var response = await client.SendAsync(request, CancellationToken.None);

        Assert.Same(terminal, response);
        Assert.Equal(2, transport.Requests.Count);
        var redirected = transport.Requests[1];
        Assert.Equal(new HttpMethod(method), redirected.Method);
        Assert.Equal(new Uri("https://media.example.test/next"), redirected.RequestUri);
        Assert.Equal("bytes=100-199", redirected.Headers.Range!.ToString());
        Assert.Equal("NovaClipTest/1.0", redirected.Headers.UserAgent.ToString());
        Assert.Equal(request.Headers.Referrer, redirected.Headers.Referrer);
        Assert.Equal("https://www.bilibili.com", Assert.Single(redirected.Headers.GetValues("Origin")));
        Assert.Equal("test_cookie=dummy", Assert.Single(redirected.Headers.GetValues("Cookie")));
        Assert.Equal("Bearer dummy", redirected.Headers.Authorization!.ToString());
        Assert.Null(redirected.Headers.Host);
        Assert.True(intermediateContent.IsDisposed);
        Assert.False(finalContent.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() => redirected.RequestUri = redirected.RequestUri);
        request.RequestUri = request.RequestUri;

        response.Dispose();
        Assert.True(finalContent.IsDisposed);
    }

    [Theory]
    [InlineData("https://other.example.test/next")]
    [InlineData("https://media.example.test:8443/next")]
    [InlineData("http://media.example.test/next")]
    public async Task OriginChangeDropsCredentialsAndReturningDoesNotRestoreThem(string next)
    {
        var transport = new RecordingHandler((request, count) => count switch
        {
            1 => Redirect(HttpStatusCode.Found, next),
            2 => Redirect(HttpStatusCode.TemporaryRedirect, "https://media.example.test/returned"),
            _ => new HttpResponseMessage(HttpStatusCode.PartialContent)
        });
        using var client = new HttpMessageInvoker(new MediaRedirectHandler(transport));
        using var request = Request("GET");
        using var response = await client.SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal(3, transport.Requests.Count);
        foreach (var redirected in transport.Requests.Skip(1))
        {
            Assert.False(redirected.Headers.Contains("Cookie"));
            Assert.Null(redirected.Headers.Authorization);
            Assert.Equal("bytes=100-199", redirected.Headers.Range!.ToString());
            Assert.Equal("NovaClipTest/1.0", redirected.Headers.UserAgent.ToString());
        }
        Assert.True(request.Headers.Contains("Cookie"));
        Assert.NotNull(request.Headers.Authorization);
    }

    [Fact]
    public async Task RedirectBudgetFailureDisposesEveryResponseAndDoesNotExposeSignedUrls()
    {
        var contents = new List<DisposalContent>();
        var transport = new RecordingHandler((request, count) =>
        {
            var content = new DisposalContent();
            contents.Add(content);
            return Redirect(HttpStatusCode.Found, $"/hop/{count}?token=dummy_sensitive_query", content);
        });
        using var client = new HttpMessageInvoker(new MediaRedirectHandler(transport, maxRedirects: 2));
        using var request = Request("GET");

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(request, CancellationToken.None));

        Assert.Equal(3, transport.Requests.Count);
        Assert.All(contents, content => Assert.True(content.IsDisposed));
        Assert.All(transport.Requests.Skip(1), redirected =>
            Assert.Throws<ObjectDisposedException>(() => redirected.RequestUri = redirected.RequestUri));
        Assert.DoesNotContain("dummy_sensitive_query", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("dummy", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("media.example.test", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("file:///tmp/video")]
    [InlineData("ftp://media.example.test/video")]
    [InlineData("https://user:dummy@media.example.test/video")]
    [InlineData("http://[")]
    public async Task UnsafeOrMalformedRedirectIsRejectedWithoutAnotherRequest(string location)
    {
        var content = new DisposalContent();
        var transport = new RecordingHandler((request, count) => Redirect(HttpStatusCode.Found, location, content));
        using var client = new HttpMessageInvoker(new MediaRedirectHandler(transport));
        using var request = Request("GET");

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(request, CancellationToken.None));

        Assert.Single(transport.Requests);
        Assert.True(content.IsDisposed);
        Assert.DoesNotContain(location, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExcessiveLocationLengthIsRejectedBeforeFollowingIt()
    {
        var content = new DisposalContent();
        var transport = new RecordingHandler((request, count) => Redirect(HttpStatusCode.Found,
            "https://media.example.test/video?token=" + new string('a', 70_000), content));
        using var client = new HttpMessageInvoker(new MediaRedirectHandler(transport));
        using var request = Request("GET");

        await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(request, CancellationToken.None));

        Assert.Single(transport.Requests);
        Assert.True(content.IsDisposed);
    }

    [Fact]
    public async Task CancellationAfterResponseStopsBeforeAnotherHopAndDisposesResponse()
    {
        using var stop = new CancellationTokenSource();
        var content = new DisposalContent();
        var transport = new RecordingHandler((request, count) =>
        {
            stop.Cancel();
            return Redirect(HttpStatusCode.Found, "/next", content);
        });
        using var client = new HttpMessageInvoker(new MediaRedirectHandler(transport));
        using var request = Request("GET");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SendAsync(request, stop.Token));

        Assert.Single(transport.Requests);
        Assert.True(content.IsDisposed);
    }

    [Fact]
    public async Task PostRedirectRemainsCallerOwnedAndIsNotReplayed()
    {
        var content = new DisposalContent();
        var terminal = Redirect(HttpStatusCode.Found, "/next", content);
        var transport = new RecordingHandler((request, count) => terminal);
        using var client = new HttpMessageInvoker(new MediaRedirectHandler(transport));
        using var request = Request("POST");

        var response = await client.SendAsync(request, CancellationToken.None);

        Assert.Same(terminal, response);
        Assert.Single(transport.Requests);
        Assert.False(content.IsDisposed);
        response.Dispose();
        Assert.True(content.IsDisposed);
    }

    [Fact]
    public async Task RedirectWithoutLocationRemainsCallerOwned()
    {
        var content = new DisposalContent();
        var terminal = new HttpResponseMessage(HttpStatusCode.Found) { Content = content };
        var transport = new RecordingHandler((request, count) => terminal);
        using var client = new HttpMessageInvoker(new MediaRedirectHandler(transport));
        using var request = Request("GET");

        var response = await client.SendAsync(request, CancellationToken.None);

        Assert.Same(terminal, response);
        Assert.Single(transport.Requests);
        Assert.False(content.IsDisposed);
        response.Dispose();
        Assert.True(content.IsDisposed);
    }

    private static HttpRequestMessage Request(string method)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), "https://media.example.test/video");
        request.Headers.Range = new RangeHeaderValue(100, 199);
        request.Headers.UserAgent.ParseAdd("NovaClipTest/1.0");
        request.Headers.Referrer = new Uri("https://www.bilibili.com/video/BVTest/");
        request.Headers.TryAddWithoutValidation("Origin", "https://www.bilibili.com");
        request.Headers.TryAddWithoutValidation("Cookie", "test_cookie=dummy");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "dummy");
        request.Headers.Host = "media.example.test";
        return request;
    }

    private static HttpResponseMessage Redirect(HttpStatusCode status, string location, DisposalContent? content = null)
    {
        var response = new HttpResponseMessage(status) { Content = content ?? new DisposalContent() };
        response.Headers.TryAddWithoutValidation("Location", location);
        return response;
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request, Requests.Count));
        }
    }

    private sealed class DisposalContent : HttpContent
    {
        public bool IsDisposed { get; private set; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => Task.CompletedTask;
        protected override bool TryComputeLength(out long length) { length = 0; return true; }
        protected override void Dispose(bool disposing)
        {
            if (disposing) IsDisposed = true;
            base.Dispose(disposing);
        }
    }
}
