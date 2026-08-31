using BiliNative.Core;
using BiliNative.WebBridge;
using Xunit;

namespace BiliNative.WebBridge.Tests;

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
}
