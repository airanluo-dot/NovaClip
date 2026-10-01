using NovaClip.Core;
using NovaClip.Infrastructure;
using Xunit;

namespace NovaClip.Infrastructure.Tests;

public sealed class DirectFileRequestFactoryTests
{
    [Theory]
    [InlineData("ftp://host/file")]
    [InlineData("file:///tmp/file")]
    [InlineData("https://user:password@host/file")]
    [InlineData("not a link")]
    public void RejectsInvalidUrl(string value) => Assert.False(DirectFileRequestFactory.TryParseUrl(value, out _));

    [Fact]
    public void KeepsSignedQueryButUsesOnlyDecodedPathForName()
    {
        const string url = "https://example.test/%E6%96%87%E4%BB%B6.zip?signature=secret&name=other.exe";
        Assert.True(DirectFileRequestFactory.TryParseUrl(url, out var uri));
        Assert.Equal("文件.zip", DirectFileRequestFactory.SuggestFileName(uri!));
        var request = DirectFileRequestFactory.Create(url, Path.GetTempPath(), "文件.zip", new RetryPolicy(1));
        Assert.Equal(new Uri(url).AbsoluteUri, request.FileTrack!.Urls[0].Url);
        Assert.False(request.MergeAfterDownload);
        Assert.Equal(ResolverStrategy.DirectFile, request.Media.Source);
    }

    [Theory]
    [InlineData("../file.zip")]
    [InlineData("CON.zip")]
    [InlineData("CON.zip.exe")]
    [InlineData("aux.data.backup")]
    [InlineData("COM¹.txt")]
    [InlineData("LPT².zip")]
    [InlineData("CONIN$.txt")]
    [InlineData("file.zip.")]
    [InlineData("a:b.zip")]
    public void RejectsUnsafeNames(string name) => Assert.Throws<ArgumentException>(() =>
        DirectFileRequestFactory.Create("https://example.test/a", Path.GetTempPath(), name, new RetryPolicy(1)));
}
