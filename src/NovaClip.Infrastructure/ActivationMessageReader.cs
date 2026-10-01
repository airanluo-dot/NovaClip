using System.Text;

namespace NovaClip.Infrastructure;

/// <summary>Reads one bounded UTF-8 activation message without waiting indefinitely for a newline.</summary>
public static class ActivationMessageReader
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static async Task<string?> ReadAsync(Stream stream, int maxBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        using var message = new MemoryStream();
        var buffer = new byte[Math.Min(256, maxBytes)];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            var newline = Array.IndexOf(buffer, (byte)'\n', 0, read);
            var count = newline >= 0 ? newline : read;
            if (message.Length + count > maxBytes) throw new InvalidDataException("Activation command exceeds its size limit.");
            message.Write(buffer, 0, count);
            if (newline >= 0) break;
        }
        if (message.Length == 0) return null;
        return StrictUtf8.GetString(message.GetBuffer(), 0, checked((int)message.Length)).TrimEnd('\r');
    }
}
