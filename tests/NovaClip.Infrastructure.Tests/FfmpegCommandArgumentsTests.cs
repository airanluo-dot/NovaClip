using NovaClip.Infrastructure;
using Xunit;

namespace NovaClip.Infrastructure.Tests;

public sealed class FfmpegCommandArgumentsTests
{
    [Fact]
    public void StagingFileUsesExplicitMp4MuxerAndSeparatePathArguments()
    {
        var args = FfmpegCommandArguments.ForMp4Mux("video with spaces.m4s.part", "audio.m4s.part", "final-output.tmp");
        Assert.Equal("-f", args[^3]);
        Assert.Equal("mp4", args[^2]);
        Assert.Equal("final-output.tmp", args[^1]);
        Assert.Contains("video with spaces.m4s.part", args);
        Assert.Contains("copy", args);
    }

    [Fact]
    public void ConcatUsesSafeRelativeManifestAndExplicitMp4Muxer()
    {
        var args = FfmpegCommandArguments.ForMp4Concat("inputs with spaces.txt", "final-output.tmp");
        Assert.Contains("inputs with spaces.txt", args);
        Assert.Equal("1", args[args.ToList().IndexOf("-safe") + 1]);
        Assert.Equal("-f", args[^3]);
        Assert.Equal("mp4", args[^2]);
        Assert.Equal("final-output.tmp", args[^1]);
    }
}
