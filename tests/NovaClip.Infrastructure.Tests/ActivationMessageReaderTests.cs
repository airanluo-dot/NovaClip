using System.Text;
using NovaClip.Infrastructure;
using Xunit;

namespace NovaClip.Infrastructure.Tests;

public sealed class ActivationMessageReaderTests
{
    [Theory]
    [InlineData("activate\nignored", "activate")]
    [InlineData("activate\r\n", "activate")]
    [InlineData("你好", "你好")]
    [InlineData("", null)]
    public async Task ReadsOneMessage(string input, string? expected)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(input));
        Assert.Equal(expected, await ActivationMessageReader.ReadAsync(stream, 32, CancellationToken.None));
    }

    [Fact]
    public async Task RejectsOversizedMessageBeforeEndOfStream()
    {
        using var stream = new MemoryStream(new byte[1024]);
        await Assert.ThrowsAsync<InvalidDataException>(() => ActivationMessageReader.ReadAsync(stream, 16, CancellationToken.None));
        Assert.True(stream.Position < stream.Length);
    }

    [Fact]
    public async Task HonorsCancellation()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("activate"));
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ActivationMessageReader.ReadAsync(stream, 32, stop.Token));
    }
}
