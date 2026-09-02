using NovaClip.Core;
using NovaClip.Bilibili;
using Xunit;

namespace NovaClip.Bilibili.Tests;

public sealed class PlayUrlNormalizerTests
{
    [Fact]
    public void NormalizesDashWithCamelCaseBackupsAndQualityMetadata()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "dash-basic.json"));
        var context = new PlayUrlContext("https://www.bilibili.com/video/BV1TEST", "Fixture title", "BV1TEST", 1, 2);
        var result = new PlayUrlNormalizer().Normalize(json, context);
        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Media!.Tracks.Count);
        Assert.Equal(2, result.Media.VideoTrack!.Urls.Count);
        Assert.Equal("1080P 高清", result.Media.QualityOptions.Single(item => item.Id == 80).Description);
    }

    [Fact]
    public void NormalizesLegacyDurl()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "durl.json"));
        var result = new PlayUrlNormalizer().Normalize(json, new PlayUrlContext("https://www.bilibili.com/video/BV1TEST", "Legacy"));
        Assert.True(result.IsSuccess);
        Assert.Empty(result.Media!.Tracks);
        Assert.Single(result.Media.LegacySegments);
    }

    [Fact]
    public void ReturnsPermissionErrorWithoutThrowing()
    {
        var result = new PlayUrlNormalizer().Normalize("{\"code\":-10403,\"message\":\"需要大会员\"}", new PlayUrlContext("https://www.bilibili.com/video/BV1TEST", "Restricted"));
        Assert.False(result.IsSuccess);
        Assert.Equal("RESOLVE_VIP_REQUIRED", result.Error!.Code);
    }

    [Fact]
    public void RejectsBridgeMessagesFromUnknownSchemaOrType()
    {
        Assert.False(BilibiliBridgeMessageParser.TryParse("{\"schemaVersion\":2,\"type\":\"pageContextChanged\",\"payload\":{}}", out _));
        Assert.False(BilibiliBridgeMessageParser.TryParse("{\"schemaVersion\":1,\"type\":\"execute\",\"payload\":{}}", out _));
    }

    [Fact]
    public void IgnoresMalformedTrackEntriesInsteadOfThrowing()
    {
        const string json = "{\"code\":0,\"data\":{\"dash\":{\"video\":[null,42,{\"id\":80,\"base_url\":\"javascript:alert(1)\"}],\"audio\":[]}}}";
        var result = new PlayUrlNormalizer().Normalize(json, new PlayUrlContext("https://www.bilibili.com/video/BV1TEST", "Malformed"));
        Assert.False(result.IsSuccess);
        Assert.Equal("RESOLVE_PLAYURL_NOT_FOUND", result.Error!.Code);
    }

    [Fact]
    public void AcceptsB23BridgePageContextAndRejectsUnknownHosts()
    {
        const string valid = "{\"schemaVersion\":1,\"type\":\"pageContextChanged\",\"payload\":{\"url\":\"https://b23.tv/abc\",\"kind\":\"video\",\"title\":\"x\"}}";
        Assert.True(BilibiliBridgeMessageParser.TryParse(valid, out var message));
        Assert.True(BilibiliBridgeMessageParser.TryReadPageContext(message!, out var context));
        Assert.Equal("b23.tv", new Uri(context!.Url).Host);

        const string unknown = "{\"schemaVersion\":1,\"type\":\"pageContextChanged\",\"payload\":{\"url\":\"https://example.com/video\",\"kind\":\"video\"}}";
        Assert.True(BilibiliBridgeMessageParser.TryParse(unknown, out var unknownMessage));
        Assert.False(BilibiliBridgeMessageParser.TryReadPageContext(unknownMessage!, out _));
    }
}
